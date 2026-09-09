using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Vehicles;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="Vehicle"/>. See <see cref="IClientRepository"/> for the shape rules (AD-6).
/// <para>
/// AD-24 gives the driver–vehicle assignment to the Vehicles capability, so the two questions about
/// <c>drivers.vehicle_id</c> — which vehicles nobody holds, and who holds this one — are asked here
/// rather than on <see cref="IDriverRepository"/>, even though the column lives on the driver row.
/// </para>
/// </summary>
public interface IVehicleRepository
{
    /// <summary>Loads a vehicle, or null when there is none with that id.</summary>
    Task<Vehicle?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the fleet, oldest first (FR-40, NFR-27).
    /// </summary>
    /// <param name="offset">Rows to skip. The caller has already validated it.</param>
    /// <param name="limit">Rows to take. The caller has already validated it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Vehicle>> ListAsync(int offset, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Every vehicle no driver holds (FR-45). Unpaged, exactly as the original's
    /// <c>GET /vehicles/unassigned</c> was: it feeds an assignment control, which needs the whole
    /// set to be a correct choice.
    /// </summary>
    Task<IReadOnlyList<Vehicle>> ListUnassignedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads the vehicle carrying that licence plate, or null when no vehicle does (FR-41). The
    /// plate is compared as stored, so the caller passes the normalized form — the same one it
    /// would write — and the unique index on the column answers the same question.
    /// <para>
    /// The friendly half of the uniqueness rule, mirroring
    /// <see cref="IUserAccountRepository.EmailExistsAsync"/>: the unique index on the column is the
    /// race backstop and answers the same 409 through <c>PERSISTENCE_UNIQUE_VIOLATION</c>.
    /// It returns the row rather than a boolean because an update has to be able to tell "another
    /// vehicle holds this plate" from "this vehicle already holds it".
    /// </para>
    /// </summary>
    Task<Vehicle?> FindByLicensePlateAsync(string licensePlate, CancellationToken cancellationToken);

    /// <summary>
    /// The driver currently holding that vehicle, or null when none does.
    /// <para>
    /// One method for two rules: FR-43 refuses to delete a held vehicle, and FR-44 refuses to
    /// assign one another driver already holds. Both ask the same question, so both ask it here.
    /// </para>
    /// </summary>
    Task<DriverId?> FindHolderAsync(int vehicleId, CancellationToken cancellationToken);

    /// <summary>Stages a new vehicle for the next commit.</summary>
    void Add(Vehicle vehicle);

    /// <summary>Stages a vehicle for deletion (FR-43).</summary>
    void Remove(Vehicle vehicle);
}
