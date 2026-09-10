using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Authorization;

/// <summary>
/// AD-3's scoping predicate: which rows of a collection this caller may be shown, in a shape a
/// repository query can take as a parameter.
/// <para>
/// AD-3 forbids post-filtering a paged result, and the reason is arithmetic rather than taste: a
/// page fetched with <c>OFFSET 0 LIMIT 10</c> and then filtered down to the caller's own rows
/// answers an empty page for a driver whose deliveries happen to be rows 51 to 60, and no amount
/// of paging further will find them. The predicate therefore has to reach the <c>WHERE</c> clause,
/// which means it has to be a value the guard can hand to a repository.
/// </para>
/// <para>
/// One role per user (AD-4), so a caller is at most one of driver or client and the pair is total.
/// Both null is the unrestricted case, which is what <see cref="Domain.Identity.UserRole.Admin"/>
/// and <see cref="Domain.Identity.UserRole.Dispatcher"/> receive.
/// </para>
/// </summary>
/// <param name="DriverId">The only driver whose rows may be shown, or null for every driver.</param>
/// <param name="ClientId">The only client whose rows may be shown, or null for every client.</param>
public readonly record struct AccessScope(DriverId? DriverId, ClientId? ClientId);
