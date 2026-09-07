using DriveTrack.Domain.Drivers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="Driver"/> (DR-3, FR-44).</summary>
internal sealed class DriverConfiguration : IEntityTypeConfiguration<Driver>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Driver> builder)
    {
        builder.ToTable("Drivers");

        builder.HasKey(driver => driver.Id);
        builder.Property(driver => driver.Id).ValueGeneratedOnAdd();

        builder.Property(driver => driver.LicenseNumber).HasMaxLength(50).IsRequired();

        // One driver row per user; the foreign key itself is in the migration.
        builder.HasIndex(driver => driver.UserId).IsUnique();

        // FR-44: a vehicle is held by at most one driver. Filtered, so any number of drivers
        // may hold none. The filter is raw SQL that reaches PostgreSQL unrewritten, which is
        // why it names the column and not the property (the naming convention rewrites
        // identifiers in the model, never the inside of a string).
        builder.HasIndex(driver => driver.VehicleId)
            .IsUnique()
            .HasFilter("vehicle_id IS NOT NULL");

        // FR-43: deleting a vehicle leaves its driver standing, holding none. Blocking the
        // delete while it is assigned is an application rule and belongs to story 4.1.
        builder.HasOne(driver => driver.Vehicle)
            .WithMany()
            .HasForeignKey(driver => driver.VehicleId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
