using DriveTrack.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="NotificationAttempt"/> (FR-28, AD-12).</summary>
internal sealed class NotificationAttemptConfiguration : IEntityTypeConfiguration<NotificationAttempt>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationAttempt> builder)
    {
        builder.ToTable("NotificationAttempts");

        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.Kind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(attempt => attempt.Outcome)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(attempt => attempt.Recipient).HasMaxLength(256).IsRequired();
        builder.Property(attempt => attempt.Error).HasMaxLength(1000);

        // No navigation on Delivery: an attempt is written after the delivery's transaction has
        // already committed (AD-12), so it never belongs to the delivery's tracked graph.
        builder.HasOne<Delivery>()
            .WithMany()
            .HasForeignKey(attempt => attempt.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(attempt => attempt.DeliveryId);
    }
}
