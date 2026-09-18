using DriveTrack.Domain.Identity;

namespace DriveTrack.Web.Components.Pages;

/// <summary>
/// What the chat screen reads back across the JavaScript boundary.
/// <para>
/// These mirror the chat capability's contracts with one deliberate difference: the thread key is a
/// plain <see cref="int"/>. A payload from the hub crosses two serializers — the hub protocol's,
/// which carries AD-22's typed identities as the numbers they are, and Blazor's JS interop, whose
/// options are the framework's and take no converters of ours. A typed identity would leave the
/// server as <c>7</c> and be read back as an object that is not there.
/// </para>
/// <para>
/// So these are the interop shapes, and nothing else uses them: the screen compares the number
/// against the one it already holds, and every authorization decision was taken by the chat service
/// before the payload was ever written.
/// </para>
/// </summary>
/// <param name="Id">The message row's id.</param>
/// <param name="SenderUserId">Who wrote it, or null once that account has been deleted.</param>
/// <param name="SenderName">Their display name, or empty when the account is gone.</param>
/// <param name="SentAt">When it was sent, at offset zero.</param>
/// <param name="Text">The message body.</param>
public sealed record ChatMessageView(
    int Id,
    int? SenderUserId,
    string SenderName,
    DateTimeOffset SentAt,
    string Text);

/// <inheritdoc cref="ChatMessageView" />
/// <param name="DriverId">The driver row the conversation is keyed on.</param>
/// <param name="DriverName">The driver's display name.</param>
/// <param name="Messages">Every message, oldest first.</param>
public sealed record ChatThreadView(
    int DriverId,
    string DriverName,
    IReadOnlyList<ChatMessageView> Messages);

/// <inheritdoc cref="ChatMessageView" />
/// <param name="DriverId">The driver row the conversation is keyed on.</param>
/// <param name="DriverName">The driver's display name, which the roster is ordered by.</param>
/// <param name="VehicleModel">The vehicle they drive, or null when they are assigned none.</param>
/// <param name="VehicleLicensePlate">That vehicle's plate, or null for the same reason.</param>
/// <param name="OnDuty">Whether a shift of theirs is open right now.</param>
public sealed record ChatThreadSummaryView(
    int DriverId,
    string DriverName,
    string? VehicleModel,
    string? VehicleLicensePlate,
    bool OnDuty);

/// <summary>
/// The decisions the chat screen takes, lifted out of it so a test can reach them (DW-17, DW-18).
/// <para>
/// A Blazor component rendered statically dispatches no events: a decision written inside an
/// <c>@onclick</c> handler or behind a live hub connection is a decision no test in this solution
/// can reach. These are the ones the screen actually makes — who is a participant, who is offered a
/// roster, what the conversation header names, which roster row is the open one, where a day of the
/// conversation begins and what that day is called, and whose line a message is — and every one of
/// them is a pure function of a role, a selection, an instant or an id.
/// </para>
/// <para>
/// FR-12 unchanged: none of this is authorization. <c>IAccessGuard.RequireChatParticipant</c> inside
/// the chat service refuses a caller whatever this screen decided to draw.
/// </para>
/// </summary>
internal static class ChatViews
{
    /// <summary>The class every bubble in the conversation carries.</summary>
    /// <remarks>
    /// The names are the design system's ChatBubble, and they live here rather than in the markup
    /// for the reason <c>ReviewViews</c>' rating classes do: which side of the conversation a line
    /// belongs to is a decision, and a decision written into a <c>class="@@(...)"</c> expression is
    /// a decision no test in this solution can reach.
    /// </remarks>
    internal const string LineClassName = "dt-bubble";

    /// <summary>
    /// The class an own line carries in addition: the viewer's own message, drawn on the brand fill
    /// and aligned to the trailing edge.
    /// </summary>
    internal const string OwnLineClassName = "dt-bubble--own";

    /// <summary>
    /// The class a received line carries in addition. Stated rather than left as the absence of
    /// <see cref="OwnLineClassName"/>, so the two sides are two named things: the distinction is
    /// carried by the side, the fill and the border together, and none of the three is the only
    /// signal - which a rule written as "not own" could not be checked for.
    /// </summary>
    internal const string OtherLineClassName = "dt-bubble--other";

    /// <summary>
    /// The value <see cref="OpenThreadMark"/> answers with for the open conversation. A constant so
    /// the markup and the test that reads it back are the same string rather than two copies of one.
    /// </summary>
    internal const string OpenThreadMarkValue = "true";

    /// <summary>
    /// Whether this caller is one of chat's two participants (FR-68, FR-69). A client and an
    /// administrator are not, which is why the screen has a refusal to show rather than an empty
    /// conversation.
    /// </summary>
    /// <param name="role">The caller's single role (AD-4).</param>
    internal static bool IsParticipant(UserRole role) =>
        role is UserRole.Dispatcher or UserRole.Driver;

    /// <summary>
    /// Whether this caller is offered a roster of conversations (FR-68). A driver has exactly one
    /// and nothing to pick between, so the picker is absent rather than disabled.
    /// </summary>
    /// <param name="role">The caller's single role.</param>
    internal static bool ShowsRoster(UserRole role) => role == UserRole.Dispatcher;

    /// <summary>
    /// Whether the header names the dispatch desk — which is who a driver is talking to, and the
    /// one side of the conversation that is not a person with a row (FR-69).
    /// </summary>
    /// <param name="role">The caller's single role.</param>
    internal static bool NamesTheDispatchDesk(UserRole role) => role == UserRole.Driver;

    /// <summary>
    /// Whether the header names the selected driver — which is who a dispatcher is talking to, once
    /// they have chosen one.
    /// </summary>
    /// <param name="role">The caller's single role.</param>
    /// <param name="hasThread">Whether a conversation is open.</param>
    internal static bool NamesTheSelectedDriver(UserRole role, bool hasThread) =>
        role == UserRole.Dispatcher && hasThread;

    /// <summary>
    /// Whether the screen asks the caller to pick a conversation: a dispatcher with none open.
    /// <para>
    /// Deliberately not the complement of <see cref="NamesTheSelectedDriver"/>. A driver with no
    /// conversation open yet is waiting for their own to load and has nothing to pick, so asking
    /// them to choose would be asking a question with one answer they cannot give.
    /// </para>
    /// </summary>
    /// <param name="role">The caller's single role.</param>
    /// <param name="hasThread">Whether a conversation is open.</param>
    internal static bool AsksForAThread(UserRole role, bool hasThread) =>
        role == UserRole.Dispatcher && !hasThread;

    /// <summary>
    /// The <c>aria-current</c> value a roster row carries: <c>"true"</c> for the conversation that
    /// is open, and null — which Blazor renders as no attribute at all — for every other row.
    /// <para>
    /// A string rather than a bool, because the two answers are "this attribute, with this value"
    /// and "no attribute". <c>aria-current="false"</c> is a legal value meaning "not the current
    /// one", and a roster where every row states that is a roster announcing a negative twelve
    /// times before it reaches the one row a reader is looking for.
    /// </para>
    /// </summary>
    /// <param name="driverId">The driver this row is for.</param>
    /// <param name="openDriverId">The conversation on screen, or null when none is open.</param>
    internal static string? OpenThreadMark(int driverId, int? openDriverId) =>
        driverId == openDriverId ? OpenThreadMarkValue : null;

    /// <summary>
    /// Whether a line begins a new day of the conversation, and so is preceded by a separator.
    /// <para>
    /// Chat keeps its whole history (FR-71), so a conversation is read as a run of days rather than
    /// as one list: the separator is what stops a message from March being read as one from this
    /// morning. The first line of a conversation begins a day as much as any later one does — a
    /// conversation that opened with no separator at all would leave its oldest day the only
    /// unnamed one.
    /// </para>
    /// <para>
    /// Compared in local time, and local here means the <em>server's</em> zone: this runs on a Blazor
    /// Server circuit, so <c>ToLocalTime</c> resolves against the host's time zone and not the
    /// browser's. That is the fleet's own zone rather than each reader's, which is the right answer
    /// for a single-country courier product and the wrong one the day it is not — reading the
    /// browser's offset would mean an interop call this decision deliberately does not make.
    /// </para>
    /// </summary>
    /// <param name="previous">When the line before this one was sent, or null when there is none.</param>
    /// <param name="current">When this line was sent.</param>
    internal static bool StartsADay(DateTimeOffset? previous, DateTimeOffset current) =>
        previous is not { } before || DayOf(before) != DayOf(current);

    /// <summary>Whether a day separator names its day as today.</summary>
    /// <param name="sentAt">The instant the day is taken from.</param>
    /// <param name="now">The reading caller's present.</param>
    internal static bool NamesTheDayAsToday(DateTimeOffset sentAt, DateTimeOffset now) =>
        DayOf(sentAt) == DayOf(now);

    /// <summary>Whether a day separator names its day as yesterday.</summary>
    /// <param name="sentAt">The instant the day is taken from.</param>
    /// <param name="now">The reading caller's present.</param>
    /// <remarks>
    /// The calendar day before, not "within twenty-four hours": a message sent at eleven last night
    /// and read at ten this morning is yesterday's, and an interval would call it today's.
    /// </remarks>
    internal static bool NamesTheDayAsYesterday(DateTimeOffset sentAt, DateTimeOffset now) =>
        DayOf(sentAt) == DayOf(now).AddDays(-1);

    /// <summary>
    /// Whether a day separator names its day by its date — every day older than yesterday.
    /// <para>
    /// Deliberately not the complement of the two above. A conversation can hold a line dated after
    /// the reader's present: the clock on the machine that stored it and the clock on the machine
    /// reading it are two clocks, and a line from a minute in the future must still be given a
    /// separator with something written on it rather than an empty one.
    /// </para>
    /// </summary>
    /// <param name="sentAt">The instant the day is taken from.</param>
    /// <param name="now">The reading caller's present.</param>
    internal static bool NamesTheDayByDate(DateTimeOffset sentAt, DateTimeOffset now) =>
        !NamesTheDayAsToday(sentAt, now) && !NamesTheDayAsYesterday(sentAt, now);

    /// <summary>
    /// The class a line carries: own messages are set apart, and the class carries the distinction
    /// as well as the alignment so it is not colour alone.
    /// </summary>
    /// <param name="senderUserId">Who wrote the line, or null once that account is deleted.</param>
    /// <param name="viewerUserId">The reading caller's user id.</param>
    /// <remarks>
    /// A deleted sender is nobody's own line: null never equals a viewer's id, which is the answer
    /// this comparison has to give rather than one it falls into.
    /// </remarks>
    internal static string LineClass(int? senderUserId, int viewerUserId) =>
        senderUserId == viewerUserId
            ? LineClassName + " " + OwnLineClassName
            : LineClassName + " " + OtherLineClassName;

    /// <summary>
    /// The calendar day an instant falls on, in the zone the application is hosted in. Written once
    /// rather than at each of the four call sites: the grouping and the three labels have to agree
    /// about where a day starts, or a separator says "today" over a group the split put in March.
    /// </summary>
    private static DateOnly DayOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToLocalTime().Date);
}
