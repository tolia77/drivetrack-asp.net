using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Deliveries;

/// <summary>The two kinds of work a delivery operation leaves behind (AD-12).</summary>
public enum DeliverySideEffectKind
{
    /// <summary>Fill in whichever of the delivery's two points has no address yet (FR-94).</summary>
    ResolveAddresses,

    /// <summary>Tell the client their delivery moved, and record the attempt (FR-28).</summary>
    StatusChangeEmail,
}

/// <summary>
/// One queued side effect, carrying the row id rather than the entity.
/// <para>
/// An id and not a <c>Delivery</c>: the entity belongs to the scope that loaded it, and that scope
/// is disposed by the time the job runs. The job reloads through its own unit of work, which is
/// also what makes it correct for a delivery changed twice in quick succession — the second run
/// sees the second state rather than a snapshot of the first.
/// </para>
/// </summary>
/// <param name="Kind">Which piece of work this is.</param>
/// <param name="DeliveryId">The delivery the work is about.</param>
/// <param name="Previous">The status held before the move, or null for a job that is not about one.</param>
/// <param name="Next">The status held after the move, or null for the same reason.</param>
public sealed record DeliverySideEffectJob(
    DeliverySideEffectKind Kind,
    int DeliveryId,
    DeliveryStatus? Previous,
    DeliveryStatus? Next);
