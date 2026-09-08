using System.Globalization;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Identity;

/// <summary>
/// FR-9: the documented way a first administrator exists at all, and the property that makes
/// running it on every start safe.
/// <para>
/// Idempotence is the whole assertion. A seeder that writes a second admin, or a fifth role row, on
/// the second <c>docker compose up</c> is one nobody can leave in the startup path — and the
/// failure only appears on a restart, which is the run nobody does while developing.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class IdentitySeederTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_four_roles_and_the_admin_are_created_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await database.Seeder.SeedAsync(cancellationToken);

        Assert.Equal(Enum.GetValues<UserRole>().Length, await CountAsync(database, "asp_net_roles", "TRUE", cancellationToken));

        foreach (var role in Enum.GetValues<UserRole>())
        {
            Assert.Equal(
                1L,
                await CountAsync(
                    database,
                    "asp_net_roles",
                    $"normalized_name = '{role.ToString().ToUpperInvariant()}'",
                    cancellationToken));
        }

        Assert.Equal(1L, await AdminCountAsync(database, cancellationToken));
    }

    [Fact]
    public async Task A_second_run_writes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await database.Seeder.SeedAsync(cancellationToken);
        await database.Seeder.SeedAsync(cancellationToken);

        Assert.Equal(Enum.GetValues<UserRole>().Length, await CountAsync(database, "asp_net_roles", "TRUE", cancellationToken));
        Assert.Equal(1L, await AdminCountAsync(database, cancellationToken));
        Assert.Equal(1L, await CountAsync(database, "asp_net_users", "TRUE", cancellationToken));
    }

    [Theory]
    [InlineData("Admin:Email")]
    [InlineData("Admin:Password")]
    public async Task Blank_configuration_still_ensures_the_roles_and_skips_the_admin(string blankKey)
    {
        // Startup must survive a developer who has not filled in .env: a container that refuses to
        // boot over a missing optional value is a worse failure than a log line.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            migrate: true,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal) { [blankKey] = "  " });

        await database.Seeder.SeedAsync(cancellationToken);

        Assert.Equal(Enum.GetValues<UserRole>().Length, await CountAsync(database, "asp_net_roles", "TRUE", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "asp_net_users", "TRUE", cancellationToken));
    }

    [Fact]
    public async Task A_password_the_policy_refuses_still_leaves_the_roles_seeded_and_does_not_throw()
    {
        // The .env.example placeholder is a value a developer copies verbatim, so a password
        // Identity refuses has to behave like a blank one: a warning and a running container, not a
        // startup that aborts on the first boot against an empty volume.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            migrate: true,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Admin:Password"] = "short",
            });

        await database.Seeder.SeedAsync(cancellationToken);

        Assert.Equal(
            Enum.GetValues<UserRole>().Length,
            await CountAsync(database, "asp_net_roles", "TRUE", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "asp_net_users", "TRUE", cancellationToken));
    }

    [Fact]
    public async Task The_schema_refuses_a_second_role_row_for_one_user()
    {
        // AD-4 is a claim about the database, not about the code that usually writes it. Counting
        // rows after a happy-path write assumes the index; this asks the index directly.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await database.Seeder.SeedAsync(cancellationToken);

        await using var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);
        var admin = await unitOfWork.Users.FindByEmailAsync(TestConfiguration.AdminEmail, cancellationToken);

        Assert.NotNull(admin);

        var userId = admin.Id.Value.ToString(CultureInfo.InvariantCulture);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(
            "INSERT INTO asp_net_user_roles (user_id, role_id) "
                + "SELECT " + userId + ", id FROM asp_net_roles "
                + "WHERE normalized_name = 'DISPATCHER'",
            cancellationToken));

        // SQLSTATE 23505: the unique index on asp_net_user_roles.user_id, which is the whole of
        // AD-4's enforcement.
        Assert.Contains("23505", failure.ToString(), StringComparison.Ordinal);

        Assert.Equal(
            1L,
            await CountAsync(database, "asp_net_user_roles", $"user_id = {userId}", cancellationToken));
    }

    [Fact]
    public async Task The_seeded_admin_holds_exactly_one_role_row()
    {
        // AD-4, on the one account that is most tempting to give extra rows to: admin passes every
        // check by rule inside the guard, not by holding the other three roles.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await database.Seeder.SeedAsync(cancellationToken);

        await using var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);
        var admin = await unitOfWork.Users.FindByEmailAsync(TestConfiguration.AdminEmail, cancellationToken);

        Assert.NotNull(admin);
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.Null(admin.DriverId);
        Assert.Null(admin.ClientId);
        Assert.True(await unitOfWork.Users.VerifyPasswordAsync(
            admin.Id,
            TestConfiguration.AdminPassword,
            cancellationToken));

        Assert.Equal(
            1L,
            await CountAsync(
                database,
                "asp_net_user_roles",
                $"user_id = {admin.Id.Value.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken));
    }

    private static Task<long> AdminCountAsync(TestDatabase database, CancellationToken cancellationToken) =>
        CountAsync(
            database,
            "asp_net_users",
            $"normalized_email = '{TestConfiguration.AdminEmail.ToUpperInvariant()}'",
            cancellationToken);

    private static async Task<long> CountAsync(
        TestDatabase database,
        string table,
        string predicate,
        CancellationToken cancellationToken)
    {
        var value = await database.ScalarAsync(
            $"SELECT COUNT(*) FROM {table} WHERE {predicate}",
            cancellationToken);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
