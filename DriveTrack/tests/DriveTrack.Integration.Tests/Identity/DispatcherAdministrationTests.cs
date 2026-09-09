using System.Globalization;
using System.Net;
using System.Text.Json;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Identity;

/// <summary>
/// The dispatcher rows of story 7.1's I/O matrix (FR-49, FR-50), over HTTP, against the real
/// pipeline and the real JWT scheme.
/// <para>
/// Two of these are the story's headline claims. Creating a dispatcher is the first thing in the
/// system that can, so "the account signs in and holds role Dispatcher" is the only proof that FR-49
/// is done; and an edit with no password in the body has to leave the existing one working, which is
/// what <c>Optional&lt;T&gt;</c> exists for.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DispatcherAdministrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_admin_creates_a_dispatcher_who_can_then_sign_in_as_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var email = AdministrationApi.UniqueEmail();

        int userId;

        using (var response = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/dispatchers",
            admin,
            new
            {
                firstName = "Ігор",
                lastName = "Ковальчук",
                email,
                password = AdministrationApi.Password,
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var account = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("data");

            userId = account.GetProperty("userId").GetInt32();

            Assert.Equal("Ігор", account.GetProperty("firstName").GetString());
            Assert.Equal(email, account.GetProperty("email").GetString());
        }

        // The account exists, holds exactly one role row (AD-4), and it is the right one.
        using (var session = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/auth/sign-in",
            token: null,
            new { email, password = AdministrationApi.Password },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);

            // AD-21: the role crosses the wire as its member name.
            Assert.Equal(
                nameof(UserRole.Dispatcher),
                (await AdministrationApi.ReadAsync(session, cancellationToken))
                    .GetProperty("data").GetProperty("role").GetString());
        }

        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory, "asp_net_user_roles", $"user_id = {userId}", cancellationToken));

        // DR-3: no subtype table, so nothing beyond the user and its role row was written.
        Assert.Equal(0L, await AdministrationApi.CountAsync(
            factory, "clients", $"user_id = {userId}", cancellationToken));
        Assert.Equal(0L, await AdministrationApi.CountAsync(
            factory, "drivers", $"user_id = {userId}", cancellationToken));

        // FR-8: only the hash landed.
        Assert.Equal(0L, await AdministrationApi.CountAsync(
            factory,
            "asp_net_users",
            $"id = {userId} AND password_hash LIKE '%{AdministrationApi.Password}%'",
            cancellationToken));
    }

    [Fact]
    public async Task An_email_another_account_already_holds_is_a_conflict_and_creates_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        // A client's address, not another dispatcher's: one account per address across every role.
        var taken = await AdministrationApi.RegisterClientAsync(client, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/dispatchers",
            admin,
            new
            {
                firstName = "Ігор",
                lastName = "Ковальчук",
                email = taken.Email,
                password = AdministrationApi.Password,
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AdministrationApi.AssertFailureAsync(
            response, ErrorCode.AUTH_EMAIL_ALREADY_IN_USE, cancellationToken);

        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory,
            "asp_net_users",
            $"normalized_email = '{taken.Email.ToUpperInvariant()}'",
            cancellationToken));
    }

    [Fact]
    public async Task An_edit_that_omits_the_password_leaves_the_existing_one_signing_in()
    {
        // FR-50 and AD-23, which is the row this whole story turns on: the absent case has to be a
        // type, because the alternative is a magic empty string every caller has to remember.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.CreateDispatcherAsync(
            client, admin, cancellationToken, firstName: "Ігор", lastName: "Ковальчук");

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { firstName = "Богдан" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var account = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("data");

            Assert.Equal("Богдан", account.GetProperty("firstName").GetString());
            Assert.Equal("Ковальчук", account.GetProperty("lastName").GetString());

            // The address is set at creation only: story 7.3 owns FR-87.
            Assert.Equal(subject.Email, account.GetProperty("email").GetString());
        }

        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task An_edit_that_carries_a_password_replaces_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { password = "New-Passw0rd" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, "New-Passw0rd", cancellationToken));
    }

    [Fact]
    public async Task A_weak_password_is_refused_by_the_same_policy_as_at_registration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.CreateDispatcherAsync(
            client, admin, cancellationToken, firstName: "Ігор");

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { firstName = "Богдан", password = "short" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        // AD-5: one unit of work, so the name in the same body did not land either.
        using var read = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, Route(subject.UserId), admin, body: null, cancellationToken);

        Assert.Equal(
            "Ігор",
            (await AdministrationApi.ReadAsync(read, cancellationToken))
                .GetProperty("data").GetProperty("firstName").GetString());

        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task The_dispatcher_surface_answers_every_other_kind_of_user_as_absent()
    {
        // The row that keeps this surface from being a general account editor: an admin's id, a
        // driver's id and an id nobody holds all answer the same 404.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var adminUserId = await AdministrationApi.AdminUserIdAsync(client, cancellationToken);
        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var driverUserId = await CreateDriverAsync(factory, cancellationToken);

        foreach (var id in new[] { adminUserId, subject.UserId, driverUserId, 999_999 })
        {
            using var read = await AdministrationApi.SendAsync(
                client, HttpMethod.Get, Route(id), admin, body: null, cancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            await AdministrationApi.AssertFailureAsync(read, ErrorCode.COMMON_NOT_FOUND, cancellationToken);

            using var edit = await AdministrationApi.SendAsync(
                client, HttpMethod.Patch, Route(id), admin,
                new { firstName = "Хтось" }, cancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);

            using var delete = await AdministrationApi.SendAsync(
                client, HttpMethod.Delete, Route(id), admin, body: null, cancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        // None of them was renamed or removed on the way past.
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory, "asp_net_users", $"id = {adminUserId}", cancellationToken));
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory, "asp_net_users", $"id = {driverUserId}", cancellationToken));
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory, "clients", $"user_id = {subject.UserId}", cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_dispatcher_leaves_the_history_they_touched_unattributed()
    {
        // AD-20's actor rule, over the wire: not restrict - which would make anyone who has ever
        // acted permanently undeletable, the exact defect this rewrite exists to fix - and not
        // cascade, which would erase the history to close an account.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);

        int entryId;

        await using (var context = await factory.Database.CreateContextAsync(cancellationToken))
        {
            var delivery = await Seed.DeliveryAsync(context, cancellationToken);

            var entry = Seed.NewTimelineEntry(delivery.Id, new UserId(subject.UserId));

            context.TimelineEntries.Add(entry);
            await context.SaveChangesAsync(cancellationToken);

            entryId = entry.Id;
        }

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Delete, Route(subject.UserId), admin, body: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal(0L, await AdministrationApi.CountAsync(
            factory, "asp_net_users", $"id = {subject.UserId}", cancellationToken));

        // The entry survives, with a null actor and the snapshotted name still readable - which is
        // why the snapshot columns exist.
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory,
            "timeline_entries",
            $"id = {entryId} AND actor_user_id IS NULL AND actor_display_name IS NOT NULL",
            cancellationToken));

        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task Nobody_below_an_administrator_reaches_this_surface_at_all()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var dispatcher = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);
        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);

        var dispatcherToken = await AdministrationApi.SignInAsync(
            client, dispatcher.Email, AdministrationApi.Password, cancellationToken);
        var clientToken = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        foreach (var token in new[] { dispatcherToken, clientToken })
        {
            using var list = await AdministrationApi.SendAsync(
                client, HttpMethod.Get, "/api/dispatchers", token, body: null, cancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
            await AdministrationApi.AssertFailureAsync(list, ErrorCode.AUTH_FORBIDDEN, cancellationToken);

            using var create = await AdministrationApi.SendAsync(
                client, HttpMethod.Post, "/api/dispatchers", token,
                new
                {
                    firstName = "Ігор",
                    lastName = "Ковальчук",
                    email = AdministrationApi.UniqueEmail(),
                    password = AdministrationApi.Password,
                },
                cancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

            using var delete = await AdministrationApi.SendAsync(
                client, HttpMethod.Delete, Route(dispatcher.UserId), token, body: null, cancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }

        // A dispatcher refused their own roster is still there afterwards.
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory, "asp_net_users", $"id = {dispatcher.UserId}", cancellationToken));

        using var anonymous = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, "/api/dispatchers", token: null, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await AdministrationApi.AssertFailureAsync(
            anonymous, ErrorCode.AUTH_UNAUTHENTICATED, cancellationToken);
    }

    [Fact]
    public async Task The_roster_carries_every_dispatcher_and_nobody_else()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var adminUserId = await AdministrationApi.AdminUserIdAsync(client, cancellationToken);
        var first = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);
        var second = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);
        var outsider = await AdministrationApi.RegisterClientAsync(client, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, "/api/dispatchers", admin, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ids = (await AdministrationApi.ReadAsync(response, cancellationToken))
            .GetProperty("data")
            .EnumerateArray()
            .Select(row => row.GetProperty("userId").GetInt32())
            .ToArray();

        Assert.Contains(first.UserId, ids);
        Assert.Contains(second.UserId, ids);

        // The list is a join on the role rows, not "every user": an admin and a client are not
        // dispatchers and must not appear.
        Assert.DoesNotContain(adminUserId, ids);
        Assert.DoesNotContain(outsider.UserId, ids);
    }

    [Fact]
    public async Task An_edit_that_changes_the_name_and_the_password_together_commits_both()
    {
        // The operation EfUserAccountRepository.UpdateNameAsync is hand-written to make possible,
        // and the one no other case here reaches. Every other body changes a name or a password;
        // the weak-password row sends both but Identity refuses before the commit, so the two
        // staged Identity writes never meet at CommitAsync. If UpdateNameAsync went through
        // UserManager.UpdateAsync - the obvious implementation - this body alone would build a
        // WHERE clause on a concurrency stamp it invented, match no row, and answer 409 for a row
        // nobody else touched.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.CreateDispatcherAsync(
            client, admin, cancellationToken, firstName: "Ігор", lastName: "Ковальчук");

        using (var response = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Patch,
            Route(subject.UserId),
            admin,
            new { firstName = "Богдан", lastName = "Шевченко", password = "New-Passw0rd" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Read back rather than trusted from the response: the response is built in memory, and the
        // claim here is about what the single commit wrote.
        using (var read = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, Route(subject.UserId), admin, body: null, cancellationToken))
        {
            var account = (await AdministrationApi.ReadAsync(read, cancellationToken))
                .GetProperty("data");

            Assert.Equal("Богдан", account.GetProperty("firstName").GetString());
            Assert.Equal("Шевченко", account.GetProperty("lastName").GetString());
        }

        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, "New-Passw0rd", cancellationToken));
    }

    [Fact]
    public async Task A_weak_password_at_creation_is_refused_and_opens_no_account()
    {
        // Creation reaches Identity through CreateAsync, which is a different call path from the
        // edit's AddPasswordAsync. The policy has to be the same one, and the refusal has to leave
        // no half-made account behind - a user row with no role row would be exactly the broken
        // invariant MapAsync reports as a defect.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var email = AdministrationApi.UniqueEmail();

        using (var response = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/dispatchers",
            admin,
            new { firstName = "Ігор", lastName = "Ковальчук", email, password = "short" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        Assert.Equal(0L, await AdministrationApi.CountAsync(
            factory,
            "asp_net_users",
            $"normalized_email = '{email.ToUpperInvariant()}'",
            cancellationToken));
    }

    [Fact]
    public async Task Padding_around_a_submitted_name_is_trimmed_before_it_is_stored()
    {
        // The trim happens as the value is merged, not after it is validated, so what the validator
        // judged and what the column holds are the same string. Nothing else in this suite submits
        // a padded value, so without this the trim could be deleted or moved after validation and
        // every other assertion would still pass.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);

        // Creation trims through a different path from the edit - the command is rewritten before
        // the validator sees it rather than merged onto a loaded row - so both are asserted.
        using (var created = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/dispatchers",
            admin,
            new
            {
                firstName = "  Оксана  ",
                lastName = " Мельник ",
                email = AdministrationApi.UniqueEmail(),
                password = AdministrationApi.Password,
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var account = (await AdministrationApi.ReadAsync(created, cancellationToken))
                .GetProperty("data");

            Assert.Equal("Оксана", account.GetProperty("firstName").GetString());
            Assert.Equal("Мельник", account.GetProperty("lastName").GetString());
        }

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { firstName = "  Богдан  ", lastName = " Шевченко " }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var read = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, Route(subject.UserId), admin, body: null, cancellationToken);

        var stored = (await AdministrationApi.ReadAsync(read, cancellationToken)).GetProperty("data");

        Assert.Equal("Богдан", stored.GetProperty("firstName").GetString());
        Assert.Equal("Шевченко", stored.GetProperty("lastName").GetString());
    }

    private static string Route(int userId) =>
        "/api/dispatchers/" + userId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A driver, written through the real ports because there is no driver endpoint yet - Epic 5
    /// owns that. The account has to be a genuine one, with a role row, or the not-found assertion
    /// above would pass for the wrong reason.
    /// </summary>
    private static async Task<int> CreateDriverAsync(
        ApiFactory factory,
        CancellationToken cancellationToken)
    {
        await using var unitOfWork = await factory.Database.UnitOfWorkFactory.CreateAsync(cancellationToken);

        var account = await unitOfWork.Users.CreateAsync(
            new NewUserAccount("Іван", "Мельник", AdministrationApi.UniqueEmail()),
            AdministrationApi.Password,
            UserRole.Driver,
            cancellationToken);

        unitOfWork.Drivers.Add(new Driver
        {
            UserId = account.Id,
            LicenseNumber = Guid.NewGuid().ToString("N")[..10],
        });

        await unitOfWork.CommitAsync(cancellationToken);

        return account.Id.Value;
    }
}
