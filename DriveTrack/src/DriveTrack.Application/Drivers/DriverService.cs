using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Reviews;
using DriveTrack.Application.Shifts;
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
/// <para>
/// AD-24 in the other direction, added by story 7.2: a driver's rating is derived from reviews, and
/// reviews belong to the Reviews capability — so this service asks <see cref="IReviewService"/> for
/// it and never reads <c>unitOfWork.Reviews</c>. The ask is batched: a roster of forty drivers is
/// one question about forty driver rows, not forty questions.
/// </para>
/// <para>
/// Story 4.2 adds a second port of the same shape and for the same reason: FR-116's on-duty flag is
/// derived from shifts, so it is <see cref="IShiftService"/>'s answer rather than a read of
/// <c>unitOfWork.Shifts</c>. That capability answers with driver row ids and asks this one for
/// nothing in return, which is what keeps the two directions from closing a constructor cycle — it
/// reads a driver's <em>name</em> off the Identity roster, as this service does.
/// </para>
/// <para>
/// Every read that needs a rating closes its persistence scope before asking for one. The other
/// capability opens a scope of its own, and two transactions held open across one screen's read is
/// a cost with nothing to buy: the rating does not depend on the rows just read, and nothing here
/// writes.
/// </para>
/// </summary>
public sealed class DriverService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    IReviewService reviews,
    IShiftService shifts,
    IValidator<CreateDriverCommand> createValidator,
    IValidator<UpdateDriverCommand> updateValidator) : IDriverService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DriverSummary>> ListAsync(CancellationToken cancellationToken)
    {
        accessGuard.RequireRole(UserRole.Dispatcher);

        var rows = new List<(Driver Driver, UserAccount Account)>();

        // An explicit block rather than a method-wide `await using`: the rating below is another
        // capability's answer and opens a scope of its own, so this one is closed first. Nothing
        // was written, so it is disposed without a commit and the empty transaction rolls back -
        // the ordinary read path, not an omission.
        await using (var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken))
        {
            foreach (var driver in await unitOfWork.Drivers.ListAsync(cancellationToken))
            {
                // The account is read per driver rather than joined: Domain declares no
                // relationship between Driver and the Identity user - it cannot, without naming an
                // Infrastructure type - so the identity fields are only reachable through the
                // Identity port. The roster is a small, unpaged set by requirement (the original
                // returned every row), so the cost is a handful of keyed reads rather than a scan.
                rows.Add((driver, await AccountAsync(unitOfWork, driver, cancellationToken)));
            }
        }

        // FR-98, batched: one question about every driver on the roster. Asking per row would make
        // a forty driver roster forty queries for a column that is the same one grouped answer.
        var ratings = await RatingsAsync([.. rows.Select(row => row.Driver.Id)], cancellationToken);

        // FR-116, asked the same way and for the same reason: one question about everybody on duty
        // rather than one per row. The answer is the whole on-duty set rather than a set narrowed to
        // this page, because "is this driver on duty" is a single indexed predicate and narrowing it
        // would cost a parameter list to save nothing.
        var onDuty = await OnDutyAsync(cancellationToken);

        return
        [
            .. rows.Select(row => Summary(
                row.Driver,
                row.Account,
                Rating(ratings, row.Driver.Id),
                onDuty.Contains(row.Driver.Id))),
        ];
    }

    /// <inheritdoc />
    public async Task<DriverSummary> GetAsync(DriverId id, CancellationToken cancellationToken)
    {
        Driver driver;
        UserAccount account;

        await using (var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken))
        {
            // AD-3: load, then guard. Nothing about the row is disclosed unless the guard passes.
            var found = await unitOfWork.Drivers.GetByIdAsync(id, cancellationToken);

            accessGuard.RequireRole(UserRole.Dispatcher);

            driver = found ?? throw NotFound(id);
            account = await AccountAsync(unitOfWork, driver, cancellationToken);
        }

        var ratings = await RatingsAsync([driver.Id], cancellationToken);
        var onDuty = await OnDutyAsync(cancellationToken);

        return Summary(driver, account, Rating(ratings, driver.Id), onDuty.Contains(driver.Id));
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

        // No rating, and no lookup for one: a driver taken on a moment ago has carried nothing, so
        // there is no delivery for anybody to have reviewed. Null is the honest answer rather than
        // a query whose result is known (FR-98). Off duty for the same reason: nobody can have gone
        // on duty as a driver who did not exist a moment ago (FR-116).
        return Summary(driver, account, rating: null, onDuty: false);
    }

    /// <inheritdoc />
    public async Task<DriverSummary> UpdateAsync(
        DriverId id,
        UpdateDriverCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Driver driver;
        UserAccount written;

        // An explicit block rather than a method-wide `await using`, for the reason ListAsync uses
        // one: the rating below is another capability's answer and opens a scope of its own, and
        // this one has nothing left to do once it has committed.
        await using (var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken))
        {
            var found = await unitOfWork.Drivers.GetByIdAsync(id, cancellationToken);

            accessGuard.RequireRole(UserRole.Dispatcher);

            driver = found ?? throw NotFound(id);

            // The account is loaded before the merge rather than only for the summary: FR-37's name
            // lives there, so it is half of the state the validator has to judge.
            var account = await unitOfWork.Users.GetByIdAsync(driver.UserId, cancellationToken)
                ?? throw Orphaned(driver);

            // AD-23: the validator reads the state the driver will hold, not the payload.
            var merged = command.MergedOnto(driver, account);

            await ValidatorExtensions.ValidateAndThrowAsync(updateValidator, merged, cancellationToken);

            var firstName = merged.FirstName.Value!.Trim();
            var lastName = merged.LastName.Value!.Trim();

            // Only when something actually changed. An edit that leaves the name alone must not
            // write to the account at all - the narrower the reach into Identity, the easier AD-24
            // is to read.
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

            // Built from the state just written rather than re-read: the rename was staged against
            // the same change tracker, so a second read would only be asking the database to
            // confirm what this scope already knows.
            written = account with { FirstName = firstName, LastName = lastName };
        }

        // A driver's standing is not touched by an edit to their licence or their vehicle, but the
        // payload still has to carry the one they actually hold: a null here would read as "nobody
        // has reviewed them", which is a different claim (FR-98).
        var ratings = await RatingsAsync([driver.Id], cancellationToken);

        // Nor is their duty state touched by an edit to their licence, and the payload still has to
        // carry the one they actually hold: a false here would read as "off duty", which is a claim
        // about the world rather than about the edit (FR-116).
        var onDuty = await OnDutyAsync(cancellationToken);

        return Summary(driver, written, Rating(ratings, driver.Id), onDuty.Contains(driver.Id));
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

    /// <summary>The account behind a loaded driver, which is where their identity fields live.</summary>
    private static async Task<UserAccount> AccountAsync(
        IUnitOfWork unitOfWork,
        Driver driver,
        CancellationToken cancellationToken) =>
        await unitOfWork.Users.GetByIdAsync(driver.UserId, cancellationToken)
            ?? throw Orphaned(driver);

    /// <summary>
    /// FR-98's aggregate for a set of drivers, asked of the capability that owns reviews (AD-24)
    /// and keyed so a mapping is a lookup rather than a scan.
    /// </summary>
    private async Task<Dictionary<DriverId, DriverRating>> RatingsAsync(
        IReadOnlyCollection<DriverId> driverIds,
        CancellationToken cancellationToken) =>
        (await reviews.ListDriverRatingsAsync(driverIds, cancellationToken))
            .ToDictionary(rating => rating.DriverId);

    /// <summary>
    /// One driver's standing, or null when nobody has reviewed a delivery of theirs.
    /// <para>
    /// Absence stays absence. Answering a zero here would be answering the worst rating the scale
    /// has to a question nobody asked, and it would sort an unrated driver below every rated one.
    /// </para>
    /// </summary>
    private static DriverRating? Rating(
        IReadOnlyDictionary<DriverId, DriverRating> ratings,
        DriverId driverId) =>
        ratings.TryGetValue(driverId, out var rating) ? rating : null;

    /// <summary>
    /// FR-116's on-duty set, asked of the capability that owns shifts (AD-24) and kept as a set so a
    /// roster's mapping is a lookup rather than a scan.
    /// </summary>
    private async Task<HashSet<DriverId>> OnDutyAsync(CancellationToken cancellationToken) =>
        [.. await shifts.ListOnDutyDriverIdsAsync(cancellationToken)];

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

    private static DriverSummary Summary(
        Driver driver,
        UserAccount account,
        DriverRating? rating,
        bool onDuty) =>
        new(
            driver.Id,
            account.Id,
            account.FirstName,
            account.LastName,
            account.Email,
            driver.LicenseNumber,
            driver.VehicleId,
            driver.Vehicle?.Model,
            driver.Vehicle?.LicensePlate,

            // Null and zero together, or neither: an average of nothing is not a number, and the
            // two fields must never disagree about whether anybody has said anything (FR-98).
            rating?.Average,
            rating?.ReviewCount ?? 0,

            // FR-116's flag, and never a filter: the picker on the delivery form reads this to mark
            // an off-duty driver, and still offers them.
            onDuty);
}
