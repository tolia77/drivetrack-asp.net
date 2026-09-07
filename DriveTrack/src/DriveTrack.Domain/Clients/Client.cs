using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;

namespace DriveTrack.Domain.Clients;

/// <summary>
/// The client subtype of an application user. Like <c>Driver</c> it exists because it carries
/// a real field of its own; admin and dispatcher do not and get no table (DR-3).
/// </summary>
public sealed class Client
{
    /// <summary>Surrogate key of the client row, distinct from <see cref="UserId"/> (AD-22).</summary>
    public ClientId Id { get; set; }

    /// <summary>
    /// The application user this client is. Required and unique — one client row per user.
    /// </summary>
    public required UserId UserId { get; set; }

    /// <summary>Contact number in international form.</summary>
    public required string PhoneNumber { get; set; }

    /// <summary>Deliveries this client requested. Survives the client's deletion with a null owner (FR-47).</summary>
    public ICollection<Delivery> Deliveries { get; } = [];

    /// <summary>Reviews this client wrote. Deleted with the client (FR-47).</summary>
    public ICollection<Review> Reviews { get; } = [];
}
