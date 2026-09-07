using DriveTrack.Domain.Clients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="Client"/> (DR-3).</summary>
internal sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("Clients");

        builder.HasKey(client => client.Id);
        builder.Property(client => client.Id).ValueGeneratedOnAdd();

        builder.Property(client => client.PhoneNumber).HasMaxLength(32).IsRequired();

        // One client row per user. The foreign key onto asp_net_users is declared in the
        // DomainModel migration rather than here - see IdentityModelConfiguration for why EF
        // cannot express it - but the uniqueness half belongs in the model, where the rest of
        // the entity's rules live.
        builder.HasIndex(client => client.UserId).IsUnique();
    }
}
