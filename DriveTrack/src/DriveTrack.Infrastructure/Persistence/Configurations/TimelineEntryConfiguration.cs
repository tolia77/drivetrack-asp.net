using DriveTrack.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapping for <see cref="TimelineEntry"/> (AD-27).
/// <para>
/// Every property is listed explicitly. EF Core discovers a property by convention only when it
/// can write to it, and this entity's properties are all get-only by design, so convention
/// finds none of them — naming each one here is what maps them and what lets EF bind the
/// constructor.
/// </para>
/// <para>
/// The append-only guarantee itself is not here. EF model configuration cannot enforce it —
/// that is a code claim wearing schema clothing, which AD-20 forbids — so the guard is a
/// trigger in the DomainModel migration.
/// </para>
/// </summary>
internal sealed class TimelineEntryConfiguration : IEntityTypeConfiguration<TimelineEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TimelineEntry> builder)
    {
        builder.ToTable("TimelineEntries");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedOnAdd();

        builder.Property(entry => entry.DeliveryId);
        builder.Property(entry => entry.ActorUserId);
        builder.Property(entry => entry.OccurredAt);

        // The actor snapshot (AD-20): captured at write time, never updated, so it cannot drift.
        builder.Property(entry => entry.ActorDisplayName).HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.ActorRole).HasMaxLength(32).IsRequired();

        builder.Property(entry => entry.PreviousStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(entry => entry.NewStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(entry => entry.Note).HasMaxLength(1000);

        // DR-9: the timeline dies with its delivery, and only that way.
        builder.HasOne<Delivery>()
            .WithMany()
            .HasForeignKey(entry => entry.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        // FR-108 reads a delivery's entries in order; this is the index that serves it.
        builder.HasIndex(entry => new { entry.DeliveryId, entry.OccurredAt });

        // The set-null foreign key onto asp_net_users is declared in the migration (EF cannot
        // point a typed UserId at IdentityUser<int>'s plain int key). This index is what keeps
        // that set-null from scanning the whole table when a user is deleted.
        builder.HasIndex(entry => entry.ActorUserId);
    }
}
