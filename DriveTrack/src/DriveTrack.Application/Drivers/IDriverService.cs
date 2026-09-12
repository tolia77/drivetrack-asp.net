using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Drivers;

/// <summary>
/// The driver capability (FR-35 to FR-39). It owns <c>Driver</c> and reaches the driver–vehicle
/// assignment only through the one writer AD-24 puts in the Vehicles capability.
/// <para>
/// Every method takes an authorization decision through <see cref="Authorization.IAccessGuard"/>,
/// inline in its own body: nothing here is in <see cref="Authorization.PublicEntryPoints"/>.
/// </para>
/// <para>
/// Since story 7.2 a <see cref="DriverSummary"/> also carries FR-98's rating. It is read through
/// <c>IReviewService</c> rather than computed here, because it is derived from reviews and reviews
/// belong to another capability (AD-24) — and it is derived on every read rather than stored on the
/// driver row (DR-18), so a review written, edited or deleted a moment ago is already reflected.
/// A driver nobody has reviewed carries <c>null</c>, never a zero.
/// </para>
/// </summary>
public interface IDriverService
{
    /// <summary>
    /// Every driver, each with the vehicle they hold (FR-35). Unpaged, as the original was; FR-36's
    /// search runs in the browser over this list, because PRD section 8 excludes server-side
    /// filtering.
    /// </summary>
    Task<IReadOnlyList<DriverSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One driver.</summary>
    /// <exception cref="Common.NotFoundException">No driver has that id.</exception>
    Task<DriverSummary> GetAsync(DriverId id, CancellationToken cancellationToken);

    /// <summary>
    /// Takes on a driver: the account, the driver row and the assignment, in one transaction (FR-35).
    /// </summary>
    /// <exception cref="Common.ConflictException">
    /// The email is already in use, or another driver already holds the named vehicle (FR-44).
    /// Either way nothing is written — an account created beside a refused assignment is exactly the
    /// half-written state AD-5 exists to prevent.
    /// </exception>
    Task<DriverSummary> CreateAsync(CreateDriverCommand command, CancellationToken cancellationToken);

    /// <summary>Changes a driver's licence number, their assignment, or both (FR-37, FR-38).</summary>
    /// <exception cref="Common.NotFoundException">No driver has that id, or no vehicle has the named id.</exception>
    /// <exception cref="Common.ConflictException">Another driver already holds the named vehicle (FR-44).</exception>
    Task<DriverSummary> UpdateAsync(
        DriverId id,
        UpdateDriverCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a driver and the account behind them (FR-39).
    /// <para>
    /// Never an error for a driver holding active deliveries: FR-39 takes the "leaves them
    /// unassigned" arm, which the declared <c>Driver → Delivery</c> SetNull already guarantees. The
    /// original answered a raw database error here.
    /// </para>
    /// </summary>
    /// <exception cref="Common.NotFoundException">No driver has that id.</exception>
    Task DeleteAsync(DriverId id, CancellationToken cancellationToken);
}
