using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Pages;
using DriveTrack.Web.Hubs;

// Aliased because the screen's type name and this suite's own Chat namespace collide, and the
// namespace wins: `Chat` alone reads as DriveTrack.Integration.Tests.Chat from here.
using ChatScreen = DriveTrack.Web.Components.Pages.Chat;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The chat screen's decisions, and the conventions its markup has to keep (FR-68 to FR-76).
/// <para>
/// The decisions are asserted against <see cref="ChatViews"/> rather than against rendered HTML,
/// because the screen's own state arrives over a live hub connection and a statically rendered
/// component dispatches no events: a rule written inside the <c>@onclick</c> or behind the socket is
/// a rule no test in this solution can reach (DW-17, DW-18). Lifting the four of them out is what
/// makes them assertable at all.
/// </para>
/// <para>
/// FR-12 unchanged throughout: none of this is authorization. <c>IAccessGuard</c> inside the chat
/// service refuses a caller whatever this screen decided to draw, and <c>ChatHubTests</c> is where
/// that is proved.
/// </para>
/// </summary>
public class ChatScreenTests
{
    [Theory]
    [InlineData(UserRole.Dispatcher, true)]
    [InlineData(UserRole.Driver, true)]
    [InlineData(UserRole.Client, false)]
    [InlineData(UserRole.Admin, false)]
    public void Only_the_two_participants_are_shown_a_conversation(UserRole role, bool participant)
    {
        // The admin row is the one a reader will take for a mistake, and it is the same product
        // decision RequireChatParticipant enforces: admins moderate rather than dispatch. The screen
        // agrees with the guard rather than deciding anything - a client or an admin who reached
        // /chat sees the refusal instead of an empty conversation that would refuse them anyway.
        Assert.Equal(participant, ChatViews.IsParticipant(role));
    }

    [Theory]
    [InlineData(UserRole.Dispatcher, true)]
    [InlineData(UserRole.Driver, false)]
    [InlineData(UserRole.Client, false)]
    [InlineData(UserRole.Admin, false)]
    public void Only_a_dispatcher_is_offered_a_roster(UserRole role, bool roster)
    {
        // FR-69: a driver has exactly one conversation, so there is no picker rather than a picker
        // with one entry - and no roster call, which the guard would refuse anyway.
        Assert.Equal(roster, ChatViews.ShowsRoster(role));
    }

    [Theory]
    [InlineData(UserRole.Driver, false, true, false, false)]
    [InlineData(UserRole.Driver, true, true, false, false)]
    [InlineData(UserRole.Dispatcher, true, false, true, false)]
    [InlineData(UserRole.Dispatcher, false, false, false, true)]
    public void The_header_names_whoever_the_caller_is_talking_to(
        UserRole role,
        bool hasThread,
        bool desk,
        bool driverName,
        bool prompt)
    {
        // The header is a different person on each side of the same conversation: a driver is
        // talking to a desk, a dispatcher to a named driver. The three branches are mutually
        // exclusive by construction and the table says so, which is what stops a fourth state -
        // a driver asked to pick from a list they do not have - appearing the next time one of the
        // conditions is edited.
        Assert.Equal(desk, ChatViews.NamesTheDispatchDesk(role));
        Assert.Equal(driverName, ChatViews.NamesTheSelectedDriver(role, hasThread));
        Assert.Equal(prompt, ChatViews.AsksForAThread(role, hasThread));
    }

    [Fact]
    public void A_driver_waiting_for_their_own_conversation_is_never_asked_to_pick_one()
    {
        // Deliberately not the complement of "a conversation is open". A driver's own conversation
        // loads on first render, and between the render and the load there is nothing for them to
        // choose - so asking would be asking a question with one answer they cannot give.
        Assert.False(ChatViews.AsksForAThread(UserRole.Driver, hasThread: false));
    }

    [Fact]
    public void A_line_is_set_apart_only_when_the_reader_wrote_it()
    {
        // FR-73's own-message distinction. The class carries it as well as the alignment, so the
        // difference is not colour alone.
        Assert.Equal(
            ChatViews.LineClassName + " " + ChatViews.OwnLineClassName,
            ChatViews.LineClass(senderUserId: 3, viewerUserId: 3));

        Assert.Equal(
            ChatViews.LineClassName + " " + ChatViews.OtherLineClassName,
            ChatViews.LineClass(senderUserId: 4, viewerUserId: 3));

        // AD-20: a deleted sender is nobody's own line. Null comparing equal to a viewer's id would
        // mark every unattributed line in the conversation as the reader's own.
        Assert.Equal(
            ChatViews.LineClassName + " " + ChatViews.OtherLineClassName,
            ChatViews.LineClass(senderUserId: null, viewerUserId: 3));
    }

    [Fact]
    public void Only_the_conversation_that_is_open_is_marked_as_the_current_one()
    {
        // The roster's fill and its edge bar say which conversation is open to everyone who can see
        // them. `aria-current` is what says it to everyone else, and it is the only signal a screen
        // reader has among twelve identically shaped rows.
        Assert.Equal(
            ChatViews.OpenThreadMarkValue,
            ChatViews.OpenThreadMark(driverId: 7, openDriverId: 7));

        // Null rather than "false": Blazor drops an attribute whose value is null, and
        // `aria-current="false"` is a legal value meaning "not this one" - a roster where every row
        // states that is a roster announcing eleven negatives before the one positive.
        Assert.Null(ChatViews.OpenThreadMark(driverId: 7, openDriverId: 8));

        // And nothing is current before a dispatcher has picked anyone, which is the state the
        // screen opens in.
        Assert.Null(ChatViews.OpenThreadMark(driverId: 7, openDriverId: null));
    }

    [Fact]
    public void A_day_separator_opens_the_conversation_and_every_later_day_of_it()
    {
        // FR-71 keeps the whole history, so a conversation is a run of days rather than one list.
        // Without the separator a line from March reads as one from this morning.
        // Written as local instants, because the day a reader means is the day on their own
        // calendar: the same UTC evening is one day in Kyiv and the day before in Lisbon, and a
        // test anchored at offset zero would assert about the build agent's timezone rather than
        // about the grouping.
        var morning = Local(2026, 9, 7, 9);
        var evening = Local(2026, 9, 7, 21);
        var nextDay = Local(2026, 9, 8, 7);

        // The first line of a conversation opens a day like any other. Without this the oldest day
        // is the only unnamed one - and on a conversation that fits on one screen, that is every
        // separator there would have been.
        Assert.True(ChatViews.StartsADay(previous: null, morning));

        // Two lines on the same day are one group, however far apart they are on it.
        Assert.False(ChatViews.StartsADay(morning, evening));

        // And the calendar day is what splits them, not an elapsed interval: ten hours inside one
        // day is one group, ten hours across midnight is two.
        Assert.True(ChatViews.StartsADay(evening, nextDay));
    }

    [Fact]
    public void A_day_is_named_today_yesterday_or_by_its_date_and_never_two_of_the_three()
    {
        // The three labels are one decision written three ways, so they are asserted together: a
        // separator that matched none of them would render as an empty pill, and one that matched
        // two would render the day twice.
        var now = Local(2026, 9, 8, 12);

        var days = new[]
        {
            now,
            Local(2026, 9, 7, 12),
            Local(2026, 9, 6, 12),
            Local(2025, 7, 4, 12),

            // A line dated after the reader's present. The clock that stored it and the clock
            // reading it are two clocks, so this is reachable - and it still has to be named
            // something rather than falling through all three branches.
            Local(2026, 9, 9, 12),
        };

        foreach (var day in days)
        {
            var named = new[]
            {
                ChatViews.NamesTheDayAsToday(day, now),
                ChatViews.NamesTheDayAsYesterday(day, now),
                ChatViews.NamesTheDayByDate(day, now),
            };

            Assert.Single(named, hit => hit);
        }

        Assert.True(ChatViews.NamesTheDayAsToday(now, now));
        Assert.True(ChatViews.NamesTheDayAsYesterday(Local(2026, 9, 7, 12), now));
        Assert.True(ChatViews.NamesTheDayByDate(Local(2026, 9, 6, 12), now));

        // Yesterday is the calendar day before, not "within twenty-four hours": a line written at
        // eleven last night and read at ten this morning is yesterday's, and an interval would call
        // it today's.
        Assert.True(
            ChatViews.NamesTheDayAsYesterday(Local(2026, 9, 7, 23), Local(2026, 9, 8, 10)));
    }

    [Fact]
    public void The_roster_search_reads_the_name_the_vehicle_and_the_plate()
    {
        // FR-36's shape, over the roster the hub already answered with - typing here is never a
        // query. Asserted against the function because the screen's own roster arrives over a live
        // connection and a static render never holds a row to filter.
        var row = new ChatThreadSummaryView(
            DriverId: 4,
            DriverName: "Олена Андросова",
            VehicleModel: "Renault Master",
            VehicleLicensePlate: "АА1234ВС",
            OnDuty: true);

        // An empty box narrows nothing, which is the state the screen opens in.
        Assert.True(ChatScreen.Matches(row, null));
        Assert.True(ChatScreen.Matches(row, "   "));

        Assert.True(ChatScreen.Matches(row, "Андрос"));
        Assert.True(ChatScreen.Matches(row, "Master"));
        Assert.True(ChatScreen.Matches(row, "АА1234"));

        // And the same plate typed on a Latin keyboard, which is how it is actually searched for: a
        // Ukrainian plate is written in the twelve Cyrillic letters that have a Latin twin, so
        // "АА1234ВС" and "AA1234BC" are different code points for the same eight characters and no
        // culture comparison equates them.
        Assert.True(ChatScreen.Matches(row, "AA1234"));
        Assert.True(ChatScreen.Matches(row, "AA1234BC"));

        // Case-insensitively, and in the culture the product runs in rather than by code point.
        Assert.True(ChatScreen.Matches(row, "master"));

        Assert.False(ChatScreen.Matches(row, "Яценко"));

        // A driver with no vehicle is still searchable by name, and the two absent fields match
        // nothing rather than matching everything.
        var bare = new ChatThreadSummaryView(5, "Петро Яценко", null, null, false);

        Assert.True(ChatScreen.Matches(bare, "Яценко"));
        Assert.False(ChatScreen.Matches(bare, "Master"));
    }

    [Fact]
    public void The_screen_is_authorized_by_session_and_never_by_role()
    {
        // FR-12 and the story's own boundary: chat's roles are decided by
        // IAccessGuard.RequireChatParticipant, once, inside the service. A [Authorize(Roles = ...)]
        // here would be a second place that decision lived - and it would have to restate the one
        // asymmetry in the system, admin lockout, in a form no test reads.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        Assert.Contains("@attribute [Authorize]", screen, StringComparison.Ordinal);
        Assert.Contains("if (!Caller.IsAuthenticated)", screen, StringComparison.Ordinal);

        // ICurrentUser.Role throws for an anonymous caller, so the caller is read once and only
        // after the authenticated check - the same shape every other screen uses.
        Assert.Equal(1, SharedMarkup.Occurrences(screen, "Caller.Role"));

        // Razor comments stripped first, as AdministrationScreenTests does: the screen explains in
        // prose why it carries neither of these, and an explanation of a rule is not a breach of it.
        var markup = Regex.Replace(
            screen, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("[Authorize(Roles", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<AuthorizeView", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_takes_every_branch_through_the_decisions_that_are_tested()
    {
        // The link between this class and the markup. Every assertion above is worthless if the
        // screen re-derives the same conditions inline, so the branches are counted: a role
        // comparison written in the markup instead would be a decision nothing here can see.
        // Every part of the screen: the page, the line it renders per message, the roster row and
        // the day separator. Reading only the page would report a decision missing the moment one
        // moved into a component the page composes.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor")
            + SharedMarkup.ReadComponent("Pages", "ChatLine.razor")
            + SharedMarkup.ReadComponent("Pages", "ChatRosterRow.razor")
            + SharedMarkup.ReadComponent("Pages", "ChatDaySeparator.razor");

        foreach (var decision in new[]
                 {
                     "ChatViews.IsParticipant",
                     "ChatViews.ShowsRoster",
                     "ChatViews.NamesTheDispatchDesk",
                     "ChatViews.NamesTheSelectedDriver",
                     "ChatViews.AsksForAThread",
                     "ChatViews.LineClass",

                     // The roster row's own mark, and the four the day separators are decided by.
                     // Each is a member of ChatViews with a test of its own below; named here so a
                     // branch that stopped going through one is a failure rather than a silently
                     // re-derived condition in the markup.
                     "ChatViews.OpenThreadMark",
                     "ChatViews.StartsADay",
                     "ChatViews.NamesTheDayAsToday",
                     "ChatViews.NamesTheDayAsYesterday",
                     "ChatViews.NamesTheDayByDate",
                 })
        {
            Assert.Contains(decision, screen, StringComparison.Ordinal);
        }

        // No role is ever named in the markup: the caller's own role is read into a field once, and
        // every branch is taken through a decision this class asserts. A `UserRole.Dispatcher`
        // written into an @if would be a decision nothing here can see.
        Assert.Equal(0, SharedMarkup.Occurrences(screen, "UserRole."));
    }

    [Fact]
    public void A_send_in_flight_disables_the_button_that_started_it()
    {
        // A double-click must not persist two rows: there is no dedupe anywhere downstream, so the
        // second would simply be a second message. The flag is set before the call and cleared in a
        // finally, so a refusal re-enables the button rather than leaving it dead.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        Assert.Contains(@"disabled=""@_sending""", screen, StringComparison.Ordinal);
        Assert.Contains("_sending = true;", screen, StringComparison.Ordinal);
        Assert.Contains("finally", screen, StringComparison.Ordinal);
        Assert.Contains("_sending = false;", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hub_that_has_dropped_says_so_holds_the_send_control_and_keeps_the_draft()
    {
        // The one failure this screen could not report. `withAutomaticReconnect()` retries in the
        // background and announces nothing, so a driver at a doorstep goes on typing into a socket
        // that is gone and finds out when Send fails - by which time the message they meant to send
        // is a refusal banner instead of a line in the conversation.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        // Declared without an initializer: a flag born true would hold the composer shut on the
        // first render, before a connection had been attempted at all.
        Assert.Contains("private bool _hubDown;", screen, StringComparison.Ordinal);

        // One callback carrying both directions rather than two methods. Two would be two places to
        // forget to clear the flag, and a dock that never clears is worse than none - it tells a
        // reader the conversation is dead while messages are arriving in it.
        Assert.Contains(
            "public Task ConnectionChangedAsync(bool connected, bool recoverable)",
            screen,
            StringComparison.Ordinal);

        Assert.Contains("_hubDown = !connected;", screen, StringComparison.Ordinal);

        // And the two failures are told apart. A drop the client is still retrying is a wait; a drop
        // it has given up on is not, and a dock that goes on saying "reconnecting…" for the rest of
        // the session is a promise nobody is keeping, made to a reader with no way out of it.
        Assert.Contains("_hubGaveUp = !connected && !recoverable;", screen, StringComparison.Ordinal);
        Assert.Contains(@"@Localizer[""ChatConnectionLost""]", screen, StringComparison.Ordinal);
        Assert.Contains(@"@Localizer[""Reload""]", screen, StringComparison.Ordinal);

        // A connection that never opened is the same permanent state, and nothing will call back to
        // clear it: without this the composer stays live over a hub that was never there.
        Assert.Contains("_hubDown = _handle is null;", screen, StringComparison.Ordinal);

        // The dock is rendered only while it is down, and the control is held for as long as it
        // shows. The fieldset is the hold: `disabled` on the button itself stays the in-flight
        // flag's, so the two failures keep saying two different things.
        Assert.Contains("@if (_hubDown)", screen, StringComparison.Ordinal);
        Assert.Contains("dt-chat-dock", screen, StringComparison.Ordinal);

        Assert.Contains(
            @"<fieldset class=""dt-chat-hold"" disabled=""@_hubDown"">",
            screen,
            StringComparison.Ordinal);

        // And the handler refuses regardless of what the markup rendered: `disabled` is a hint to
        // the browser, and an Enter already dispatched in the box reaches SendAsync anyway.
        Assert.Contains("_sending || _hubDown)", screen, StringComparison.Ordinal);

        // The draft survives the drop and the recovery. A callback that cleared the box would throw
        // away the message a driver typed while the connection was down, which is exactly the
        // message they still mean to send when it comes back.
        var body = BodyOf(screen, "ConnectionChangedAsync");

        Assert.DoesNotContain("_draft", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dock_is_not_nested_in_a_conversation_that_may_not_be_open()
    {
        // The state this was written for is the dispatch desk's opening one: a roster on the left
        // and nothing picked yet. Nested inside the conversation panel's `@if (_thread is not null)`
        // the dock could never render there - a dispatcher whose hub died before they chose anyone
        // would read a roster that looked perfectly live and open conversations that never loaded.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        var dock = screen.IndexOf("@if (_hubDown)", StringComparison.Ordinal);
        var conversation = screen.IndexOf("@if (_thread is not null)", StringComparison.Ordinal);

        Assert.True(dock >= 0, "Chat.razor renders no dock.");
        Assert.True(conversation >= 0, "Chat.razor has no conversation branch.");

        Assert.True(
            dock < conversation,
            "The dock is rendered inside the conversation branch, so a hub that dropped before a "
                + "conversation was opened says nothing at all.");
    }

    [Fact]
    public async Task A_roster_row_renders_the_driver_the_vehicle_the_plate_and_the_duty()
    {
        // The roster only ever arrives over a live hub connection, and the harness installs a JS
        // runtime that refuses to be called precisely so a static render never reaches
        // OnAfterRenderAsync - so as a block inside the screen's own loop none of this was
        // reachable, and re-pointing that loop at an empty list left every test green. Its own
        // component is what makes a row a thing a test can produce.
        var open = await RenderRowAsync(
            new ChatThreadSummaryView(4, "Олена Андросова", "Renault Master", "АА1234ВС", true),
            openDriverId: 4);

        Assert.Contains("Олена Андросова", open, StringComparison.Ordinal);
        Assert.Contains("Renault Master", open, StringComparison.Ordinal);
        Assert.Contains("АА1234ВС", open, StringComparison.Ordinal);

        Assert.Contains(Ukrainian("ShiftOpen"), open, StringComparison.Ordinal);
        Assert.DoesNotContain(Ukrainian("ShiftClosed"), open, StringComparison.Ordinal);

        // The open conversation, marked for the reader who cannot see the fill or the edge bar.
        Assert.Contains(@"aria-current=""true""", open, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_roster_row_that_is_not_the_open_one_carries_no_mark_and_absent_stays_absent()
    {
        var other = await RenderRowAsync(
            new ChatThreadSummaryView(4, "Олена Андросова", "Renault Master", "АА1234ВС", true),
            openDriverId: 5);

        // No attribute at all rather than `aria-current="false"`, which is a legal value meaning
        // "not this one" and would have every row in the roster announcing a negative.
        Assert.DoesNotContain("aria-current", other, StringComparison.Ordinal);

        // A driver with no vehicle renders the row without one - no blank line where a van would be
        // - and the off state says so in words rather than by the absence of the on one.
        var bare = await RenderRowAsync(
            new ChatThreadSummaryView(5, "Петро Яценко", null, null, false),
            openDriverId: null);

        Assert.Contains("Петро Яценко", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-chat-thread__vehicle", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-chat-thread__plate", bare, StringComparison.Ordinal);

        Assert.Contains(Ukrainian("ShiftClosed"), bare, StringComparison.Ordinal);
        Assert.DoesNotContain(Ukrainian("ShiftOpen"), bare, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_day_separator_names_today_yesterday_and_an_older_day_by_its_date()
    {
        // Three days, rendered, because the decisions being right is not the same claim as the
        // markup taking the branch they decide: making both label branches read "today" left every
        // assertion in this file green until this existed.
        var now = Local(2026, 9, 8, 12);

        var today = await RenderSeparatorAsync(Local(2026, 9, 8, 9), now);
        var yesterday = await RenderSeparatorAsync(Local(2026, 9, 7, 9), now);
        var older = await RenderSeparatorAsync(Local(2026, 3, 2, 9), now);

        Assert.Contains(Ukrainian("ChatToday"), today, StringComparison.Ordinal);
        Assert.DoesNotContain(Ukrainian("ChatYesterday"), today, StringComparison.Ordinal);

        Assert.Contains(Ukrainian("ChatYesterday"), yesterday, StringComparison.Ordinal);
        Assert.DoesNotContain(Ukrainian("ChatToday"), yesterday, StringComparison.Ordinal);

        // The older one names its date, in the culture the product runs in (NFR-15), and neither of
        // the two words - which is the branch that would silently disappear first.
        Assert.Contains(
            Local(2026, 3, 2, 9).ToLocalTime().ToString("d", CultureInfo.GetCultureInfo("uk-UA")),
            older,
            StringComparison.Ordinal);

        Assert.DoesNotContain(Ukrainian("ChatToday"), older, StringComparison.Ordinal);
        Assert.DoesNotContain(Ukrainian("ChatYesterday"), older, StringComparison.Ordinal);

        // And a separator is not a line of the conversation. The list it sits in is the message
        // list, so an unqualified <li> would have a screen reader announce nine items over a
        // conversation of six messages and three days.
        Assert.Contains(@"role=""presentation""", today, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_participant_whose_hub_is_up_is_shown_no_dock()
    {
        // The other direction, rendered rather than read: the flag starts false, so the screen a
        // reader first meets carries no warning about a connection that is fine. Source text cannot
        // tell this from a dock rendered unconditionally - the file contains the same words either
        // way.
        var html = await ShellCaller.RenderAsync<ChatScreen>(UserRole.Driver);

        // The guard this assertion is worthless without. `DoesNotContain` passes for a screen that
        // rendered the dock's whole enclosing branch and passes just as happily for one that
        // rendered nothing at all - including, as this once did, a dock nested inside a conversation
        // a static render never has. So the region that would hold it is asserted present first.
        Assert.Contains("dt-chat-screen", html, StringComparison.Ordinal);
        Assert.Contains("dt-chat-thread-panel", html, StringComparison.Ordinal);

        Assert.DoesNotContain("dt-chat-dock", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_echoed_optimistically()
    {
        // FR-73: a sent message appears when the server broadcasts the stored row back, so what is
        // on screen is what is in the database. The only place the conversation grows is the
        // callback the hub invokes.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        Assert.Contains("[JSInvokable]", screen, StringComparison.Ordinal);
        Assert.Equal(1, SharedMarkup.Occurrences(screen, "_thread with"));
    }

    // =====================================================================================
    // What the screen actually renders
    //
    // Everything above reads source text, and no arrangement of words can tell a driver's screen
    // from a client's: swap `_isParticipant` and `_showsRoster` in OnInitialized and FR-69's whole
    // driver side is dead while every assertion above still passes, because the file still contains
    // all the same words. These render it.
    //
    // Static rendering never reaches OnAfterRenderAsync, so no JavaScript runtime is called and the
    // screen renders exactly as it would on the server's first pass.
    // =====================================================================================

    [Theory]
    [InlineData(UserRole.Client)]
    [InlineData(UserRole.Admin)]
    public async Task A_caller_who_is_not_a_participant_is_shown_the_refusal_and_no_conversation(
        UserRole role)
    {
        var html = await ShellCaller.RenderAsync<ChatScreen>(role);

        Assert.Contains(Ukrainian("ChatNotParticipant"), html, StringComparison.Ordinal);

        // Not merely "the refusal is there as well": the conversation shell, the roster and the
        // composer are all absent, so there is nothing to type into and nothing to read.
        Assert.DoesNotContain("dt-chat-thread-panel", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-chat-roster", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-chat-composer", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    public async Task A_participant_is_shown_the_conversation_and_never_the_refusal(UserRole role)
    {
        var html = await ShellCaller.RenderAsync<ChatScreen>(role);

        Assert.Contains("dt-chat-thread-panel", html, StringComparison.Ordinal);
        Assert.DoesNotContain(Ukrainian("ChatNotParticipant"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_dispatcher_is_rendered_a_roster_and_asked_to_pick_a_driver()
    {
        // FR-68 and FR-69 as the two sides of one screen, rendered rather than read: a dispatcher
        // gets a list to choose from and a prompt to choose, a driver gets neither because they have
        // exactly one conversation and it opens itself.
        var desk = await ShellCaller.RenderAsync<ChatScreen>(UserRole.Dispatcher);
        var cab = await ShellCaller.RenderAsync<ChatScreen>(UserRole.Driver);

        Assert.Contains("dt-chat-roster", desk, StringComparison.Ordinal);
        Assert.Contains(Ukrainian("ChatSelectThread"), desk, StringComparison.Ordinal);

        Assert.DoesNotContain("dt-chat-roster", cab, StringComparison.Ordinal);
        Assert.DoesNotContain(Ukrainian("ChatSelectThread"), cab, StringComparison.Ordinal);

        // And the header names the right counterparty on each side.
        Assert.Contains(Ukrainian("ChatDispatchDesk"), cab, StringComparison.Ordinal);
        Assert.DoesNotContain(Ukrainian("ChatDispatchDesk"), desk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_renders_its_sender_its_text_and_the_time_it_was_sent()
    {
        // FR-73 and FR-76. Rendered through ChatLine, which exists as its own component precisely so
        // there is a state with one message in it that a test can produce - the screen's own
        // conversation only ever arrives over a live connection.
        var sentAt = new DateTimeOffset(2026, 9, 7, 14, 35, 0, TimeSpan.Zero);

        var html = await RenderLineAsync(
            new ChatMessageView(1, 5, "Ігор Ковальчук", sentAt, "Забери вантаж."),
            viewerUserId: 3);

        Assert.Contains("Ігор Ковальчук", html, StringComparison.Ordinal);
        Assert.Contains("Забери вантаж.", html, StringComparison.Ordinal);

        // The instant, formatted in the culture the product runs in (NFR-15) rather than the
        // machine's - so this is the time a Ukrainian reader actually sees.
        Assert.Contains(
            sentAt.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("uk-UA")),
            html,
            StringComparison.Ordinal);

        // And the day is part of it. Chat keeps its whole history, so a line rendered as a bare
        // clock reading is a line from any day at all being read as one from today.
        Assert.Contains(
            sentAt.ToLocalTime().ToString("d", CultureInfo.GetCultureInfo("uk-UA")),
            html,
            StringComparison.Ordinal);

        // Somebody else's line, so it is not set apart.
        Assert.DoesNotContain(ChatViews.OwnLineClassName, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_readers_own_line_is_set_apart_and_names_them_as_themselves()
    {
        var html = await RenderLineAsync(
            new ChatMessageView(2, 3, "Петро Шевченко", DateTimeOffset.UnixEpoch, "Виїхав."),
            viewerUserId: 3);

        Assert.Contains(ChatViews.OwnLineClassName, html, StringComparison.Ordinal);

        // And it says "you" rather than the reader's own name. A conversation with two participants
        // has one name worth printing, and printing both makes a reader look for the difference
        // between them on every line. Rendered rather than read out of the file: the catalogue check
        // scans source text, so a component that stopped taking this branch would still contain the
        // key and every other assertion here would still pass.
        Assert.Contains(Ukrainian("ChatYou"), html, StringComparison.Ordinal);
        Assert.DoesNotContain("Петро Шевченко", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_whose_sender_was_deleted_renders_the_unattributed_label()
    {
        // AD-20's set-null, seen from the screen: the account is gone and the line is not. The
        // capability hands back an empty name because the label is a sentence the catalogue owns,
        // and this is where that sentence appears.
        var html = await RenderLineAsync(
            new ChatMessageView(3, null, string.Empty, DateTimeOffset.UnixEpoch, "Заберіть накладну."),
            viewerUserId: 3);

        Assert.Contains(Ukrainian("ChatUnknownSender"), html, StringComparison.Ordinal);
        Assert.Contains("Заберіть накладну.", html, StringComparison.Ordinal);

        // Nobody's own line: a null sender comparing equal to a viewer's id would mark every
        // unattributed line in the conversation as the reader's.
        Assert.DoesNotContain(ChatViews.OwnLineClassName, html, StringComparison.Ordinal);
    }

    // =====================================================================================
    // The hub's own convention
    // =====================================================================================

    [Fact]
    public void Every_hub_method_adopts_its_caller_before_it_does_anything_else()
    {
        // The whole authorization story rests on this line. A hub scope has no HttpContext and no
        // circuit, so a method that does not hand Context.User to HubCaller reads as anonymous and
        // is refused with AUTH_UNAUTHENTICATED while looking perfectly correct - or, worse, would
        // have read some other source had one been available.
        //
        // Enforced rather than remembered: the next method added to this hub either adopts, or this
        // fails and says which one did not.
        var source = File.ReadAllText(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Hubs", "ChatHub.cs"));

        var methods = typeof(ChatHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.DeclaringType == typeof(ChatHub) && !method.IsSpecialName)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Vacuity guard: a reflection filter that matched nothing would make the loop below pass for
        // a hub with no adopting method at all.
        Assert.Equal(["JoinThread", "LeaveThread", "ListThreads", "Send"], methods);

        var offenders = methods
            .Where(name => !BodyOf(source, name).Contains("Adopt();", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// An instant on the reader's own clock, which is the clock the day separators are reckoned
    /// against. Written this way rather than at offset zero so the assertions say the same thing on
    /// every machine that runs them.
    /// </summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour) =>
        new(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Local));

    /// <summary>Renders one roster row, with the given conversation open.</summary>
    private static Task<string> RenderRowAsync(ChatThreadSummaryView summary, int? openDriverId) =>
        ComponentRenderer.RenderAsync<ChatRosterRow>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [nameof(ChatRosterRow.Summary)] = summary,
                [nameof(ChatRosterRow.OpenDriverId)] = openDriverId,
                [nameof(ChatRosterRow.OnOpen)] = (Func<Task>)(() => Task.CompletedTask),
            });

    /// <summary>Renders one day separator, as it is read at the given present.</summary>
    private static Task<string> RenderSeparatorAsync(DateTimeOffset sentAt, DateTimeOffset now) =>
        ComponentRenderer.RenderAsync<ChatDaySeparator>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [nameof(ChatDaySeparator.SentAt)] = sentAt,
                [nameof(ChatDaySeparator.Now)] = now,
            });

    /// <summary>Renders one conversation line as the given viewer sees it.</summary>
    private static Task<string> RenderLineAsync(ChatMessageView message, int viewerUserId) =>
        ComponentRenderer.RenderAsync<ChatLine>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [nameof(ChatLine.Message)] = message,
                [nameof(ChatLine.ViewerUserId)] = viewerUserId,
            });

    /// <summary>
    /// The Ukrainian text behind a <c>UiText</c> key, read from the catalogue rather than written
    /// out here — so a render assertion fails when the screen stops showing something, not when
    /// somebody rewords a sentence.
    /// </summary>
    private static string Ukrainian(string key)
    {
        var path = Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Resources", "UiText.resx");

        var value = XDocument.Load(path)
            .Root!
            .Elements("data")
            .FirstOrDefault(entry => entry.Attribute("name")?.Value == key)
            ?.Element("value")
            ?.Value;

        Assert.True(value is not null, $"UiText.resx has no entry for {key}.");

        return value!;
    }

    /// <summary>
    /// The body of a method in a C# source file, braces balanced from its signature.
    /// <para>
    /// Read from the source rather than walked as IL because the claim is about what the method
    /// itself says: <c>Adopt</c> is a private call in each body, and the point is that each body
    /// makes it rather than that some frame below does.
    /// </para>
    /// </summary>
    private static string BodyOf(string source, string methodName)
    {
        var signature = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);

        Assert.True(signature >= 0, $"ChatHub.cs declares no method named {methodName}.");

        var open = source.IndexOf('{', signature);

        Assert.True(open >= 0, $"ChatHub.{methodName} has no body.");

        var depth = 0;

        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[open..index];
            }
        }

        Assert.Fail($"ChatHub.{methodName}'s body is not closed.");

        return string.Empty;
    }
}
