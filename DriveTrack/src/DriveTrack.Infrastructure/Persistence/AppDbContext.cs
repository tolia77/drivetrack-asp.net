using DriveTrack.Domain.Chat;
using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Domain.Shifts;
using DriveTrack.Domain.Vehicles;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Persistence.Converters;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for DriveTrack, and under AD-20 the only authority for the
/// database schema — the model here is what migrations are generated from, and a migration is
/// the only thing that touches the database.
/// <para>
/// It derives from <see cref="IdentityDbContext{TUser, TRole, TKey}"/> so the Identity tables
/// and the domain tables share one model, one migration history and one transaction. AD-5
/// depends on that: creating an <c>ApplicationUser</c> and its <c>Driver</c> row must be one
/// atomic write, which is impossible across two contexts.
/// </para>
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<int>, int>(options)
{
    /// <summary>Client subtype rows (DR-3).</summary>
    public DbSet<Client> Clients => Set<Client>();

    /// <summary>Driver subtype rows (DR-3).</summary>
    public DbSet<Driver> Drivers => Set<Driver>();

    /// <summary>The fleet.</summary>
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    /// <summary>Deliveries, with their two embedded locations (AD-11).</summary>
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    /// <summary>The append-only delivery timeline (AD-27).</summary>
    public DbSet<TimelineEntry> TimelineEntries => Set<TimelineEntry>();

    /// <summary>Proof captured at hand-over (FR-119).</summary>
    public DbSet<ProofOfDelivery> ProofOfDeliveries => Set<ProofOfDelivery>();

    /// <summary>Outbound notification records (FR-28).</summary>
    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();

    /// <summary>Driver shifts (FR-110).</summary>
    public DbSet<Shift> Shifts => Set<Shift>();

    /// <summary>Delivery reviews (FR-62).</summary>
    public DbSet<Review> Reviews => Set<Review>();

    /// <summary>Chat messages, keyed by driver thread (AD-15).</summary>
    public DbSet<Message> Messages => Set<Message>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Identity's own mapping first, so a configuration below can extend it - AD-4's unique
        // index on AspNetUserRoles.UserId is exactly that.
        base.OnModelCreating(modelBuilder);

        // One IEntityTypeConfiguration per entity: every rule about an entity is in one file
        // with that entity's name on it, rather than in a thousand-line OnModelCreating.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // AD-22: registered for the type, not per property, so every UserId column in the schema
        // is an integer produced by the same converter - including the nullable ones, which
        // pre-convention configuration covers automatically.
        configurationBuilder.Properties<UserId>().HaveConversion<UserIdConverter>();
        configurationBuilder.Properties<DriverId>().HaveConversion<DriverIdConverter>();
        configurationBuilder.Properties<ClientId>().HaveConversion<ClientIdConverter>();

        // Registered as a convention rather than invoked at the end of OnModelCreating, so
        // configuration added by a later story cannot end up outside the naming policy.
        configurationBuilder.Conventions.Add(_ => new SnakeCaseNamingConvention());
    }
}
