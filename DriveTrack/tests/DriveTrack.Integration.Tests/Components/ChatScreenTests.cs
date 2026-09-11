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

        Assert.Equal(ChatViews.LineClassName, ChatViews.LineClass(senderUserId: 4, viewerUserId: 3));

        // AD-20: a deleted sender is nobody's own line. Null comparing equal to a viewer's id would
        // mark every unattributed line in the conversation as the reader's own.
        Assert.Equal(ChatViews.LineClassName, ChatViews.LineClass(senderUserId: null, viewerUserId: 3));
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
        // Both halves of the screen: the page and the line it renders per message.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor")
            + SharedMarkup.ReadComponent("Pages", "ChatLine.razor");

        foreach (var decision in new[]
                 {
                     "ChatViews.IsParticipant",
                     "ChatViews.ShowsRoster",
                     "ChatViews.NamesTheDispatchDesk",
                     "ChatViews.NamesTheSelectedDriver",
                     "ChatViews.AsksForAThread",
                     "ChatViews.LineClass",
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
    public async Task The_readers_own_line_is_set_apart_when_it_is_rendered()
    {
        var html = await RenderLineAsync(
            new ChatMessageView(2, 3, "Петро Шевченко", DateTimeOffset.UnixEpoch, "Виїхав."),
            viewerUserId: 3);

        Assert.Contains(ChatViews.OwnLineClassName, html, StringComparison.Ordinal);
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
