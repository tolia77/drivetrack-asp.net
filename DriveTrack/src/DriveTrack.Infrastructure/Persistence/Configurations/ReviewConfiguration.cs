using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="Review"/> (DR-6, DR-7, FR-62, FR-67).</summary>
internal sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Review> builder)
    {
        // DR-7: the rating bound is a constraint, so a race cannot slip a 0 or a 6 past it.
        builder.ToTable(
            "Reviews",
            table => table.HasCheckConstraint("CK_Reviews_Rating", "rating >= 1 AND rating <= 5"));

        builder.HasKey(review => review.Id);

        builder.Property(review => review.Text).HasMaxLength(2000).IsRequired();

        // DR-6, FR-62: one review per delivery. The original enforced this with an application
        // check, which is the worked example of an invariant concurrency defeats; declaring the
        // relationship one-to-one is what produces the unique index that actually holds.
        builder.HasOne<Delivery>()
            .WithOne(delivery => delivery.Review)
            .HasForeignKey<Review>(review => review.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        // FR-47: a deleted client takes its reviews with it.
        builder.HasOne<Client>()
            .WithMany(client => client.Reviews)
            .HasForeignKey(review => review.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(review => review.ClientId);
    }
}
