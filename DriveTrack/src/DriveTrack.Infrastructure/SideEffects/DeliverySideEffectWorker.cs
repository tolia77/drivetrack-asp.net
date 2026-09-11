using DriveTrack.Application.Deliveries;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveTrack.Infrastructure.SideEffects;

/// <summary>
/// The running half of AD-12: one background loop draining
/// <see cref="DeliverySideEffectQueue"/> and handing each job to
/// <see cref="IDeliverySideEffectRunner"/>.
/// <para>
/// Every job runs on a token of this service's own — its stopping token, narrowed by a per-job
/// deadline — and never on the token of the request that queued it. That one was cancelled when the
/// response was written, which is before the work here begins, so passing it on would cancel every
/// side effect the instant it became due.
/// </para>
/// <para>
/// One loop rather than a pool. Nothing here is throughput-sensitive: a delivery write leaves at
/// most two jobs behind, and running them in order keeps two changes to the same delivery arriving
/// at the geocoder in the order they were made.
/// </para>
/// <para>
/// Public, like <c>UnitOfWork</c> and the queue beside it, so a registration test can name the type
/// rather than compare a string to its name. "The hosted loop that drains the queue is registered"
/// is a claim worth pinning: without it a delivery operation queues work nothing ever performs, and
/// nothing fails loudly enough to say so — the write the caller waited for succeeded either way.
/// </para>
/// </summary>
public sealed class DeliverySideEffectWorker(
    DeliverySideEffectQueue queue,
    IDeliverySideEffectRunner runner,
    ILogger<DeliverySideEffectWorker> logger) : BackgroundService
{
    /// <summary>
    /// The longest a single job may take before it is abandoned.
    /// <para>
    /// Without it the loop has no deadline of its own, and one job can hold it for the life of the
    /// process. That is not hypothetical: <c>SmtpClient.Timeout</c> does not apply to
    /// <c>SendMailAsync</c>, so a relay that accepts the connection and then never answers blocks
    /// the single reader forever, and every later delivery in the system is silently never geocoded
    /// and never notified — with nothing failing anywhere to say so.
    /// </para>
    /// <para>
    /// Generous rather than tight: it is a backstop for work that has stopped answering, not a
    /// service-level target. A geocoder has its own, shorter timeout.
    /// </para>
    /// </summary>
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                // Linked, so the job is abandoned by whichever comes first: the host stopping, or
                // the job running out of time. Its own source per job, because a deadline shared
                // across jobs is a deadline that has already expired by the second one.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(JobTimeout);

                try
                {
                    await runner.RunAsync(job, deadline.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // The host is stopping, and this job went with it. Ordinary shutdown rather than
                    // a defect: the work is by definition allowed to be absent (DR-11, FR-95), and
                    // logging an error for every job in flight at shutdown would teach whoever reads
                    // the log to ignore this message.
                }
                catch (Exception exception)
                {
                    // The runner contains the two failures it exists to answer for - a geocoder that
                    // would not answer, a relay that refused the message - so what reaches here is a
                    // storage failure or a job that ran out of time. Logged, because otherwise it
                    // leaves no trace at all, and caught, because an exception that escaped would end
                    // the only reader this queue has: every later delivery in the process would
                    // silently stop being geocoded and notified. One lost job is better than all of
                    // them.
                    logger.LogError(
                        exception,
                        "A delivery side effect of kind {Kind} for delivery {DeliveryId} failed.",
                        job.Kind,
                        job.DeliveryId);
                }
                finally
                {
                    // However it ended. A job that threw is a job that is no longer outstanding,
                    // and leaving it counted would hang anything waiting for the queue to drain.
                    queue.Completed();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping while the loop was waiting for its next job. Whatever is still
            // queued dies with the process, which is the right trade for work that is by definition
            // allowed to be absent (DR-11, FR-95).
        }
    }
}
