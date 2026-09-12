using Microsoft.AspNetCore.Components;

namespace DriveTrack.Web.Components.Shared;

/// <summary>
/// A screen whose service calls are scoped to the screen's own lifetime, owned once so no screen
/// can get it wrong.
/// <para>
/// Every interactive screen in this product starts work it may not finish: a user who navigates
/// away mid-load, or closes a dialog mid-save, leaves a query running against a pooled connection,
/// and nothing would ever ask it to stop. Each screen used to answer that with a
/// <see cref="CancellationTokenSource"/> of its own, cancelled <em>and disposed</em> on teardown —
/// and the disposal is the defect. A continuation that resumes after the component is gone and
/// reads the token off a disposed source gets <see cref="ObjectDisposedException"/>, which is not
/// cancellation, is swallowed by nothing, and reaches the renderer as an unhandled exception that
/// takes the whole circuit down with it.
/// </para>
/// <para>
/// So the source is cancelled and deliberately never disposed: a late continuation finds a
/// cancelled token instead of a disposed source, and a cancelled token is something the framework
/// already knows how to unwind quietly. <c>ComponentBase</c> discards a lifecycle task and an
/// event-callback task that ended cancelled, so no screen needs a
/// <c>catch (OperationCanceledException)</c> arm of its own; what it needs is for the token to
/// still be readable, which is exactly what not disposing buys.
/// </para>
/// <para>
/// Not disposing costs nothing here. <see cref="CancellationTokenSource.Dispose()"/> releases a
/// wait handle allocated on the first <see cref="CancellationToken.WaitHandle"/> use — nobody in
/// this product waits on one — and detaches linked registrations, of which there are none. The
/// source is collected with the component.
/// </para>
/// <para>
/// Closes DW-13, DW-22 and DW-42: the eager disposal, the blanket <c>catch</c> in the capture
/// panel that reported a cancelled capture as a failure, and the fourteen copies of one lifetime
/// rule that let the first two happen independently of each other.
/// </para>
/// </summary>
public abstract class CircuitScopedComponent : ComponentBase, IDisposable
{
    // The screen's own lifetime, owned once for every screen.
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>
    /// The token every service call on this screen passes. Cancelled when the renderer disposes
    /// the component, and readable for as long as anything still holds a reference to it.
    /// </summary>
    protected internal CancellationToken CircuitToken => _cancellation.Token;

    /// <summary>
    /// Whether a blanket <c>catch</c> should report this failure to the user, or leave it alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second half of the same answer, and it belongs beside the first: a screen that catches
    /// everything intercepts its own teardown before <c>ComponentBase</c> can discard it, and
    /// reports a panel that is already gone as a failure the user never asked about. A screen that
    /// catches everything <em>except</em> cancellation is back on the common path.
    /// </para>
    /// <para>
    /// The token is read rather than the type alone, because the two are not the same claim. An
    /// <see cref="OperationCanceledException"/> raised while this screen is still alive came from
    /// somewhere else — an outbound port's own timeout, say — and is a real failure the user has to
    /// be told about. What is silent is narrower than that but wider than one token: <em>any</em>
    /// cancellation observed once this screen's own teardown has been asked for.
    /// </para>
    /// <para>
    /// That imprecision is deliberate. Matching <see cref="OperationCanceledException.CancellationToken"/>
    /// against <see cref="CircuitToken"/> would be the exact question, and it would be wrong in
    /// practice: a thrower that raises <c>new OperationCanceledException()</c> carries
    /// <see cref="CancellationToken.None"/>, so the exact test would report the very cancellations
    /// this exists to swallow. After teardown there is also nobody left to report to — the panel is
    /// gone and the screen behind it never asked — so the window in which the two answers differ is
    /// a window in which the wider one costs nothing.
    /// </para>
    /// </remarks>
    /// <param name="failure">The exception a blanket <c>catch</c> caught.</param>
    protected internal bool Reports(Exception failure) =>
        failure is not OperationCanceledException || !CircuitToken.IsCancellationRequested;

    /// <inheritdoc />
    public void Dispose()
    {
        // Cancelled, never disposed: a continuation that resumes after teardown must find a
        // cancelled token, not a disposed source - the latter throws ObjectDisposedException
        // into the renderer, which is a torn circuit rather than a quiet unwind.
        _cancellation.Cancel();
        GC.SuppressFinalize(this);
    }
}
