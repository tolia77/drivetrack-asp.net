namespace DriveTrack.Web.Components.Shared;

/// <summary>
/// The three looks a <c>DtBadge</c> can wear.
/// <para>
/// Three, and there is no fourth for "good" or "bad". A badge marks a fact beside a value, and the
/// moment one of them is coloured by outcome it starts competing with the StatusLabel vocabulary -
/// which is the one distinction the design keeps absolute: badges are borderless pills, status
/// labels are bordered squares, and neither is allowed to look like the other.
/// </para>
/// </summary>
public enum DtBadgeVariant
{
    /// <summary>A plain fact: "On duty", "3 / 5 photos". The sunken pill.</summary>
    Neutral,

    /// <summary>Past its delivery window. Warning text on the overdue pill, with a warning mark.</summary>
    Overdue,

    /// <summary>
    /// Somebody's role, wherever a person is named. Every role wears the same badge: roles are not
    /// ranked, and a colour apiece would rank them.
    /// </summary>
    Role,
}
