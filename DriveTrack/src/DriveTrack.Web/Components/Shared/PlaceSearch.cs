using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Web.Account;

namespace DriveTrack.Web.Components.Shared;

/// <summary>
/// One address box beside a map (FR-104): what was typed, what it found, whether a lookup is in
/// flight, and what choosing a match does.
/// <para>
/// Two forms build a pair of these — the dispatch board's <c>DeliveryForm</c> and the client's
/// request form on <c>/my-deliveries</c> — and <see cref="DtLocationPicker" /> renders whichever it
/// is handed. It lives here rather than nested in the screen that came first, which is what used to
/// force <c>/my-deliveries</c> to alias a type out of another page. It knows nothing about either.
/// </para>
/// </summary>
/// <remarks>
/// Free of the framework for the reason the screens' pure functions are static: static rendering
/// dispatches no events, so no render test can press this button or click one of its answers. Every
/// decision the box makes therefore lives here, where it can be driven directly — including the one
/// that is easy to get wrong and impossible to see in a diff, which is which point a box fills in.
/// <para>
/// Public rather than internal only because <see cref="DtLocationPicker" /> takes one as a
/// parameter and a component's generated class is public: a parameter cannot be less accessible
/// than the component that declares it. Nothing outside this assembly constructs one.
/// </para>
/// <para>
/// The port is a parameter of <see cref="RunAsync" /> rather than a constructor dependency, because
/// the two ends of this box are owned by different things. A form's own constructor builds it, and
/// a form is built in places that have no service to hand — a test constructs one with
/// <c>new DeliveryForm()</c>. What runs it is <see cref="DtLocationPicker" />, which injects
/// <c>IDeliveryService</c> and passes the search method in at the call. So the form settles which
/// point the box fills and the picker settles where its answers come from, and neither has to know
/// the other's half.
/// </para>
/// </remarks>
/// <param name="assign">Where a chosen match's point goes. The whole identity of the box.</param>
public sealed class PlaceSearch(Action<MapLocation> assign)
{
    /// <summary>What the caller typed. Bound two-way to the box.</summary>
    public string? Query { get; set; }

    /// <summary>What the last search found. Empty before the first one.</summary>
    public IReadOnlyList<PlaceMatch> Matches { get; private set; } = [];

    /// <summary>True while a lookup is in flight.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>True once a search has run at all, whatever it answered.</summary>
    public bool HasSearched { get; private set; }

    /// <summary>
    /// Why the last search was refused, or empty when it was not. This box's own sink, and the
    /// whole of DW-41.
    /// <para>
    /// It used to be the screen's single <c>_failures</c> field, which the submit path also
    /// writes: a successful address search then silently erased a refusal the client had not
    /// read yet, and the two address boxes erased each other's. A box that holds its own answer
    /// cannot do that to anybody else's.
    /// </para>
    /// </summary>
    public IReadOnlyList<ScreenFailure> Failures { get; private set; } = [];

    /// <summary>
    /// True when a search has run and matched nothing — which is a different state from "no
    /// search has run yet", and the only one of the two with something to say.
    /// </summary>
    /// <remarks>
    /// A refusal empties <see cref="Matches"/> too, so without the third clause a refused
    /// search would be a found-nothing search as well - and the box's own banner sits directly
    /// above this line, telling a dispatcher two different things about one click.
    /// </remarks>
    public bool FoundNothing => HasSearched && Matches.Count == 0 && Failures.Count == 0;

    /// <summary>
    /// Runs the search, leaving the answer in <see cref="Matches"/> and any refusal in
    /// <see cref="Failures"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="Task"/> rather than the failures, deliberately: a caller that is
    /// handed them can put them anywhere, and where they used to be put is what DW-41 is about.
    /// With nothing to assign, a search can only ever write this box's own sink.
    /// </remarks>
    /// <param name="search">The capability's search method.</param>
    /// <param name="cancellationToken">The screen's own token.</param>
    public async Task RunAsync(
        Func<SearchPlacesQuery, CancellationToken, Task<IReadOnlyList<PlaceMatch>>> search,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);

        // The button is disabled while this is set. A lookup is bounded only by the geocoder's
        // own timeout, so without it the button looks like it did nothing and an impatient
        // second click is a second request to a public service.
        IsBusy = true;

        try
        {
            Matches = await search(new SearchPlacesQuery(Query), cancellationToken);

            // Cleared only on the way out of a successful lookup, so a refusal stays on screen
            // until this box itself has something better to say.
            Failures = [];
        }
        catch (DriveTrackException failure)
        {
            Matches = [];
            Failures = FailureKeys.For(failure);
        }
        finally
        {
            // Both in a finally, so a refusal leaves the box usable and says so rather than
            // leaving it disabled and looking as though the button had done nothing.
            HasSearched = true;
            IsBusy = false;
        }
    }

    /// <summary>Applies a match to the point this box is about.</summary>
    public void Choose(PlaceMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        // The same assignment a map click makes. DtMap re-centres when its Value changes, so the
        // map follows the chosen address without this having to tell it to (FR-104, FR-15).
        assign(match.Point);

        // Cleared on the way out: a list left standing beside a map that has already moved
        // invites a second click on a match that has already been applied.
        Matches = [];
        HasSearched = false;
    }
}
