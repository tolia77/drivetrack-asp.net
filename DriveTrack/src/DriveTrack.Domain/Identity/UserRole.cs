namespace DriveTrack.Domain.Identity;

/// <summary>
/// The four roles a user can hold (AD-4). One user holds exactly one of these — the unique index
/// on <c>asp_net_user_roles.user_id</c> is what makes that true in the schema rather than in
/// application code.
/// <para>
/// The member names <em>are</em> the ASP.NET Core Identity role names and the value of the role
/// claim both authentication schemes issue (AD-21): an enum crosses every boundary as its member
/// name, never as an ordinal, so reordering this enum cannot silently repoint a stored role row.
/// </para>
/// <para>
/// <c>Admin</c> satisfies every authorization check by rule inside the access guard, never by
/// holding extra role rows (AD-4).
/// </para>
/// </summary>
public enum UserRole
{
    /// <summary>Full access by rule, decided inside the access guard and nowhere else.</summary>
    Admin,

    /// <summary>Runs the dispatch board: deliveries, drivers and vehicles.</summary>
    Dispatcher,

    /// <summary>Carries deliveries. Has a <c>Driver</c> subtype row (DR-3).</summary>
    Driver,

    /// <summary>Requests deliveries. Has a <c>Client</c> subtype row (DR-3).</summary>
    Client,
}
