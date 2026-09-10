using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Identity;

/// <summary>
/// AD-5's crux, against a real PostgreSQL: Identity is not a second persistence authority.
/// <para>
/// <c>UserManager.CreateAsync</c> normally saves through its own store, which would commit an
/// account independently of the <c>clients</c> row that makes it usable and reproduce exactly the
/// orphaned-account failure the decision names. With <c>AutoSaveChanges = false</c> and the store
/// built over the unit of work's own context, the account is inside the unit of work's transaction —
/// so disposing the scope without committing leaves nothing behind.
/// </para>
/// </summary>
public class IdentityTransactionTests(PostgresFixture postgres)
{
    private const string Password = "Passw0rd-Test";

    [Fact]
    public async Task A_scope_disposed_without_committing_leaves_no_user_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await database.Seeder.SeedAsync(cancellationToken);

        var email = UniqueEmail();

        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            await unitOfWork.Users.CreateAsync(
                new NewUserAccount("Олена", "Петренко", email),
                Password,
                UserRole.Client,
                cancellationToken);

            // No CommitAsync. Disposing rolls the transaction back (NFR-9), and the account created
            // through Identity is inside it like every other write.
        }

        Assert.Equal(0L, await CountUsersAsync(database, email, cancellationToken));
    }

    [Fact]
    public async Task A_failure_after_the_user_is_staged_leaves_no_user_role_or_client_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await database.Seeder.SeedAsync(cancellationToken);

        var email = UniqueEmail();
        UserId userId;

        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            var account = await unitOfWork.Users.CreateAsync(
                new NewUserAccount("Олена", "Петренко", email),
                Password,
                UserRole.Client,
                cancellationToken);

            userId = account.Id;

            // A client row pointing at a user that does not exist: the raw-SQL foreign key in the
            // DomainModel migration refuses it, so the commit fails after the account was staged.
            unitOfWork.Clients.Add(new Client
            {
                UserId = new UserId(int.MaxValue),
                PhoneNumber = "+380441234567",
            });

            await Assert.ThrowsAnyAsync<Exception>(() => unitOfWork.CommitAsync(cancellationToken));
        }

        Assert.Equal(0L, await CountUsersAsync(database, email, cancellationToken));
        Assert.Equal(
            0L,
            await CountAsync(database, "asp_net_user_roles", $"user_id = {Sql(userId)}", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "clients", $"user_id = {Sql(userId)}", cancellationToken));
    }

    [Fact]
    public async Task A_committed_registration_leaves_the_user_role_and_client_rows_together()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await database.Seeder.SeedAsync(cancellationToken);

        var email = UniqueEmail();
        UserId userId;

        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            var account = await unitOfWork.Users.CreateAsync(
                new NewUserAccount("Олена", "Петренко", email),
                Password,
                UserRole.Client,
                cancellationToken);

            userId = account.Id;

            unitOfWork.Clients.Add(new Client
            {
                UserId = account.Id,
                PhoneNumber = "+380441234567",
            });

            await unitOfWork.CommitAsync(cancellationToken);
        }

        Assert.Equal(1L, await CountUsersAsync(database, email, cancellationToken));
        Assert.Equal(
            1L,
            await CountAsync(database, "asp_net_user_roles", $"user_id = {Sql(userId)}", cancellationToken));
        Assert.Equal(1L, await CountAsync(database, "clients", $"user_id = {Sql(userId)}", cancellationToken));

        // FR-8: what is stored is a hash, and the password appears nowhere in it.
        Assert.Equal(
            0L,
            await CountAsync(
                database,
                "asp_net_users",
                $"id = {Sql(userId)} AND (password_hash IS NULL OR password_hash LIKE '%{Password}%')",
                cancellationToken));
    }

    [Fact]
    public async Task The_account_can_be_read_back_through_the_port_with_its_role_and_client_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await database.Seeder.SeedAsync(cancellationToken);

        var email = UniqueEmail();

        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            var account = await unitOfWork.Users.CreateAsync(
                new NewUserAccount("Олена", "Петренко", email),
                Password,
                UserRole.Client,
                cancellationToken);

            unitOfWork.Clients.Add(new Client { UserId = account.Id, PhoneNumber = "+380441234567" });

            await unitOfWork.CommitAsync(cancellationToken);
        }

        await using var reader = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);

        // Case-insensitively, because Identity normalizes the address and two spellings are one
        // account.
        var found = await reader.Users.FindByEmailAsync(email.ToUpperInvariant(), cancellationToken);

        Assert.NotNull(found);
        Assert.Equal(UserRole.Client, found.Role);
        Assert.NotNull(found.ClientId);
        Assert.Null(found.DriverId);
        Assert.True(await reader.Users.VerifyPasswordAsync(found.Id, Password, cancellationToken));
        Assert.False(await reader.Users.VerifyPasswordAsync(found.Id, "Wrong-Passw0rd", cancellationToken));
    }

    private static string UniqueEmail() => Guid.NewGuid().ToString("N")[..12] + "@drivetrack.test";

    private static string Sql(UserId userId) => userId.Value.ToString(CultureInfo.InvariantCulture);

    private static Task<long> CountUsersAsync(
        TestDatabase database,
        string email,
        CancellationToken cancellationToken) =>
        CountAsync(
            database,
            "asp_net_users",
            $"normalized_email = '{email.ToUpperInvariant()}'",
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
