using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Vehicles;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Persistence;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// Minimal, valid rows for the tests that need something to attach a constraint violation to.
/// Everything here is the legal shape; each test breaks exactly one rule of its own.
/// </summary>
internal static class Seed
{
    /// <summary>A fixed instant at offset zero — the only offset AD-13 permits in storage.</summary>
    public static readonly DateTimeOffset Instant = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A location with no resolved address, which is the state every location starts in (DR-11).</summary>
    public static Location Location(double latitude = 50.4501, double longitude = 30.5234) =>
        new(latitude, longitude, null, null);

    /// <summary>Creates and saves an application user with a unique name and email.</summary>
    public static async Task<ApplicationUser> UserAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var user = new ApplicationUser
        {
            FirstName = "Test",
            LastName = "Person",
            UserName = $"user-{suffix}",
            NormalizedUserName = $"USER-{suffix}".ToUpperInvariant(),
            Email = $"{suffix}@drivetrack.test",
            NormalizedEmail = $"{suffix}@DRIVETRACK.TEST".ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        return user;
    }

    /// <summary>Creates and saves a client, with the user it needs.</summary>
    public static async Task<Client> ClientAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var user = await UserAsync(context, cancellationToken);

        var client = new Client
        {
            UserId = new UserId(user.Id),
            PhoneNumber = "+380441234567",
        };

        context.Clients.Add(client);
        await context.SaveChangesAsync(cancellationToken);

        return client;
    }

    /// <summary>Creates and saves a driver, with the user it needs.</summary>
    public static async Task<Driver> DriverAsync(
        AppDbContext context,
        CancellationToken cancellationToken,
        int? vehicleId = null)
    {
        var user = await UserAsync(context, cancellationToken);

        var driver = new Driver
        {
            UserId = new UserId(user.Id),
            LicenseNumber = Guid.NewGuid().ToString("N")[..10],
            VehicleId = vehicleId,
        };

        context.Drivers.Add(driver);
        await context.SaveChangesAsync(cancellationToken);

        return driver;
    }

    /// <summary>Creates and saves a vehicle with a unique plate unless one is given.</summary>
    public static async Task<Vehicle> VehicleAsync(
        AppDbContext context,
        CancellationToken cancellationToken,
        string? licensePlate = null)
    {
        var vehicle = new Vehicle
        {
            Model = "Renault Master",
            LicensePlate = licensePlate ?? Guid.NewGuid().ToString("N")[..10],
            CapacityKg = 1200m,
            Mileage = 42_000,
            NextMaintenanceDate = new DateOnly(2026, 12, 1),
        };

        context.Vehicles.Add(vehicle);
        await context.SaveChangesAsync(cancellationToken);

        return vehicle;
    }

    /// <summary>A valid, unsaved delivery.</summary>
    public static Delivery NewDelivery(
        ClientId? clientId = null,
        DriverId? driverId = null,
        decimal packageWeightKg = 12.5m,
        DateTimeOffset? windowEarliestAt = null,
        DateTimeOffset? windowLatestAt = null,
        DeliveryStatus status = DeliveryStatus.Pending) =>
        new()
        {
            ClientId = clientId,
            DriverId = driverId,
            PickupLocation = Location(),
            DropoffLocation = Location(49.8397, 24.0297),
            PackageDetails = "One pallet of coursework",
            PackageWeightKg = packageWeightKg,
            DeliveryNotes = null,
            WindowEarliestAt = windowEarliestAt,
            WindowLatestAt = windowLatestAt,
            Status = status,
            CreatedAt = Instant,
        };

    /// <summary>Creates and saves a valid delivery.</summary>
    public static async Task<Delivery> DeliveryAsync(
        AppDbContext context,
        CancellationToken cancellationToken,
        ClientId? clientId = null,
        DriverId? driverId = null)
    {
        var delivery = NewDelivery(clientId, driverId);

        context.Deliveries.Add(delivery);
        await context.SaveChangesAsync(cancellationToken);

        return delivery;
    }

    /// <summary>A timeline entry recording a status change, ready to append.</summary>
    public static TimelineEntry NewTimelineEntry(
        int deliveryId,
        UserId? actorUserId,
        DeliveryStatus? previousStatus = DeliveryStatus.Pending,
        DeliveryStatus? newStatus = DeliveryStatus.InTransit,
        string? note = null,
        DateTimeOffset? occurredAt = null) =>
        new(
            deliveryId,
            actorUserId,
            "Test Person",
            "dispatcher",
            previousStatus,
            newStatus,
            note,
            occurredAt ?? Instant);
}
