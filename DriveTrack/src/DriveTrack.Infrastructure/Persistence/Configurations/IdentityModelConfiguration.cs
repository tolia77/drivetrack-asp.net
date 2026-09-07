using DriveTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DriveTrack.Infrastructure.Persistence.Configurations;

/// <summary>
/// The rules that sit on ASP.NET Core Identity's own tables.
/// <para>
/// AD-4: a user holds exactly one role, as a unique index on <c>asp_net_user_roles.user_id</c>.
/// Identity's shape is many-to-many, which would let a user hold two roles and fork every
/// ownership check into an undefined case; AD-20 forbids leaving an invariant that load-bearing
/// to application code.
/// </para>
/// <para>
/// The one-to-one from <c>ApplicationUser</c> to <c>Client</c> and <c>Driver</c> is <em>not</em>
/// declared here, and that is a deliberate concession rather than an oversight. Those subtype
/// rows key on a typed <c>UserId</c> (AD-22) while <see cref="IdentityUser{TKey}"/> fixes the
/// principal key as a plain <c>int</c>, and EF Core rejects a relationship whose foreign key and
/// principal key have different CLR types — "cannot target the primary key ... because it is not
/// compatible". Rather than surrender the typed id, whose entire purpose is that a user id and a
/// driver row id cannot be confused, the two foreign keys and the actor's set-null key are
/// declared as SQL in the DomainModel migration. AD-20 makes the migration the schema authority
/// anyway, so the constraint is in the place that decides. The uniqueness half of each stays in
/// the model, on the entity it belongs to.
/// </para>
/// </summary>
internal sealed class IdentityModelConfiguration :
    IEntityTypeConfiguration<ApplicationUser>,
    IEntityTypeConfiguration<IdentityUserRole<int>>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(user => user.LastName).HasMaxLength(100).IsRequired();
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdentityUserRole<int>> builder)
    {
        // AD-4. Identity's own key on this table is (user_id, role_id), which permits a second
        // row for the same user; this index is what makes "exactly one role" true.
        builder.HasIndex(userRole => userRole.UserId).IsUnique();
    }
}
