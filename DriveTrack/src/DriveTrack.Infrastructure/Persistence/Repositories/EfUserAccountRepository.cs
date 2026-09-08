using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>
/// The Identity port over the unit of work's own context and its scoped managers (AD-5).
/// <para>
/// Nothing Identity-shaped leaves this file: <c>ApplicationUser</c>, <c>IdentityResult</c> and the
/// managers all stop here, and Application sees <see cref="UserAccount"/> and the five typed
/// failures (AD-1, AD-8, AD-17).
/// </para>
/// </summary>
internal sealed class EfUserAccountRepository(AppDbContext context, ScopedIdentity identity)
    : IUserAccountRepository
{
    /// <inheritdoc />
    public async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = identity.Users.NormalizeEmail(email);

        var user = await context.Users
            .FirstOrDefaultAsync(candidate => candidate.NormalizedEmail == normalized, cancellationToken);

        return user is null ? null : await MapAsync(user, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UserAccount?> GetByIdAsync(UserId id, CancellationToken cancellationToken)
    {
        var user = await context.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == id.Value, cancellationToken);

        return user is null ? null : await MapAsync(user, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        // Compared as Identity stores it, not as the caller typed it: two addresses differing only
        // in case are one account, and asking the question any other way would let the second one
        // through as far as the unique index.
        var normalized = identity.Users.NormalizeEmail(email);

        return context.Users.AnyAsync(
            candidate => candidate.NormalizedEmail == normalized,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UserAccount> CreateAsync(
        NewUserAccount account,
        string password,
        UserRole role,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var user = new ApplicationUser
        {
            FirstName = account.FirstName,
            LastName = account.LastName,
            UserName = account.Email,
            Email = account.Email,
        };

        // FR-8: the password reaches storage only through Identity's IPasswordHasher. This is the
        // one call in the system that turns a plaintext password into a stored value.
        Throw(await identity.Users.CreateAsync(user, password));

        // One flush, here, for two reasons that both come down to the key.
        //
        // The subtype row's foreign key needs the real number: the Client and Driver relationships
        // are raw SQL in the DomainModel migration (see IdentityModelConfiguration), because their
        // typed UserId cannot be declared against IdentityUser's plain int key - so EF has nothing
        // to propagate a temporary key through, and would write the placeholder.
        //
        // And AddToRoleAsync below re-attaches the user, which EF refuses while its key is still
        // temporary. Identity itself never decides when to save - AutoSaveChanges is off - so the
        // number and placement of saves is decided here, and this is the only one.
        //
        // It is a flush inside the unit of work's transaction, not a commit: the scope still owns
        // the transaction, and disposing it without committing still leaves nothing behind (NFR-9).
        await FlushAsync(cancellationToken);

        // AD-4: exactly one role row, and the unique index on asp_net_user_roles.user_id is what
        // makes a second one impossible rather than merely unusual. Staged, not saved - it reaches
        // the database with the subtype row at IUnitOfWork.CommitAsync.
        Throw(await identity.Users.AddToRoleAsync(user, role.ToString()));

        return new UserAccount(
            new UserId(user.Id),
            user.FirstName,
            user.LastName,
            user.Email!,
            role,
            DriverId: null,
            ClientId: null);
    }

    /// <inheritdoc />
    public async Task<bool> VerifyPasswordAsync(
        UserId userId,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == userId.Value, cancellationToken);

        if (user is null)
        {
            return false;
        }

        return await identity.Users.CheckPasswordAsync(user, password);
    }

    /// <inheritdoc />
    public async Task EnsureRoleAsync(UserRole role, CancellationToken cancellationToken)
    {
        var name = role.ToString();

        if (await identity.Roles.RoleExistsAsync(name))
        {
            return;
        }

        Throw(await identity.Roles.CreateAsync(new IdentityRole<int>(name)));
    }

    /// <summary>
    /// Saves the staged changes inside the unit of work's transaction, translating a constraint
    /// refusal exactly as <c>UnitOfWork.CommitAsync</c> does, so the normalized-email unique index
    /// surfaces as a 409 whether it is hit here or at the commit.
    /// </summary>
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            var translated = PostgresConstraintTranslator.Translate(exception);

            if (ReferenceEquals(translated, exception))
            {
                throw;
            }

            throw translated;
        }
    }

    /// <summary>Reads the single role row and the subtype row id for a loaded user.</summary>
    private async Task<UserAccount> MapAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var roleName = await (
            from userRole in context.UserRoles
            join declared in context.Roles on userRole.RoleId equals declared.Id
            where userRole.UserId == user.Id
            select declared.Name).FirstOrDefaultAsync(cancellationToken);

        if (roleName is null || !Enum.TryParse<UserRole>(roleName, ignoreCase: false, out var role))
        {
            // AD-4 makes one role, drawn from the four, a schema-level invariant. A row that breaks
            // it is a defect, not a caller error, so it leaves as the 500 envelope rather than being
            // smoothed over into a plausible-looking profile.
            throw new InvalidOperationException(
                "User " + user.Id.ToString(CultureInfo.InvariantCulture)
                    + " holds no recognised role. AD-4 requires exactly one.");
        }

        var userId = new UserId(user.Id);
        DriverId? driverId = null;
        ClientId? clientId = null;

        if (role == UserRole.Driver)
        {
            var driver = await context.Drivers
                .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

            driverId = driver?.Id;
        }
        else if (role == UserRole.Client)
        {
            var client = await context.Clients
                .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

            clientId = client?.Id;
        }

        return new UserAccount(
            userId,
            user.FirstName,
            user.LastName,
            user.Email!,
            role,
            driverId,
            clientId);
    }

    /// <summary>
    /// Turns an <c>IdentityResult</c> failure into one of AD-8's typed failures. Identity's own
    /// error codes are the input, the contract's codes are the output, and the mapping lives here
    /// so no caller ever inspects an <c>IdentityError</c>.
    /// </summary>
    private static void Throw(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        var codes = result.Errors.Select(error => error.Code).ToArray();
        var detail = string.Join(", ", result.Errors.Select(error => error.Code + ": " + error.Description));

        if (Array.Exists(
                codes,
                code => code.Equals("DuplicateEmail", StringComparison.Ordinal)
                    || code.Equals("DuplicateUserName", StringComparison.Ordinal)))
        {
            throw new ConflictException(ErrorCode.AUTH_EMAIL_ALREADY_IN_USE, detail);
        }

        if (Array.Exists(codes, code => code.StartsWith("Password", StringComparison.Ordinal)))
        {
            throw new ValidationException(
                ErrorCode.COMMON_VALIDATION_FAILED,
                detail,
                [new FieldError("Password", nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))]);
        }

        if (Array.Exists(
                codes,
                code => code.Equals("InvalidEmail", StringComparison.Ordinal)
                    || code.Equals("InvalidUserName", StringComparison.Ordinal)))
        {
            throw new ValidationException(
                ErrorCode.COMMON_VALIDATION_FAILED,
                detail,
                [new FieldError("Email", nameof(ErrorCode.AUTH_EMAIL_INVALID))]);
        }

        throw new ValidationException(ErrorCode.COMMON_VALIDATION_FAILED, detail, []);
    }
}
