using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Vehicles;
using FluentValidation;

namespace DriveTrack.Application.Vehicles;

/// <summary>
/// AD-3's pipeline over the fleet: load resource → guard → validate → act → commit → map.
/// <para>
/// AD-2: every method calls <see cref="IAccessGuard"/> through the interface, inline in its own
/// body. Folding the call into a private helper would read better and would not count — the
/// coverage gate walks each public method's IL and does not follow a call it makes.
/// </para>
/// <para>
/// "Dispatcher or admin" is written <c>RequireRole(UserRole.Dispatcher)</c> throughout: AD-4 makes
/// admin satisfy every check by rule inside the guard, so naming it here would be a second place
/// the override lived.
/// </para>
/// </summary>
public sealed class VehicleService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    IValidator<ListVehiclesQuery> listValidator,
    IValidator<CreateVehicleCommand> createValidator,
    IValidator<UpdateVehicleCommand> updateValidator) : IVehicleService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<VehicleSummary>> ListAsync(
        ListVehiclesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        accessGuard.RequireRole(UserRole.Dispatcher);

        // NFR-27: refused before any query is built, which is the half the original was missing.
        await ValidatorExtensions.ValidateAndThrowAsync(listValidator, query, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var vehicles = await unitOfWork.Vehicles.ListAsync(
            query.Offset,
            query.Limit,
            cancellationToken);

        // Nothing was written, so the scope is disposed without a commit and the empty transaction
        // rolls back. That is the ordinary read path, not an omission.
        return [.. vehicles.Select(Summary)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VehicleSummary>> ListUnassignedAsync(CancellationToken cancellationToken)
    {
        accessGuard.RequireRole(UserRole.Dispatcher);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var vehicles = await unitOfWork.Vehicles.ListUnassignedAsync(cancellationToken);

        return [.. vehicles.Select(Summary)];
    }

    /// <inheritdoc />
    public async Task<VehicleSummary> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // AD-3: load, then guard. The row is read before the decision so a future rule can be about
        // the row; nothing about it is disclosed unless the guard passes.
        var vehicle = await unitOfWork.Vehicles.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireRole(UserRole.Dispatcher);

        return Summary(vehicle ?? throw NotFound(id));
    }

    /// <inheritdoc />
    public async Task<VehicleSummary> CreateAsync(
        CreateVehicleCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A create loads nothing, so the guard is the first step rather than the second.
        accessGuard.RequireRole(UserRole.Dispatcher);

        await ValidatorExtensions.ValidateAndThrowAsync(createValidator, command, cancellationToken);

        var plate = NormalizePlate(command.LicensePlate!);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        await RefuseDuplicatePlateAsync(unitOfWork, plate, keeping: null, cancellationToken);

        var vehicle = new Vehicle
        {
            Model = command.Model!.Trim(),
            LicensePlate = plate,
            CapacityKg = command.CapacityKg,
            Mileage = command.Mileage,
            NextMaintenanceDate = command.NextMaintenanceDate,
        };

        unitOfWork.Vehicles.Add(vehicle);

        await unitOfWork.CommitAsync(cancellationToken);

        return Summary(vehicle);
    }

    /// <inheritdoc />
    public async Task<VehicleSummary> UpdateAsync(
        int id,
        UpdateVehicleCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var vehicle = await unitOfWork.Vehicles.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireRole(UserRole.Dispatcher);

        if (vehicle is null)
        {
            throw NotFound(id);
        }

        // AD-23: the validator reads the state the row will hold, not the payload. An update that
        // mentions only the capacity must not be refused for a model it never sent.
        var merged = command.MergedOnto(vehicle);

        await ValidatorExtensions.ValidateAndThrowAsync(updateValidator, merged, cancellationToken);

        var plate = NormalizePlate(merged.LicensePlate.Value!);

        await RefuseDuplicatePlateAsync(unitOfWork, plate, keeping: vehicle.Id, cancellationToken);

        // FR-103's third mover, and the one that is easiest to miss: the invariant is usually
        // broken by changing the delivery or by handing the driver another vehicle, but lowering
        // the capacity of the vehicle they already hold reaches the same forbidden state without
        // either row being touched. The rule is the Deliveries capability's (AD-24), so this asks
        // rather than restates it, and it runs inside this scope so a refusal rolls the whole edit
        // back. Only when the figure actually changed: re-submitting an unchanged form is the
        // ordinary case, and a query per save would be a cost for nothing.
        if (merged.CapacityKg.Value != vehicle.CapacityKg
            && await unitOfWork.Vehicles.FindHolderAsync(vehicle.Id, cancellationToken) is { } holder)
        {
            await DeliveryCapacity.EnsureDriverStillFitsAsync(
                unitOfWork,
                holder,
                merged.CapacityKg.Value,
                cancellationToken);
        }

        vehicle.Model = merged.Model.Value!.Trim();
        vehicle.LicensePlate = plate;
        vehicle.CapacityKg = merged.CapacityKg.Value;
        vehicle.Mileage = merged.Mileage.Value;

        // The merged value, so an absent date leaves the row's own and a present null clears it -
        // the one field on a vehicle where "no value" is a state a dispatcher can choose.
        vehicle.NextMaintenanceDate = merged.NextMaintenanceDate.Value;

        await unitOfWork.CommitAsync(cancellationToken);

        return Summary(vehicle);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var vehicle = await unitOfWork.Vehicles.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireRole(UserRole.Dispatcher);

        if (vehicle is null)
        {
            throw NotFound(id);
        }

        // FR-43. The schema's SetNull would happily strand a driver holding nothing, which is the
        // silent data loss this refusal exists to prevent - and, unlike the original, the caller now
        // has a way out: FR-38's clear makes the vehicle deletable.
        var holder = await unitOfWork.Vehicles.FindHolderAsync(vehicle.Id, cancellationToken);

        if (holder is { } driverId)
        {
            throw new ConflictException(
                ErrorCode.FLEET_VEHICLE_IN_USE,
                "Vehicle " + vehicle.Id.ToString(CultureInfo.InvariantCulture)
                    + " is held by driver "
                    + driverId.Value.ToString(CultureInfo.InvariantCulture)
                    + " and cannot be deleted until that driver is unassigned.");
        }

        unitOfWork.Vehicles.Remove(vehicle);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// A licence plate as it is stored: trimmed, and upper-cased because a plate is a
    /// case-insensitive identifier — <c>AA1234BB</c> and <c>aa1234bb</c> are one vehicle.
    /// <para>
    /// Normalizing on write is what lets the case-sensitive unique index the schema already
    /// declares carry that rule, with no migration and no functional index. Storing the plate as
    /// typed and comparing case-insensitively instead would leave the index enforcing something
    /// narrower than the check promises: two concurrent creates differing only in case would both
    /// pass the check and both commit, and FR-41 would be broken by exactly the race the index is
    /// there to lose gracefully.
    /// </para>
    /// </summary>
    private static string NormalizePlate(string licensePlate) =>
        licensePlate.Trim().ToUpperInvariant();

    /// <summary>
    /// FR-41's friendly answer, asked against the normalized plate. The unique index on the column
    /// is the race backstop and surfaces as <c>PERSISTENCE_UNIQUE_VIOLATION</c>, which is also a
    /// 409 — so two dispatchers registering the same plate at the same instant get the same status
    /// either way (NFR-2), and because both go through <see cref="NormalizePlate"/> the index sees
    /// the same equality this check does.
    /// </summary>
    /// <param name="keeping">
    /// The vehicle the plate is allowed to belong to already, so an edit that leaves the plate
    /// alone is not a conflict with itself.
    /// </param>
    private static async Task RefuseDuplicatePlateAsync(
        IUnitOfWork unitOfWork,
        string licensePlate,
        int? keeping,
        CancellationToken cancellationToken)
    {
        var holder = await unitOfWork.Vehicles.FindByLicensePlateAsync(licensePlate, cancellationToken);

        if (holder is null || holder.Id == keeping)
        {
            return;
        }

        throw new ConflictException(
            ErrorCode.FLEET_LICENSE_PLATE_IN_USE,
            "Licence plate '" + licensePlate + "' is already carried by vehicle "
                + holder.Id.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private static NotFoundException NotFound(int id) =>
        new(
            ErrorCode.COMMON_NOT_FOUND,
            "No vehicle exists with id " + id.ToString(CultureInfo.InvariantCulture) + ".");

    private static VehicleSummary Summary(Vehicle vehicle) =>
        new(
            vehicle.Id,
            vehicle.Model,
            vehicle.LicensePlate,
            vehicle.CapacityKg,
            vehicle.Mileage,
            vehicle.NextMaintenanceDate);
}
