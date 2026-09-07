using DriveTrack.Domain.Identity;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DriveTrack.Infrastructure.Persistence.Converters;

/// <summary>
/// Stores a <see cref="UserId"/> as the <c>integer</c> it wraps.
/// <para>
/// Registered once through <c>ConfigureConventions</c> rather than per property, so every use
/// site converts identically and a new entity cannot acquire a differently-shaped column by
/// forgetting a line (AD-22).
/// </para>
/// </summary>
public sealed class UserIdConverter() : ValueConverter<UserId, int>(id => id.Value, value => new UserId(value));

/// <summary>Stores a <see cref="DriverId"/> as the <c>integer</c> it wraps. See <see cref="UserIdConverter"/>.</summary>
public sealed class DriverIdConverter() : ValueConverter<DriverId, int>(id => id.Value, value => new DriverId(value));

/// <summary>Stores a <see cref="ClientId"/> as the <c>integer</c> it wraps. See <see cref="UserIdConverter"/>.</summary>
public sealed class ClientIdConverter() : ValueConverter<ClientId, int>(id => id.Value, value => new ClientId(value));
