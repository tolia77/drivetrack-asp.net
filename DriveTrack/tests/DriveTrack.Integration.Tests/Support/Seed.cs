using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
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
    /// <remarks>
    /// A non-<c>Pending</c> <paramref name="status"/> is reached by walking the lifecycle rather
    /// than by assigning the property, which has no public setter: <c>TryChangeStatus</c> is the
    /// only mutator (AD-10), so a seeded row is one the production write path could actually have
    /// produced. A helper that could fabricate an unreachable state would seed tests with rows the
    /// system cannot hold.
    /// </remarks>
    public static Delivery NewDelivery(
        ClientId? clientId = null,
        DriverId? driverId = null,
        decimal packageWeightKg = 12.5m,
        DateTimeOffset? windowEarliestAt = null,
        DateTimeOffset? windowLatestAt = null,
        DeliveryStatus status = DeliveryStatus.Pending)
    {
        var delivery = new Delivery
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
            CreatedAt = Instant,
        };

        Advance(delivery, status);

        return delivery;
    }

    /// <summary>
    /// Walks a delivery to <paramref name="target"/> through the entity's own mutator. Every status
    /// is two moves or fewer from <c>Pending</c>, which is why this needs no search.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The walk did not arrive, which means the lifecycle changed shape and this helper has to be
    /// re-read rather than quietly seeding the wrong status.
    /// </exception>
    public static void Advance(Delivery delivery, DeliveryStatus target)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (delivery.Status == target)
        {
            return;
        }

        var from = delivery.Status;

        // Decided before anything moves. TryChangeStatus reports failure rather than throwing, so a
        // walk that acted first and checked afterwards would leave a half-advanced entity behind -
        // a Failed row asked for Pending would arrive at InTransit and only then complain, and the
        // caller's `catch` would be holding a delivery in a status nobody asked for.
        if (!Reaches(from, target))
        {
            throw Unreachable(from, target);
        }

        if (!Delivery.NextStatuses(from).Contains(target)
            && !delivery.TryChangeStatus(DeliveryStatus.InTransit))
        {
            throw Unreachable(from, target);
        }

        if (!delivery.TryChangeStatus(target))
        {
            throw Unreachable(from, target);
        }
    }

    /// <summary>
    /// Whether the lifecycle gets from one status to another in at most the two moves this helper
    /// makes, asked of <see cref="Delivery.NextStatuses"/> so the answer cannot drift from the rule.
    /// </summary>
    private static bool Reaches(DeliveryStatus from, DeliveryStatus target) =>
        Delivery.NextStatuses(from).Contains(target)
        || (Delivery.NextStatuses(from).Contains(DeliveryStatus.InTransit)
            && Delivery.NextStatuses(DeliveryStatus.InTransit).Contains(target));

    private static InvalidOperationException Unreachable(DeliveryStatus from, DeliveryStatus target) =>
        new("The lifecycle does not reach " + target + " from " + from
            + " in two moves; Seed.Advance has to be re-read.");

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

    /// <summary>A valid, unsaved review of a delivery.</summary>
    /// <remarks>
    /// For the suites that need more reviews than a page holds. Writing one over HTTP means
    /// carrying a parcel end to end first — a vehicle, a driver, a client, three status changes —
    /// which is the right arrangement for a test about authoring and the wrong one for a test about
    /// where a <c>WHERE</c> clause goes.
    /// </remarks>
    public static Review NewReview(
        int deliveryId,
        ClientId clientId,
        int rating = 5,
        string text = "усе добре") =>
        new()
        {
            DeliveryId = deliveryId,
            ClientId = clientId,
            Rating = rating,
            Text = text,
            CreatedAt = Instant,
        };

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

            // The enum rather than a free string (AD-21). The column still holds the member name,
            // so what lands in the database is unchanged.
            UserRole.Dispatcher,
            previousStatus,
            newStatus,
            note,
            occurredAt ?? Instant);
}
