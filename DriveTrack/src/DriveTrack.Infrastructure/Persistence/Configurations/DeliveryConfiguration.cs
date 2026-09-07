using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Drivers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="Delivery"/>, including its two embedded locations (AD-11).</summary>
internal sealed class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Delivery> builder)
    {
        builder.ToTable("Deliveries", table =>
        {
            // FR-102. A weight of zero or below is not a validation nicety - it is a row that
            // must not exist, so it is refused by the database rather than by a service.
            table.HasCheckConstraint("CK_Deliveries_PackageWeightKg", "package_weight_kg > 0");

            // FR-100: either bound may stand alone, and both may be absent; only an inverted
            // pair is rejected.
            table.HasCheckConstraint(
                "CK_Deliveries_DeliveryWindow",
                "window_earliest_at IS NULL OR window_latest_at IS NULL "
                + "OR window_earliest_at < window_latest_at");
        });

        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.PackageDetails).HasMaxLength(1000).IsRequired();
        builder.Property(delivery => delivery.DeliveryNotes).HasMaxLength(1000);
        builder.Property(delivery => delivery.PackageWeightKg).HasPrecision(10, 3);

        // AD-21: the stored value is the member name, so reordering the enum cannot remap rows.
        builder.Property(delivery => delivery.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        ConfigureLocation(builder, delivery => delivery.PickupLocation, "Pickup");
        ConfigureLocation(builder, delivery => delivery.DropoffLocation, "Dropoff");

        // FR-47: a deleted client leaves its deliveries standing, unowned.
        builder.HasOne<Client>()
            .WithMany(client => client.Deliveries)
            .HasForeignKey(delivery => delivery.ClientId)
            .OnDelete(DeleteBehavior.SetNull);

        // FR-16, FR-39: an unassigned delivery is legal, so a deleted driver nulls the
        // assignment instead of taking the delivery with it - or, as in the original, raising
        // a database error because the key was never declared at all.
        builder.HasOne<Driver>()
            .WithMany(driver => driver.Deliveries)
            .HasForeignKey(delivery => delivery.DriverId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(delivery => delivery.ClientId);
        builder.HasIndex(delivery => delivery.DriverId);
        builder.HasIndex(delivery => delivery.Status);
    }

    /// <summary>
    /// Embeds a <c>Location</c> in the delivery's own row with an explicit column prefix
    /// (AD-11): there is no <c>locations</c> table, so editing a location is an update of this
    /// row and cannot orphan anything.
    /// </summary>
    private static void ConfigureLocation(
        EntityTypeBuilder<Delivery> builder,
        System.Linq.Expressions.Expression<Func<Delivery, Domain.Common.Location?>> navigation,
        string prefix)
    {
        builder.OwnsOne(navigation, location =>
        {
            location.Property(value => value.Latitude).HasColumnName($"{prefix}Latitude");
            location.Property(value => value.Longitude).HasColumnName($"{prefix}Longitude");
            location.Property(value => value.Address)
                .HasColumnName($"{prefix}Address")
                .HasMaxLength(500);
            location.Property(value => value.AddressResolvedAt)
                .HasColumnName($"{prefix}AddressResolvedAt");
        });

        builder.Navigation(navigation).IsRequired();
    }
}
