using System.Net;
using System.Net.Http.Json;
using DriveTrack.Application.Chat;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Architecture;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Web.Api;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>
/// The hub, swept the way the REST surface is: every method, every caller who should not reach it.
/// <para>
/// <c>ChatHub</c> is a fifth entry point into the system and the only one that is neither a
/// controller nor a screen, so nothing in the endpoint matrix or the screen matrix covers it.
/// <c>ChatHubTests</c> asserts the capability's behaviour; what this adds is the boundary statement
/// — including <c>LeaveThread</c>, which reaches no guard by design and which no test named it
/// before.
/// </para>
/// </summary>
public class HubBoundaryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_handshake_with_no_credentials_is_refused_and_reaches_no_hub_method()
    {
        // FR-75. Asserted at the HTTP level as well as through the client, because the status is
        // the contract: 401 rather than the cookie handler's redirect to a sign-in page, which a
        // programmatic caller cannot read.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        using (var negotiate = await world.Client.PostAsJsonAsync(
                   new Uri("/hubs/chat/negotiate?negotiateVersion=1", UriKind.Relative),
                   new { },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        }

        await using var connection = Build(world, token: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(cancellationToken));
    }

    [Fact]
    public void No_controller_can_answer_a_message_so_the_hub_is_the_only_door_to_history()
    {
        // Not a refusal, and not FR-75 itself: the claim that history is unreachable without
        // credentials is carried by the handshake test above, which is where an anonymous caller is
        // actually turned away. What this adds is the other half of that sentence - that the hub is
        // the *only* door, so refusing the handshake refuses history.
        //
        // Asserted as a dependency rather than by looking for "chat" or "message" in a route
        // template, because a route named /api/threads or .../history would satisfy a name scan
        // untouched. A controller cannot answer a stored message without the capability that reads
        // them, so this is the property itself.
        var controllers = LayerAssemblies.Resolve("DriveTrack.Web")
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(typeof(ControllerBase).IsAssignableFrom)
            .ToArray();

        Assert.NotEmpty(controllers);

        var reachers = controllers
            .Where(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(IChatService)))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(reachers);
    }

    [Fact]
    public async Task A_client_and_an_administrator_reach_no_thread_and_no_roster()
    {
        // AD-15's two deliberate exclusions, on all three of the methods that take a decision. An
        // administrator is refused here and nowhere else in the system, which is exactly the kind
        // of asymmetry a sweep has to restate rather than assume.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        foreach (var role in new[] { UserRole.Client, UserRole.Admin })
        {
            await using var connection = await ConnectAsync(world, world.TokenFor(role), cancellationToken);

            AssertRefusal(
                await Assert.ThrowsAsync<HubException>(() =>
                    connection.InvokeAsync<IReadOnlyList<ChatThreadSummary>>("ListThreads", cancellationToken)),
                ErrorCode.AUTH_FORBIDDEN);

            AssertRefusal(
                await Assert.ThrowsAsync<HubException>(() =>
                    connection.InvokeAsync<ChatThread>(
                        "JoinThread", world.DriverA.DriverId, cancellationToken)),
                ErrorCode.AUTH_FORBIDDEN);

            AssertRefusal(
                await Assert.ThrowsAsync<HubException>(() =>
                    connection.InvokeAsync("Send", world.DriverA.DriverId, "чуже", cancellationToken)),
                ErrorCode.AUTH_FORBIDDEN);
        }
    }

    [Fact]
    public async Task Another_driver_is_refused_the_thread_a_participant_can_read()
    {
        // The refusal, and the participant read that keeps it from being vacuous: the conversation
        // does exist and does hold history, and this driver is told none of it.
        //
        // The consequence - that a refused caller is not in the group, so the next line of the
        // conversation never reaches them - is asserted in
        // ChatHubTests.A_driver_opening_another_drivers_thread_is_refused_and_told_nothing, which
        // sends a real message on a host of its own. It is not repeated here because a send would
        // write a row, and nothing in this namespace writes.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        await using var intruder = await ConnectAsync(world, world.DriverB.Token, cancellationToken);

        AssertRefusal(
            await Assert.ThrowsAsync<HubException>(() =>
                intruder.InvokeAsync<ChatThread>("JoinThread", world.DriverA.DriverId, cancellationToken)),
            ErrorCode.AUTH_FORBIDDEN);

        await using var desk = await ConnectAsync(world, world.DispatcherToken, cancellationToken);

        var thread = await desk.InvokeAsync<ChatThread>(
            "JoinThread", world.DriverA.DriverId, cancellationToken);

        // The seeded line by its row id, not merely "some history": the driver above was refused
        // this exact message.
        Assert.Contains(thread.Messages, message => message.Id == world.MessageId);
    }

    [Fact]
    public async Task Leaving_a_thread_nobody_may_read_is_allowed_and_grants_nothing()
    {
        // LeaveThread reaches no guard, on purpose: giving up a subscription discloses nothing, and
        // a caller who was never in the group leaves one they were not in (ChatHub.cs). Untested
        // until now, which meant the file's claim and the file's behaviour were the same sentence.
        //
        // Pinned from both sides. It does not throw for a caller chat refuses - so the documented
        // "no decision to take" is what actually happens - and it hands that caller nothing: the
        // join that follows is refused exactly as it was before.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        // Both roles chat excludes and a driver who is not the thread's: the administrator is as
        // much a stranger to a thread as the client is (AD-15), and leaving one has to be as
        // uneventful for them.
        foreach (var token in new[]
        {
            world.TokenFor(UserRole.Client),
            world.TokenFor(UserRole.Admin),
            world.DriverB.Token,
        })
        {
            await using var connection = await ConnectAsync(world, token, cancellationToken);

            await connection.InvokeAsync("LeaveThread", world.DriverA.DriverId, cancellationToken);

            AssertRefusal(
                await Assert.ThrowsAsync<HubException>(() =>
                    connection.InvokeAsync<ChatThread>(
                        "JoinThread", world.DriverA.DriverId, cancellationToken)),
                ErrorCode.AUTH_FORBIDDEN);
        }
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_reach_leave_thread_either()
    {
        // LeaveThread takes no decision of its own, so the connection is the whole of its
        // protection: what keeps it out of an anonymous caller's reach is the hub's [Authorize] and
        // nothing else, and dropping that attribute would leave one hub method open to anybody.
        //
        // The claim is therefore exactly that the connection cannot be established. Invoking
        // LeaveThread on a connection that never started would prove nothing about the server - the
        // SignalR client refuses it before a byte leaves the process - so this does not pretend to.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        await using var connection = Build(world, token: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(cancellationToken));
    }

    private static void AssertRefusal(HubException failure, ErrorCode expected)
    {
        // A HubException carries one string, and what crosses the wire is the resource key the
        // screen renders - never a sentence, and never a stack frame (NFR-3).
        Assert.Contains(expected.ToString(), failure.Message, StringComparison.Ordinal);

        foreach (var leak in new[] { "   at ", "SELECT ", "Npgsql", "DriveTrack.Application" })
        {
            Assert.DoesNotContain(leak, failure.Message, StringComparison.Ordinal);
        }
    }

    private static async Task<HubConnection> ConnectAsync(
        AuthorizationWorld world,
        string token,
        CancellationToken cancellationToken)
    {
        var connection = Build(world, token);

        await connection.StartAsync(cancellationToken);

        return connection;
    }

    /// <summary>
    /// An unstarted connection to <c>/hubs/chat</c>, over long polling because that is the
    /// transport a <c>TestServer</c> can serve — it has no socket to upgrade. Everything asserted
    /// here is the same on either transport.
    /// </summary>
    private static HubConnection Build(AuthorizationWorld world, string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(world.Factory.Server.BaseAddress, "hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => world.Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;

                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .AddJsonProtocol(options =>
            {
                // The adapter's own converters, exactly as the server's protocol takes them: a
                // DriverId arrives as the number it is and would not read back without them.
                options.PayloadSerializerOptions.Converters.Add(new UserIdJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new DriverIdJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new ClientIdJsonConverter());
            })
            .Build();
}
