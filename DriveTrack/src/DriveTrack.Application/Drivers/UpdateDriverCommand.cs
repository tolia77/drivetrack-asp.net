using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Drivers;

namespace DriveTrack.Application.Drivers;

/// <summary>
/// What a dispatcher sends to change a driver (FR-37, FR-38).
/// <para>
/// AD-23: every field is <see cref="Optional{T}"/>, and <see cref="VehicleId"/> is the reason the
/// wrapper exists. Absent leaves the assignment alone; present-and-null clears it. The original
/// system could express only the first, so an assigned vehicle could never be released and was
/// therefore permanently undeletable — the defect FR-38 names.
/// </para>
/// <para>
/// FR-37 names three things a dispatcher may change: the driver's name, their licence number and
/// their vehicle. The name lives on the account rather than on the driver row, which is why
/// <see cref="MergedOnto"/> takes both — the merge has to read each field from whichever row holds
/// it. The address, the password and the role are deliberately absent: those stay the account
/// capability's, and AD-24 keeps this capability's reach into Identity to the three calls FR-35,
/// FR-37 and FR-39 actually need.
/// </para>
/// </summary>
/// <param name="FirstName">Given name, held on the account.</param>
/// <param name="LastName">Family name, held on the account.</param>
/// <param name="LicenseNumber">The driving licence number.</param>
/// <param name="VehicleId">The vehicle to hold. Absent leaves it alone, null releases it (FR-38).</param>
public sealed record UpdateDriverCommand(
    Optional<string> FirstName,
    Optional<string> LastName,
    Optional<string> LicenseNumber,
    Optional<int?> VehicleId)
{
    /// <summary>
    /// The command with every absent field filled in from the rows it will be applied to: the state
    /// the driver will hold afterwards.
    /// <para>
    /// AD-23 makes this the thing the validator reads. Validating the payload instead would refuse
    /// an update that only releases a vehicle, because it carries no licence number and no name.
    /// </para>
    /// </summary>
    /// <param name="driver">The stored driver row, which holds the licence number and the assignment.</param>
    /// <param name="account">The stored account, which holds the name.</param>
    internal UpdateDriverCommand MergedOnto(Driver driver, UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(account);

        return new UpdateDriverCommand(
            Optional<string>.Present(FirstName.Or(account.FirstName)),
            Optional<string>.Present(LastName.Or(account.LastName)),
            Optional<string>.Present(LicenseNumber.Or(driver.LicenseNumber)),
            Optional<int?>.Present(VehicleId.Or(driver.VehicleId)));
    }
}
