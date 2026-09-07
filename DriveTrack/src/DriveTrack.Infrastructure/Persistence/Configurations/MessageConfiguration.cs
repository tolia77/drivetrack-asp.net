using DriveTrack.Domain.Chat;
using DriveTrack.Domain.Drivers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>Mapping for <see cref="Message"/> (AD-15, AD-16, FR-39).</summary>
internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Text).HasMaxLength(2000).IsRequired();

        // AD-15: the thread key is the driver row. WithMany() names no navigation because
        // AD-16 keeps chat severable - Driver holds no path back into the chat module.
        builder.HasOne<Driver>()
            .WithMany()
            .HasForeignKey(message => message.DriverId)
            .OnDelete(DeleteBehavior.Cascade);

        // FR-39 reads a driver's thread oldest first; this is the index that serves it.
        builder.HasIndex(message => new { message.DriverId, message.SentAt });

        // The set-null foreign key onto asp_net_users is declared in the migration, for the
        // reason IdentityModelConfiguration gives. This index is what keeps that set-null from
        // scanning the whole table when a user is deleted.
        builder.HasIndex(message => message.SenderUserId);
    }
}
