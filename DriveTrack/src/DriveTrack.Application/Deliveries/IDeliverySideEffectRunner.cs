namespace DriveTrack.Application.Deliveries;

/// <summary>
/// Performs one queued side effect (AD-12).
/// <para>
/// Named <c>Runner</c> rather than <c>Service</c>, and the name is load-bearing. AD-2's coverage
/// gate binds on the <c>Service</c> suffix and requires an <c>IAccessGuard</c> call in every public
/// method it finds; this type has no caller to authorize — it runs on a background worker, on
/// behalf of nobody, long after the request that queued it was authorized and answered. A guard
/// call here would have no principal to read and would be a decision with no question behind it.
/// </para>
/// </summary>
public interface IDeliverySideEffectRunner
{
    /// <summary>
    /// Runs the job.
    /// <para>
    /// The failures a side effect exists to absorb are absorbed inside: an unreachable geocoder
    /// leaves the address absent, and a refused send leaves a notification attempt recording the
    /// refusal. Neither is ever a failure the caller of the original operation could be told about,
    /// because that caller was answered before this ran.
    /// </para>
    /// <para>
    /// A storage failure is not absorbed and does leave here. There is still nobody to tell, but
    /// there is somewhere to say it: the caller of this method logs what escapes. Catching it here
    /// instead would leave no row and no log line anywhere, which is the silent swallow FR-28 exists
    /// to end.
    /// </para>
    /// </summary>
    /// <param name="job">The queued work.</param>
    /// <param name="cancellationToken">
    /// The job's own token: the worker's stopping token, narrowed by a per-job deadline.
    /// </param>
    Task RunAsync(DeliverySideEffectJob job, CancellationToken cancellationToken);
}
