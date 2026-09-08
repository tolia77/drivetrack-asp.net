namespace DriveTrack.Application.Abstractions;

/// <summary>
/// The per-operation persistence scope (AD-5). It owns one short-lived database context and one
/// transaction, and exposes the repositories that write through them.
/// <para>
/// One operation, one commit. Disposing without committing rolls the transaction back, so a
/// multi-step write either lands whole or not at all (NFR-9). Nothing injects a context
/// directly — a scoped context on a Blazor Server circuit lives for hours, accumulates tracked
/// entities and throws on concurrent renders, which is the trap this shape exists to avoid.
/// </para>
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>
    /// User accounts (FR-1, FR-4, FR-9). AD-5: Identity writes go through this scope and this
    /// commit, so an account and its subtype row can never be half-created.
    /// </summary>
    IUserAccountRepository Users { get; }

    /// <summary>Clients (FR-47).</summary>
    IClientRepository Clients { get; }

    /// <summary>Drivers (FR-39).</summary>
    IDriverRepository Drivers { get; }

    /// <summary>Vehicles (FR-43).</summary>
    IVehicleRepository Vehicles { get; }

    /// <summary>Deliveries (FR-24).</summary>
    IDeliveryRepository Deliveries { get; }

    /// <summary>Driver shifts (FR-110).</summary>
    IShiftRepository Shifts { get; }

    /// <summary>Reviews (FR-62, FR-66).</summary>
    IReviewRepository Reviews { get; }

    /// <summary>Proof of delivery (FR-119).</summary>
    IProofOfDeliveryRepository ProofOfDeliveries { get; }

    /// <summary>Notification attempts (FR-28).</summary>
    INotificationAttemptRepository NotificationAttempts { get; }

    /// <summary>The append-only delivery timeline (AD-27).</summary>
    ITimelineEntryRepository TimelineEntries { get; }

    /// <summary>Chat messages (AD-15).</summary>
    IMessageRepository Messages { get; }

    /// <summary>
    /// Saves every staged change and commits the transaction. Called once per operation; a
    /// second call on the same scope is a programming error.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken);
}
