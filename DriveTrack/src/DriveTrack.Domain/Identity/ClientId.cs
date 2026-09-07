namespace DriveTrack.Domain.Identity;

/// <summary>
/// The identity of a client row — deliberately not the identity of the client's user.
/// See <see cref="UserId"/> for why the three identity kinds never convert (AD-22).
/// </summary>
/// <param name="Value">The underlying surrogate key.</param>
public readonly record struct ClientId(int Value);
