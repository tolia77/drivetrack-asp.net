using System.Threading.Channels;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Infrastructure.SideEffects;

/// <summary>
/// The dispatch half of AD-12: an in-process queue behind <see cref="IDeliverySideEffects"/>.
/// <para>
/// Unbounded, and the choice is the one FR-95 forces. A bounded channel has to do something when it
/// is full, and every option is worse than the problem: blocking would make the delivery write wait
/// on a background queue, and dropping would lose a notification FR-28 requires to be recorded.
/// The queue only ever holds two small records per delivery operation, so the depth an unbounded
/// channel could reach is the depth the database could be written at.
/// </para>
/// <para>
/// A singleton, deliberately shared with the worker as a concrete type rather than only through its
/// interface: the interface is what a delivery operation is allowed to see, and the reader and the
/// completion signal are the worker's business.
/// </para>
/// </summary>
public sealed class DeliverySideEffectQueue : IDeliverySideEffects
{
    private readonly Channel<DeliverySideEffectJob> _channel =
        Channel.CreateUnbounded<DeliverySideEffectJob>(new UnboundedChannelOptions
        {
            // One worker reads it. Saying so lets the channel take its single-reader path, and says
            // in the type what the hosted service arranges.
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Lock _gate = new();

    /// <summary>Queued but not yet finished, counted under <see cref="_gate"/>.</summary>
    private int _outstanding;

    /// <summary>
    /// Completed when <see cref="_outstanding"/> next falls to zero, or null while there is nothing
    /// outstanding. Null rather than a completed source, so an idle queue answers without allocating
    /// and without a stale source to reset.
    /// </summary>
    private TaskCompletionSource? _drained;

    /// <summary>The jobs, for the worker that runs them.</summary>
    public ChannelReader<DeliverySideEffectJob> Reader => _channel.Reader;

    /// <inheritdoc />
    public void ResolveAddresses(int deliveryId) =>
        Enqueue(new DeliverySideEffectJob(
            DeliverySideEffectKind.ResolveAddresses,
            deliveryId,
            Previous: null,
            Next: null));

    /// <inheritdoc />
    public void NotifyStatusChanged(int deliveryId, DeliveryStatus previous, DeliveryStatus next) =>
        Enqueue(new DeliverySideEffectJob(
            DeliverySideEffectKind.StatusChangeEmail,
            deliveryId,
            previous,
            next));

    /// <summary>
    /// Records that a job has finished, however it finished. Called by the worker in a
    /// <c>finally</c>, so a job that threw still releases whoever is waiting on the drain.
    /// </summary>
    public void Completed()
    {
        TaskCompletionSource? drained = null;

        lock (_gate)
        {
            if (_outstanding > 0 && --_outstanding == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        // Outside the lock: the continuations this releases are somebody else's code, and running
        // them while holding the gate is how a queue acquires a deadlock nobody can reproduce.
        drained?.TrySetResult();
    }

    /// <summary>
    /// Completes when nothing is queued or running.
    /// <para>
    /// This exists for the tests, and it says so rather than pretending otherwise. The alternative
    /// is a test that sleeps: a delay long enough to be reliable on a loaded machine is a delay paid
    /// by every run, and one short enough to be quick is a test that fails for no reason a few times
    /// a month. Waiting on the queue's own signal is neither.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait; the jobs keep running.</param>
    public Task WhenIdleAsync(CancellationToken cancellationToken)
    {
        Task drained;

        lock (_gate)
        {
            if (_outstanding == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _drained.Task;
        }

        return drained.WaitAsync(cancellationToken);
    }

    private void Enqueue(DeliverySideEffectJob job)
    {
        lock (_gate)
        {
            // Counted before it is written, so a worker fast enough to finish the job before this
            // method returns cannot drive the count below zero.
            _outstanding++;
        }

        if (!_channel.Writer.TryWrite(job))
        {
            // Unreachable for an unbounded channel that is never completed, and handled anyway: a
            // job that was counted and not queued would leave WhenIdleAsync waiting for ever.
            Completed();
        }
    }
}
