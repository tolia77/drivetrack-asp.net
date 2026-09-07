namespace DriveTrack.Domain.Identity;

/// <summary>
/// The identity of an application user row.
/// <para>
/// AD-22: <see cref="UserId"/>, <see cref="DriverId"/> and <see cref="ClientId"/> are three
/// distinct types over <see cref="int"/> with no conversion between them, so
/// <c>entry.ActorUserId == caller.DriverId</c> does not compile. That is the whole point:
/// the original system typed all three as <c>int</c>, which let an ownership check compare
/// the wrong number and silently authorize the wrong caller.
/// </para>
/// </summary>
/// <param name="Value">The underlying surrogate key.</param>
public readonly record struct UserId(int Value);
