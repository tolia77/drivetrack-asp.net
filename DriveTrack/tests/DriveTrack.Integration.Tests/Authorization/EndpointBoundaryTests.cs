using System.Globalization;
using System.Net;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>
/// The role half of the sweep: every REST endpoint, every role, and the refusal each one owes.
/// <para>
/// Asserted through the AD-7 envelope rather than on the status code alone. A 403 whose body is
/// empty, or an HTML page, or a framework problem-details document is a refusal no client can act
/// on and no localized catalogue has a message for — and it is exactly what a missing suppression
/// or a mis-wired authentication event produces, which is a failure the status code cannot see.
/// </para>
/// <para>
/// Every request here is one the sweep expects to be refused, except the one probe per row, which
/// is shaped so that passing authorization still cannot write. That is what lets the whole
/// namespace share <see cref="AuthorizationWorld"/>.
/// </para>
/// </summary>
public class EndpointBoundaryTests(PostgresFixture postgres)
{
    /// <summary>Every guarded row, by key. The two public ones are asserted separately.</summary>
    public static TheoryData<string> Guarded
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var row in EndpointMatrix.Rows.Where(row => !row.IsPublic))
            {
                data.Add(row.Key);
            }

            return data;
        }
    }

    /// <summary>Every (row, role) pair the matrix says must be refused.</summary>
    public static TheoryData<string, string> Refusals
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var row in EndpointMatrix.Rows)
            {
                foreach (var role in row.Refused)
                {
                    data.Add(row.Key, role.ToString());
                }
            }

            return data;
        }
    }

    /// <summary>Every guarded row, for the one caller that is allowed to reach past the guard.</summary>
    public static TheoryData<string> Probes => Guarded;

    [Theory]
    [MemberData(nameof(Guarded))]
    public async Task An_anonymous_caller_is_refused_through_the_envelope(string key)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);
        var row = EndpointMatrix.Resolve(key);

        using var request = row.Request(world);
        using var response = await world.SendAsync(request, token: null, cancellationToken);

        await FleetApi.AssertFailureAsync(
            response,
            HttpStatusCode.Unauthorized,
            ErrorCode.AUTH_UNAUTHENTICATED,
            cancellationToken);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_role_outside_the_allowed_set_is_refused_through_the_envelope(
        string key,
        string role)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);
        var row = EndpointMatrix.Resolve(key);

        using var request = row.Request(world);
        using var response = await world.SendAsync(
            request, world.TokenFor(Enum.Parse<UserRole>(role)), cancellationToken);

        await FleetApi.AssertFailureAsync(
            response,
            HttpStatusCode.Forbidden,
            ErrorCode.AUTH_FORBIDDEN,
            cancellationToken);
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task Every_allowed_caller_reaches_past_the_guard(string key)
    {
        // The other direction, and the reason the two above are not vacuous: a route that refused
        // everybody would pass every assertion in this file without the boundary being anywhere.
        //
        // Every allowed role, not one of them. On a {Admin, Dispatcher} row a single probe as the
        // admin leaves the dispatcher in neither set - not refused, not probed - so a regression
        // that started answering dispatchers 403 would pass the whole sweep in silence.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);
        var row = EndpointMatrix.Resolve(key);

        Assert.False(
            row.Allowed.Count == 0,
            $"{row.Key} names no allowed role at all, so nothing here proves the route is reachable "
                + "by anybody - which is exactly how a route that refuses everyone passes a sweep "
                + "about refusals.");

        foreach (var role in EndpointMatrix.Roles.Where(row.Allowed.Contains))
        {
            using var request = row.Request(world);
            using var response = await world.SendAsync(
                request, world.TokenFor(role), cancellationToken);

            Assert.False(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{row.Key} refused {role}, which the matrix says is allowed to use it "
                    + $"(status {(int)response.StatusCode}).");

            // A route that threw is not a route that let the caller through. Without this, a probe
            // shaped to land on a 404 would go on "passing" the moment that path started faulting,
            // and the sweep would report a 500 as a boundary in good order.
            Assert.True(
                (int)response.StatusCode < 500,
                $"{row.Key} answered {role} with status {(int)response.StatusCode}, so what the "
                    + "probe reached was a failure rather than the guard.");
        }
    }

    [Fact]
    public async Task The_two_public_routes_answer_an_anonymous_caller_about_what_they_sent()
    {
        // FR-1 and FR-4 are the operations a caller performs before they have credentials to be
        // judged on. Neither body is legal, so both answer 422 COMMON_VALIDATION_FAILED - which is
        // the point, and is why this asserts the code rather than merely "not 401 and not 403": the
        // refusal they get is about what they sent, never about who they are.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        foreach (var row in EndpointMatrix.Rows.Where(candidate => candidate.IsPublic))
        {
            using var request = row.Request(world);
            using var response = await world.SendAsync(request, token: null, cancellationToken);

            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }
    }

    [Fact]
    public async Task Replaying_every_refusal_inserts_and_deletes_no_row_in_any_table()
    {
        // Half of "and the action does not occur", and only half: COUNT(*) sees an insert and a
        // delete that should not have happened, and cannot see an update - a 403 answered after a
        // column was written leaves the count exactly where it was. The content-level half is
        // OwnershipBoundaryTests', which reads the shift, the review and the delivery's status back
        // through dispatch's own routes on either side of the refusal.
        //
        // Counted over every table in the schema rather than a remembered list, so a capability
        // that lands with a table of its own is covered without anyone editing this.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var before = await world.RowCountsAsync(cancellationToken);

        Assert.NotEmpty(before);

        foreach (var row in EndpointMatrix.Rows.Where(candidate => !candidate.IsPublic))
        {
            using (var anonymous = row.Request(world))
            {
                using var refused = await world.SendAsync(anonymous, token: null, cancellationToken);

                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }

            foreach (var role in row.Refused)
            {
                using var request = row.Request(world);
                using var response = await world.SendAsync(
                    request, world.TokenFor(role), cancellationToken);

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }

        var after = await world.RowCountsAsync(cancellationToken);

        // Over the union of both snapshots rather than over the first one: walking `before` alone
        // can only see a table it already knew about, so a table that appeared between the two
        // would be compared against nothing and the assertion would pass without looking at it.
        var changed = before.Keys
            .Union(after.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(table => Count(before, table) != Count(after, table))
            .Select(table => table
                + ": " + Count(before, table)
                + " -> " + Count(after, table))
            .ToArray();

        Assert.Empty(changed);
    }

    /// <summary>
    /// A table's count in one snapshot, rendered so a table present in only one of them reads as
    /// the absence it is rather than as a zero somebody has to interpret.
    /// </summary>
    private static string Count(IReadOnlyDictionary<string, long> snapshot, string table) =>
        snapshot.TryGetValue(table, out var count)
            ? count.ToString(CultureInfo.InvariantCulture)
            : "absent";
}
