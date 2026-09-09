using DriveTrack.Application.Drivers;
using DriveTrack.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// The driver roster over REST (FR-35 to FR-39).
/// <para>
/// <c>[Authorize]</c> is the same convenience it is on <see cref="VehiclesController"/>: the
/// decision is <c>IAccessGuard.RequireRole</c> inside <see cref="IDriverService"/>, which is why a
/// signed-in client calling any of these is refused by the guard rather than by an attribute.
/// </para>
/// </summary>
[ApiController]
[Route("api/drivers")]
[Authorize]
public sealed class DriversController(IDriverService drivers) : ControllerBase
{
    /// <summary>Every driver, each with the vehicle they hold (FR-35). Unpaged, as the original was.</summary>
    [HttpGet]
    public Task<IReadOnlyList<DriverSummary>> ListAsync(CancellationToken cancellationToken) =>
        drivers.ListAsync(cancellationToken);

    /// <summary>One driver.</summary>
    [HttpGet("{id:int}")]
    public Task<DriverSummary> GetAsync(int id, CancellationToken cancellationToken) =>
        drivers.GetAsync(new DriverId(id), cancellationToken);

    /// <summary>Takes on a driver, their account and their first assignment in one transaction (FR-35).</summary>
    [HttpPost]
    public Task<DriverSummary> CreateAsync(
        [FromBody] CreateDriverCommand command,
        CancellationToken cancellationToken) =>
        drivers.CreateAsync(command, cancellationToken);

    /// <summary>
    /// Changes a driver (FR-37, FR-38). An absent <c>vehicleId</c> leaves the assignment alone; an
    /// explicit null releases it.
    /// </summary>
    [HttpPut("{id:int}")]
    public Task<DriverSummary> UpdateAsync(
        int id,
        [FromBody] UpdateDriverCommand command,
        CancellationToken cancellationToken) =>
        drivers.UpdateAsync(new DriverId(id), command, cancellationToken);

    /// <summary>Removes a driver and the account behind them (FR-39).</summary>
    [HttpDelete("{id:int}")]
    public Task DeleteAsync(int id, CancellationToken cancellationToken) =>
        drivers.DeleteAsync(new DriverId(id), cancellationToken);
}
