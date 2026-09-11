using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// What the timeline panel actually renders (FR-105 to FR-108).
/// <para>
/// A source scan cannot make these claims. "A viewer who may not be told the actor's name is shown
/// their role instead" and "the buttons are exactly the moves the lifecycle allows" are properties
/// of the output, and the file contains all the same words whether they hold or not.
/// </para>
/// <para>
/// Rendering is static, so nothing here presses a button. What is asserted is what a viewer is
/// offered — which is the half FR-12 is about, since pressing one is refused or allowed by
/// <c>IAccessGuard</c> and the API suite asserts that.
/// </para>
/// </summary>
public class DeliveryTimelineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The label each status renders, as the catalogue holds it. Written out rather than resolved
    /// through a localizer, because what is under test is that the component reaches the right key:
    /// asserting against the same localizer the component reads would pass for every wiring.
    /// </summary>
    private static readonly Dictionary<DeliveryStatus, string> Labels = new()
    {
        [DeliveryStatus.Pending] = "Очікує",
        [DeliveryStatus.InTransit] = "У дорозі",
        [DeliveryStatus.Delivered] = "Доставлено",
        [DeliveryStatus.Failed] = "Не вручено",
    };

    [Fact]
    public async Task An_empty_timeline_says_it_is_empty_rather_than_rendering_nothing()
    {
        // FR-84's rule reaching the panel: a blank rectangle is indistinguishable from a panel that
        // failed to load, and a delivery nobody has touched yet is the ordinary case.
        var html = await RenderAsync([]);

        Assert.Contains("Історія цієї доставки поки порожня.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_names_both_statuses_with_the_localized_labels()
    {
        // FR-105: the line reads as a move rather than as a destination, and both ends are the
        // shared status vocabulary rather than a second copy of it.
        var html = await RenderAsync([Change("Тарас Шевченко", UserRole.Dispatcher)]);

        Assert.Contains("Очікує", html, StringComparison.Ordinal);
        Assert.Contains("У дорозі", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_only_entry_renders_its_note_and_no_status()
    {
        // FR-107: an entry may carry only a note, and the panel must not invent a transition for it.
        var html = await RenderAsync([Note("Залишено на рецепції")]);

        Assert.Contains("Залишено на рецепції", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Очікує", html, StringComparison.Ordinal);
        Assert.DoesNotContain("У дорозі", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_withheld_actor_shows_a_role_and_no_personal_name()
    {
        // FR-27 and FR-96 reaching the timeline. The service decided; this asserts the panel renders
        // that decision rather than quietly dropping the actor altogether, which would make the
        // history read as though nobody had done anything.
        var html = await RenderAsync([Change(actorName: null, UserRole.Dispatcher)]);

        Assert.DoesNotContain("Шевченко", html, StringComparison.Ordinal);
        Assert.Contains("Диспетчер", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disclosed_actor_shows_the_name()
    {
        var html = await RenderAsync([Change("Тарас Шевченко", UserRole.Dispatcher)]);

        Assert.Contains("Тарас Шевченко", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_role_a_snapshot_can_hold_has_a_label()
    {
        // Four independent @if blocks and four literal keys, so a role added to the enum without a
        // label renders an anonymous line rather than failing loudly. Walked rather than listed as
        // theory data, so a fifth role is covered here without anyone remembering to add a row -
        // which is the whole claim, and the thing hard-coded rows could not make.
        var roles = Enum.GetValues<UserRole>();

        Assert.NotEmpty(roles);

        foreach (var role in roles)
        {
            var html = await RenderAsync([Change(actorName: null, role)]);

            var actor = SharedMarkup.TextOf(
                SharedMarkup.ElementWithClass(html, "div", "dt-timeline-meta"));

            Assert.True(
                SharedMarkup.IsUkrainian(actor),
                $"The entry for role {role} names no actor: it rendered '{actor}'.");
        }
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.InTransit)]
    [InlineData(DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.Delivered)]
    public async Task The_buttons_are_exactly_the_moves_the_lifecycle_allows(DeliveryStatus status)
    {
        // AD-10 read rather than restated: the panel offers whatever Delivery.NextStatuses answers,
        // so the screen cannot drift from the rule the service enforces. Delivered offers nothing,
        // which is what "terminal" looks like to a user.
        //
        // Which statuses, not just how many. Counting alone would pass a panel that offered the one
        // wrong move from Pending - a driver would press "Доставлено" and get a 409 for a button the
        // screen drew.
        var expected = Delivery.NextStatuses(status);
        var html = await RenderAsync([], status, allowStatusChange: true);

        var actions = SharedMarkup.ElementWithClass(html, "div", "dt-timeline-actions");

        Assert.Equal(expected.Count, SharedMarkup.Occurrences(actions, "dt-timeline-status"));

        foreach (var offered in Enum.GetValues<DeliveryStatus>())
        {
            Assert.Equal(
                expected.Contains(offered),
                actions.Contains(Labels[offered], StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_buttons_follow_the_history_rather_than_the_status_the_panel_opened_with()
    {
        // The refusal path, at the surface a viewer sees it. A status change is refused with a 409
        // when somebody else moved the delivery first, and the panel answers by reloading; if that
        // reload replaced only the list, the buttons would still be the ones that were just
        // rejected and every retry would fail identically with nothing on screen having changed.
        //
        // Static rendering cannot press a button, but it can arrange the same state: a panel opened
        // on Pending whose history ends at InTransit is exactly what the reload leaves behind.
        var html = await RenderAsync(
            [Change(actorName: null, UserRole.Dispatcher)],
            DeliveryStatus.Pending,
            allowStatusChange: true);

        var actions = SharedMarkup.ElementWithClass(html, "div", "dt-timeline-actions");

        foreach (var offered in Enum.GetValues<DeliveryStatus>())
        {
            Assert.Equal(
                Delivery.NextStatuses(DeliveryStatus.InTransit).Contains(offered),
                actions.Contains(Labels[offered], StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_history_that_moved_nothing_leaves_the_status_where_it_was()
    {
        // The fallback arm, and the reason the walk skips entries rather than reading the last one:
        // FR-107 lets anyone append a note, so the newest entry is routinely one that carries no
        // status at all. Reading it as the current status would move the delivery backwards on
        // screen every time somebody said something about it.
        var notes = new[] { Note("Немає нікого вдома"), Note("Спробуємо завтра") };

        Assert.Equal(
            DeliveryStatus.Failed,
            Web.Components.Pages.DeliveryTimeline.LatestStatus(notes, DeliveryStatus.Failed));

        Assert.Equal(
            DeliveryStatus.Pending,
            Web.Components.Pages.DeliveryTimeline.LatestStatus([], DeliveryStatus.Pending));
    }

    [Fact]
    public void The_newest_entry_that_moved_the_delivery_is_the_one_that_counts()
    {
        // Oldest-first (FR-108), so the walk runs from the end - and it has to pass over the note
        // that was appended after the move to find it.
        IReadOnlyList<TimelineEntryView> history =
        [
            Change(actorName: null, UserRole.Dispatcher),
            new(3, null, UserRole.Driver, DeliveryStatus.InTransit, DeliveryStatus.Failed, null, Noon),
            Note("Залишено на рецепції"),
        ];

        Assert.Equal(
            DeliveryStatus.Failed,
            Web.Components.Pages.DeliveryTimeline.LatestStatus(history, DeliveryStatus.Pending));
    }

    [Fact]
    public async Task Every_status_the_enum_declares_renders_a_label()
    {
        // The other half of AD-10's closed set. Delivery.NextStatuses throws for a status nobody
        // declared a transition for; the label component answers an empty span for one nobody
        // declared a label for, and that empty span reaches both delivery tables and every timeline
        // line. Walking the enum is what makes the two halves fail together.
        var statuses = Enum.GetValues<DeliveryStatus>();

        Assert.NotEmpty(statuses);

        foreach (var status in statuses)
        {
            var html = await ComponentRenderer.RenderAsync<Web.Components.Pages.DeliveryStatusLabel>(
                new Dictionary<string, object?> { ["Status"] = status });

            var label = SharedMarkup.TextOf(html);

            Assert.True(
                SharedMarkup.IsUkrainian(label),
                $"The status {status} renders no label: it rendered '{label}'.");

            // And it is the label this suite reads elsewhere, so the table below cannot drift from
            // the catalogue while both keep passing.
            Assert.Equal(Labels[status], label);
        }
    }

    [Fact]
    public void The_label_table_covers_every_status()
    {
        // Vacuity guard for the two assertions above: a status missing from this table would make
        // them throw rather than fail, and a table that has fallen behind the enum is the thing that
        // would be wrong.
        Assert.Equal(Enum.GetValues<DeliveryStatus>().Length, Labels.Count);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.InTransit)]
    [InlineData(DeliveryStatus.Failed)]
    public async Task A_viewer_with_no_lifecycle_action_is_offered_no_button(DeliveryStatus status)
    {
        // FR-90 at the component tier: a client sees the history and the note field and nothing to
        // press. It hides rather than decides - IAccessGuard refuses the request either way - but
        // offering an action that could only ever be refused is worse than offering none.
        var html = await RenderAsync([], status, allowStatusChange: false);

        Assert.DoesNotContain("dt-timeline-status", html, StringComparison.Ordinal);

        // And the note field is still there, because FR-107 is open to everyone who can see the
        // delivery. Losing it with the buttons would be the easy mistake.
        Assert.Contains("dt-timeline-add-note", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_the_panel_renders_is_written_in_Latin()
    {
        // NFR-14 on the rendered chrome rather than on the source: a key that failed to resolve
        // hands back its own name, which is Latin here and nowhere else.
        var html = await RenderAsync(
            [Change(actorName: null, UserRole.Driver), Note("Залишено на рецепції")],
            DeliveryStatus.InTransit,
            allowStatusChange: true);

        var texts = SharedMarkup.TextOf(html)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(text => text.Length > 0)
            .ToArray();

        Assert.NotEmpty(texts);
        Assert.DoesNotContain(texts, SharedMarkup.HasLatinWord);
    }

    private static Task<string> RenderAsync(
        IReadOnlyList<TimelineEntryView> entries,
        DeliveryStatus status = DeliveryStatus.Pending,
        bool allowStatusChange = false) =>
        ComponentRenderer.RenderAsync<Web.Components.Pages.DeliveryTimeline>(
            new Dictionary<string, object?>
            {
                ["DeliveryId"] = 1,
                ["Status"] = status,
                ["AllowStatusChange"] = allowStatusChange,
            },
            services => services.AddSingleton<IDeliveryService>(new StubTimeline(entries)));

    /// <summary>A status change, with the actor disclosed or withheld.</summary>
    private static TimelineEntryView Change(string? actorName, UserRole role) =>
        new(1, actorName, role, DeliveryStatus.Pending, DeliveryStatus.InTransit, null, Noon);

    /// <summary>A note-only entry: neither status, which is the shape FR-107 asks for.</summary>
    private static TimelineEntryView Note(string note) =>
        new(2, null, UserRole.Client, null, null, note, Noon);

    /// <summary>
    /// A delivery capability that answers a fixed timeline and records nothing. Static rendering
    /// dispatches no events, so the two write members are never reached from here.
    /// </summary>
    private sealed class StubTimeline(IReadOnlyList<TimelineEntryView> entries) : IDeliveryService
    {
        public Task<IReadOnlyList<TimelineEntryView>> ListTimelineAsync(
            int id,
            CancellationToken cancellationToken) =>
            Task.FromResult(entries);

        public Task<TimelineEntryView> ChangeStatusAsync(
            int id,
            ChangeDeliveryStatusCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Static rendering dispatches no events.");

        public Task<TimelineEntryView> AddNoteAsync(
            int id,
            AddDeliveryNoteCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Static rendering dispatches no events.");

        public Task<IReadOnlyList<DeliverySummary>> ListAsync(
            ListDeliveriesQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The panel reads only the timeline.");

        public Task<IReadOnlyList<AssignedDeliverySummary>> ListMineAsync(
            ListDeliveriesQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The panel reads only the timeline.");

        public Task<DeliverySummary> GetAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The panel reads only the timeline.");

        public Task<DeliverySummary> CreateAsync(
            CreateDeliveryCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The panel reads only the timeline.");

        public Task<DeliverySummary> UpdateAsync(
            int id,
            UpdateDeliveryCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The panel reads only the timeline.");

        public Task DeleteAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The panel reads only the timeline.");

        public Task<IReadOnlyList<PlaceMatch>> SearchPlacesAsync(
            SearchPlacesQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The panel reads only the timeline.");
    }
}
