using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DriveTrack.Infrastructure.Identity;

/// <summary>
/// FR-9's documented way to get a first administrator into a fresh database.
/// <para>
/// Runs at startup, right after the migrator, and is idempotent in both halves: the four role rows
/// are ensured one at a time, and the admin is created only when no account holds
/// <c>Admin__Email</c>. Restarting the container against the same volume therefore writes nothing —
/// which is the property that makes running it unconditionally safe.
/// </para>
/// <para>
/// Blank <em>or refused</em> configuration is not a failure. The roles are still ensured, the admin
/// is skipped with a warning and startup continues: a developer who has not filled in
/// <c>Admin__Password</c>, or who chose one Identity's policy rejects, should get a running
/// application and a log line, not a container that will not boot.
/// </para>
/// </summary>
public sealed class IdentitySeeder(
    IUnitOfWorkFactory unitOfWorkFactory,
    IConfiguration configuration,
    ILogger<IdentitySeeder> logger)
{
    /// <summary>Configuration key holding the first administrator's email.</summary>
    public const string EmailKey = "Admin:Email";

    /// <summary>Configuration key holding the first administrator's password.</summary>
    public const string PasswordKey = "Admin:Password";

    /// <summary>Configuration key holding the first administrator's given name.</summary>
    public const string FirstNameKey = "Admin:FirstName";

    /// <summary>Configuration key holding the first administrator's family name.</summary>
    public const string LastNameKey = "Admin:LastName";

    /// <summary>Ensures the role vocabulary exists and, when configured, the first administrator.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await EnsureRolesAsync(cancellationToken);
        await EnsureAdminAsync(cancellationToken);
    }

    private async Task EnsureRolesAsync(CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        foreach (var role in Enum.GetValues<UserRole>())
        {
            await unitOfWork.Users.EnsureRoleAsync(role, cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task EnsureAdminAsync(CancellationToken cancellationToken)
    {
        var email = configuration[EmailKey];
        var password = configuration[PasswordKey];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "No first administrator was seeded: set the Admin__Email and Admin__Password "
                    + "environment variables (see .env.example) to create one.");

            return;
        }

        // A second unit of work, because the roles must be committed before AddToRoleAsync can find
        // the Admin row: Identity looks a role up with a query, and a query does not see rows that
        // are still only staged in the change tracker.
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        if (await unitOfWork.Users.EmailExistsAsync(email, cancellationToken))
        {
            logger.LogInformation("The first administrator already exists; nothing was seeded.");

            return;
        }

        try
        {
            await unitOfWork.Users.CreateAsync(
                new NewUserAccount(
                    NameOrDefault(FirstNameKey, "Адміністратор"),
                    NameOrDefault(LastNameKey, "Системи"),
                    email),
                password,
                UserRole.Admin,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }
        catch (DriveTrackException failure)
        {
            // A refused password or a malformed address is the same class of problem as a blank
            // one: someone has not finished filling in .env. Absent configuration already leaves the
            // container running with a warning, so bad configuration must not be the case that
            // refuses to boot - it would be found only on a first start against an empty volume.
            logger.LogWarning(
                failure,
                "No first administrator was seeded: the configured Admin__Email or Admin__Password "
                    + "was refused ({ErrorCode}). Correct them and restart to create one.",
                failure.Code);

            return;
        }

        logger.LogInformation("Seeded the first administrator.");
    }

    /// <summary>
    /// The configured name, or the default when nothing usable was configured. A blank value has to
    /// fall back as well as an absent one: compose forwards an unset variable as the empty string,
    /// and <c>??</c> would take that empty string as the answer and seed a nameless administrator.
    /// </summary>
    private string NameOrDefault(string key, string fallback)
    {
        var configured = configuration[key];

        return string.IsNullOrWhiteSpace(configured) ? fallback : configured;
    }
}
