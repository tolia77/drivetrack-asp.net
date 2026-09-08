namespace DriveTrack.Application.Users;

/// <summary>Credentials presented for sign-in (FR-4).</summary>
/// <param name="Email">The address the account signs in with.</param>
/// <param name="Password">The password, verified against the stored hash and never logged.</param>
public sealed record SignInCommand(string? Email, string? Password);
