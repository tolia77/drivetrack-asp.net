using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Shifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="Shift"/> (FR-110, FR-117, DR-13).</summary>
internal sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        builder.ToTable("Shifts");

        builder.HasKey(shift => shift.Id);

        // FR-110: one open shift per driver, as a partial unique index rather than a lookup
        // two concurrent requests can both pass. The filter is raw SQL and so names the column
        // rather than the property; it is also the exact, immutable definition of open
        // (FR-117), which is why nothing time-dependent may appear in it.
        builder.HasIndex(shift => shift.DriverId)
            .IsUnique()
            .HasFilter("ended_at IS NULL")
            .HasDatabaseName("IX_Shifts_DriverId_Open");

        // FR-39: a shift is meaningless without its driver, and references no delivery (DR-13),
        // so nothing is orphaned by taking it along.
        builder.HasOne<Driver>()
            .WithMany(driver => driver.Shifts)
            .HasForeignKey(shift => shift.DriverId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
