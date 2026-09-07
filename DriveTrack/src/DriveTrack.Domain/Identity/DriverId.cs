namespace DriveTrack.Domain.Identity;

/// <summary>
/// The identity of a driver row — deliberately not the identity of the driver's user.
/// <para>
/// A driver row id and a user id are different numbers (see the surrogate key on
/// <c>Driver</c>), which is what makes AD-22's compile-time separation load-bearing rather
/// than decorative.
/// </para>
/// </summary>
/// <param name="Value">The underlying surrogate key.</param>
public readonly record struct DriverId(int Value);
