using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveTrack.Infrastructure.Identity;

/// <summary>
/// The <c>UserManager</c> and <c>RoleManager</c> for one persistence scope, built over that scope's
/// own context.
/// <para>
/// <b>Why this exists at all.</b> AD-5 requires a user, its role row and its subtype row to land in
/// one transaction. <c>UserManager.CreateAsync</c> normally calls <c>SaveChangesAsync</c> on its
/// store, which would commit the account independently of the subtype row and reproduce exactly the
/// orphaned-account failure AD-5 names. Constructing the stores here with
/// <c>AutoSaveChanges = false</c> is the single flag that stops Identity being a second persistence
/// authority: from then on nothing inside Identity decides when a write reaches the database.
/// </para>
/// <para>
/// The context arrives as a <see cref="DbContext"/> rather than the concrete application context on
/// purpose — the stores need nothing more, and the persistence contract keeps the concrete context
/// named only inside <c>Persistence/</c>.
/// </para>
/// <para>
/// Every collaborator Identity registers is scoped, so this opens a container scope of its own per
/// unit of work and hands it to <see cref="ScopedIdentity"/> to dispose. Resolving them from the
/// root provider would work in Production and throw in Development, which is the worst of both.
/// </para>
/// </summary>
public sealed class ScopedIdentityFactory(IServiceScopeFactory scopeFactory)
{
    /// <summary>Builds the managers for <paramref name="context"/>.</summary>
    public ScopedIdentity Create(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scope = scopeFactory.CreateScope();

        try
        {
            var services = scope.ServiceProvider;
            var describer = services.GetRequiredService<IdentityErrorDescriber>();
            var normalizer = services.GetRequiredService<ILookupNormalizer>();

            var userStore = new UserStore<ApplicationUser, IdentityRole<int>, DbContext, int>(context, describer)
            {
                // AD-5. The whole point of this class.
                AutoSaveChanges = false,
            };

            var roleStore = new RoleStore<IdentityRole<int>, DbContext, int>(context, describer)
            {
                AutoSaveChanges = false,
            };

            var users = new UserManager<ApplicationUser>(
                userStore,
                services.GetRequiredService<IOptions<IdentityOptions>>(),
                services.GetRequiredService<IPasswordHasher<ApplicationUser>>(),
                services.GetServices<IUserValidator<ApplicationUser>>(),
                services.GetServices<IPasswordValidator<ApplicationUser>>(),
                normalizer,
                describer,
                services,
                services.GetRequiredService<ILogger<UserManager<ApplicationUser>>>());

            var roles = new RoleManager<IdentityRole<int>>(
                roleStore,
                services.GetServices<IRoleValidator<IdentityRole<int>>>(),
                normalizer,
                describer,
                services.GetRequiredService<ILogger<RoleManager<IdentityRole<int>>>>());

            return new ScopedIdentity(scope, users, roles);
        }
        catch
        {
            // The caller never receives the result, so it can never dispose it.
            scope.Dispose();
            throw;
        }
    }
}

/// <summary>
/// One scope's Identity managers, disposed with the unit of work that owns them. Disposing a
/// manager disposes its store; the store never owns the context, so the pooled context is returned
/// by the unit of work exactly as before.
/// </summary>
public sealed class ScopedIdentity : IDisposable
{
    private readonly IServiceScope _scope;

    internal ScopedIdentity(
        IServiceScope scope,
        UserManager<ApplicationUser> users,
        RoleManager<IdentityRole<int>> roles)
    {
        _scope = scope;
        Users = users;
        Roles = roles;
    }

    /// <summary>The user manager bound to this scope's context.</summary>
    public UserManager<ApplicationUser> Users { get; }

    /// <summary>The role manager bound to this scope's context.</summary>
    public RoleManager<IdentityRole<int>> Roles { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nested, not sequential: a manager that threw on disposal would otherwise strand the
        // container scope, and a leaked scope is a leaked pooled context. The same shape the unit
        // of work uses, for the same reason.
        try
        {
            Users.Dispose();
        }
        finally
        {
            try
            {
                Roles.Dispose();
            }
            finally
            {
                _scope.Dispose();
            }
        }
    }
}
