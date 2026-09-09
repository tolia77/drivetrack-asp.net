namespace DriveTrack.Application.Vehicles;

/// <summary>
/// The fleet capability (FR-40 to FR-45). It owns <c>Vehicle</c> and, by AD-24, the driver–vehicle
/// assignment — which is why the unassigned list lives here rather than on the driver capability.
/// <para>
/// Every method takes an authorization decision through <see cref="Authorization.IAccessGuard"/>,
/// inline in its own body: nothing here is in <see cref="Authorization.PublicEntryPoints"/>, and a
/// guard call reached only from a private helper does not count.
/// </para>
/// </summary>
public interface IVehicleService
{
    /// <summary>One page of the fleet (FR-40, NFR-27).</summary>
    /// <exception cref="Common.ValidationException">The paging arguments are out of range.</exception>
    Task<IReadOnlyList<VehicleSummary>> ListAsync(
        ListVehiclesQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every vehicle no driver holds (FR-45). Unpaged: it feeds an assignment control, and a
    /// truncated set of choices is a wrong set of choices.
    /// </summary>
    Task<IReadOnlyList<VehicleSummary>> ListUnassignedAsync(CancellationToken cancellationToken);

    /// <summary>One vehicle.</summary>
    /// <exception cref="Common.NotFoundException">No vehicle has that id.</exception>
    Task<VehicleSummary> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>Puts a vehicle on the fleet (FR-40).</summary>
    /// <exception cref="Common.ConflictException">Another vehicle already carries that plate (FR-41).</exception>
    Task<VehicleSummary> CreateAsync(CreateVehicleCommand command, CancellationToken cancellationToken);

    /// <summary>Changes a vehicle (FR-42).</summary>
    /// <exception cref="Common.NotFoundException">No vehicle has that id.</exception>
    /// <exception cref="Common.ConflictException">Another vehicle already carries that plate (FR-41).</exception>
    Task<VehicleSummary> UpdateAsync(
        int id,
        UpdateVehicleCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes a vehicle off the fleet (FR-43).
    /// </summary>
    /// <exception cref="Common.NotFoundException">No vehicle has that id.</exception>
    /// <exception cref="Common.ConflictException">
    /// A driver holds it. The original system made this state permanent — it had no way to clear an
    /// assignment — so the refusal names the way out rather than being a dead end.
    /// </exception>
    Task DeleteAsync(int id, CancellationToken cancellationToken);
}
