using DriveTrack.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="ProofOfDelivery"/> (FR-119, AD-11).</summary>
internal sealed class ProofOfDeliveryConfiguration : IEntityTypeConfiguration<ProofOfDelivery>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProofOfDelivery> builder)
    {
        builder.ToTable("ProofOfDeliveries");

        builder.HasKey(proof => proof.Id);

        builder.Property(proof => proof.RecipientName).HasMaxLength(200).IsRequired();

        // AD-11: the capture point is embedded in this row, like the delivery's own two.
        builder.OwnsOne(proof => proof.CaptureLocation, location =>
        {
            location.Property(value => value.Latitude).HasColumnName("CaptureLatitude");
            location.Property(value => value.Longitude).HasColumnName("CaptureLongitude");
            location.Property(value => value.Address)
                .HasColumnName("CaptureAddress")
                .HasMaxLength(500);
            location.Property(value => value.AddressResolvedAt)
                .HasColumnName("CaptureAddressResolvedAt");
        });

        builder.Navigation(proof => proof.CaptureLocation).IsRequired();

        // One proof per delivery, and it dies with the delivery (FR-119, DR-9). Declaring the
        // relationship one-to-one is what produces the unique index on delivery_id.
        builder.HasOne<Delivery>()
            .WithOne(delivery => delivery.ProofOfDelivery)
            .HasForeignKey<ProofOfDelivery>(proof => proof.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        // As on messages and the timeline: the foreign key is in the migration, the index that
        // makes its set-null cheap is here.
        builder.HasIndex(proof => proof.CapturedByUserId);
    }
}
