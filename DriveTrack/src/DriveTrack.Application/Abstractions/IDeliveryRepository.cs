using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Abstractions;

/// <summary>Persistence for <see cref="Delivery"/>. See <see cref="IClientRepository"/> for the shape rules (AD-6).</summary>
public interface IDeliveryRepository
{
    /// <summary>Loads a delivery, or null when there is none with that id.</summary>
    Task<Delivery?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Stages a new delivery for the next commit.</summary>
    void Add(Delivery delivery);

    /// <summary>Stages a delivery for deletion (FR-24). Its timeline, proof, review and notification attempts go with it (DR-9).</summary>
    void Remove(Delivery delivery);
}
