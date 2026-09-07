using DriveTrack.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="ProofAsset"/> (AD-26, DR-14).</summary>
internal sealed class ProofAssetConfiguration : IEntityTypeConfiguration<ProofAsset>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProofAsset> builder)
    {
        // Named explicitly: this entity has no DbSet of its own - it is reached through its
        // proof - and without a DbSet EF would take the singular type name for the table.
        builder.ToTable("ProofAssets");

        builder.HasKey(asset => asset.Id);

        builder.Property(asset => asset.Kind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(asset => asset.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(asset => asset.ContentType).HasMaxLength(100).IsRequired();

        builder.HasOne<ProofOfDelivery>()
            .WithMany(proof => proof.Assets)
            .HasForeignKey(asset => asset.ProofOfDeliveryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
