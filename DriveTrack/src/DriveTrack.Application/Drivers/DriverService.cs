using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Vehicles;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using FluentValidation;

namespace DriveTrack.Application.Drivers;

/// <summary>
/// AD-3's pipeline over the driver roster: load resource → guard → validate → act → commit → map.
/// <para>
/// AD-2: every method calls <see cref="IAccessGuard"/> through the interface, inline in its own
/// body. "Dispatcher or admin" is written <c>RequireRole(UserRole.Dispatcher)</c>, because AD-4
/// makes admin satisfy every check by rule inside the guard.
/// </para>
/// <para>
/// AD-24, and the tension worth naming: <c>ApplicationUser</c> belongs to Identity, yet FR-35
/// creates a driver's account, FR-37 renames it and FR-39 deletes it. This service therefore
/// reaches <c>unitOfWork.Users</c> for exactly those three calls, mirroring
/// <c>UserService.RegisterClientAsync</c>, which creates the Identity-owned <c>Client</c> row the
/// same way. The boundary is what is <em>not</em> in that list: the address, the password and the
/// role stay the account capability's, and each of the three calls is a named port method rather
/// than a general "update the user" the rule could erode through. It never touches
/// <c>unitOfWork.Vehicles</c> — the assignment goes through <see cref="VehicleAssignment"/>, the
/// one writer of <c>drivers.vehicle_id</c>.
/// </para>
/// </summary>
public sealed class DriverService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    IValidator<CreateDriverCommand> createValidator,
    IValidator<UpdateDriverCommand> updateValidator) : IDriverService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DriverSummary>> ListAsync(CancellationToken cancellationToken)
    {
        accessGuard.RequireRole(UserRole.Dispatcher);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var drivers = await unitOfWork.Drivers.ListAsync(cancellationToken);
        var summaries = new List<DriverSummary>(drivers.Count);

        foreach (var driver in drivers)
        {
            // The account is read per driver rather than joined: Domain declares no relationship
            // between Driver and the Identity user - it cannot, without naming an Infrastructure
            // type - so the identity fields are only reachable through the Identity port. The
            // roster is a small, unpaged set by requirement (the original returned every row), so
            // the cost is a handful of keyed reads rather than a scan.
            summaries.Add(await SummaryAsync(unitOfWork, driver, cancellationToken));
        }

        // Nothing was written, so the scope is disposed without a commit and the empty transaction
        // rolls back. That is the ordinary read path, not an omission.
        return summaries;
    }

    /// <inheritdoc />
    public async Task<DriverSummary> GetAsync(DriverId id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // AD-3: load, then guard. Nothing about the row is disclosed unless the guard passes.
        var driver = await unitOfWork.Drivers.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireRole(UserRole.Dispatcher);

        if (driver is null)
        {
            throw NotFound(id);
        }

        return await SummaryAsync(unitOfWork, driver, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DriverSummary> CreateAsync(
        CreateDriverCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A create loads nothing, so the guard is the first step rather than the second.
        accessGuard.RequireRole(UserRole.Dispatcher);

        await ValidatorExtensions.ValidateAndThrowAsync(createValidator, command, cancellationToken);

        // The validator has proved these are present; the nullable annotations exist because the
        // wire can send anything and the command has to be able to carry it as far as here.
        var email = command.Email!.Trim();

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // The friendly answer. The normalized-email unique index behind it is the race backstop and
        // surfaces as PERSISTENCE_UNIQUE_VIOLATION, which is also a 409 (NFR-2).
        if (await unitOfWork.Users.EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException(
                ErrorCode.AUTH_EMAIL_ALREADY_IN_USE,
                "An account already exists for the submitted email address.");
        }

        var account = await unitOfWork.Users.CreateAsync(
            new NewUserAccount(command.FirstName!.Trim(), command.LastName!.Trim(), email),
            command.Password!,
            UserRole.Driver,
            cancellationToken);

        var driver = new Driver
        {
            UserId = account.Id,
            LicenseNumber = command.LicenseNumber!.Trim(),
        };

        unitOfWork.Drivers.Add(driver);

        // AD-24: the assignment is written here and only here. It runs inside this scope, so a
        // vehicle another driver already holds refuses the whole operation and the account created
        // moments ago is rolled back with it - the half-written state NFR-9 forbids.
        await VehicleAssignment.ApplyAsync(unitOfWork, driver, command.VehicleId, cancellationToken);

        // AD-5: the user row, its role row, the driver row and the assignment reach the database
        // here or nowhere.
        await unitOfWork.CommitAsync(cancellationToken);

        return Summary(driver, account);
    }

    /// <inheritdoc />
    public async Task<DriverSummary> UpdateAsync(
        DriverId id,
        UpdateDriverCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var driver = await unitOfWork.Drivers.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireRole(UserRole.Dispatcher);

        if (driver is null)
        {
            throw NotFound(id);
        }

        // The account is loaded before the merge rather than only for the summary: FR-37's name
        // lives there, so it is half of the state the validator has to judge.
        var account = await unitOfWork.Users.GetByIdAsync(driver.UserId, cancellationToken)
            ?? throw Orphaned(driver);

        // AD-23: the validator reads the state the driver will hold, not the payload.
        var merged = command.MergedOnto(driver, account);

        await ValidatorExtensions.ValidateAndThrowAsync(updateValidator, merged, cancellationToken);

        var firstName = merged.FirstName.Value!.Trim();
        var lastName = merged.LastName.Value!.Trim();

        // Only when something actually changed. An edit that leaves the name alone must not write
        // to the account at all - the narrower the reach into Identity, the easier AD-24 is to read.
        if (!string.Equals(firstName, account.FirstName, StringComparison.Ordinal)
            || !string.Equals(lastName, account.LastName, StringComparison.Ordinal))
        {
            await unitOfWork.Users.RenameAsync(driver.UserId, firstName, lastName, cancellationToken);
        }

        driver.LicenseNumber = merged.LicenseNumber.Value!.Trim();

        // The merged value, so an absent VehicleId re-applies what the driver already holds - a
        // no-op inside the writer - and a present null clears it (FR-38).
        await VehicleAssignment.ApplyAsync(
            unitOfWork,
            driver,
            merged.VehicleId.Value,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // Built from the state just written rather than re-read: the rename was staged against the
        // same change tracker, so a second read would only be asking the database to confirm what
        // this scope already knows.
        return Summary(driver, account with { FirstName = firstName, LastName = lastName });
    }

    /// <inheritdoc />
    public async Task DeleteAsync(DriverId id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var driver = await unitOfWork.Drivers.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireRole(UserRole.Dispatcher);

        if (driver is null)
        {
            throw NotFound(id);
        }

        // The account, not only the driver row. Removing the row alone would leave an account
        // holding role Driver with nothing behind it: signable-in, carrying a null driver claim.
        // The declared cascades do the rest - the driver row, its shifts and its chat messages go
        // with the account, and its deliveries are left with a null driver, which is FR-39's
        // "leaves those deliveries unassigned" arm rather than an error.
        await unitOfWork.Users.DeleteAsync(driver.UserId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    private static NotFoundException NotFound(DriverId id) =>
        new(
            ErrorCode.COMMON_NOT_FOUND,
            "No driver exists with id " + id.Value.ToString(CultureInfo.InvariantCulture) + ".");

    /// <summary>The summary for a loaded driver, reading the account behind them.</summary>
    private static async Task<DriverSummary> SummaryAsync(
        IUnitOfWork unitOfWork,
        Driver driver,
        CancellationToken cancellationToken)
    {
        var account = await unitOfWork.Users.GetByIdAsync(driver.UserId, cancellationToken)
            ?? throw Orphaned(driver);

        return Summary(driver, account);
    }

    /// <summary>
    /// A driver row whose account is gone breaks the foreign key the schema declares, so it is a
    /// defect rather than a caller error and leaves as the 500 envelope instead of being smoothed
    /// over into a plausible-looking row.
    /// </summary>
    private static InvalidOperationException Orphaned(Driver driver) =>
        new(
            "Driver " + driver.Id.Value.ToString(CultureInfo.InvariantCulture)
                + " references user "
                + driver.UserId.Value.ToString(CultureInfo.InvariantCulture)
                + ", which does not exist.");

    private static DriverSummary Summary(Driver driver, UserAccount account) =>
        new(
            driver.Id,
            account.Id,
            account.FirstName,
            account.LastName,
            account.Email,
            driver.LicenseNumber,
            driver.VehicleId,
            driver.Vehicle?.Model,
            driver.Vehicle?.LicensePlate);
}
