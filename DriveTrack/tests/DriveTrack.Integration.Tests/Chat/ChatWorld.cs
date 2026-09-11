using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Chat;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Identity;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Api;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Chat;

/// <summary>
/// One signed-in participant: who they are, and the token that proves it.
/// </summary>
/// <param name="UserId">The user row's id.</param>
/// <param name="DriverId">The driver row's id, for a driver; null for everyone else.</param>
/// <param name="Token">A bearer token, which is how the suite drives the hub.</param>
/// <param name="Email">
/// The address the account signs in with — kept so a test can obtain the <em>other</em> credential,
/// the session cookie, which is the one a real browser holds.
/// </param>
internal sealed record Participant(int UserId, int? DriverId, string Token, string Email);

/// <summary>
/// The cast every chat test needs, and the two moves it makes: open a host on the real
/// authentication schemes, and connect to its hub the way a client actually would.
/// <para>
/// The connection is a real <c>HubConnection</c> against the real pipeline, because the hub is where
/// chat's authorization is enforced: a test that called <c>ChatHub</c>'s methods directly would
/// assert against a class, not against a caller who had to negotiate, authenticate and be added to a
/// group before hearing anything. NFR-17 names messaging as the feature the original never tested,
/// and this is what testing it means.
/// </para>
/// </summary>
internal sealed class ChatWorld : IAsyncDisposable
{
    private readonly List<HubConnection> _connections = [];

    private ChatWorld(ApiFactory factory, HttpClient client)
    {
        Factory = factory;
        Client = client;
    }

    /// <summary>The host, running the production <c>Program.cs</c> on its own database.</summary>
    public ApiFactory Factory { get; }

    /// <summary>An HTTP client onto the same host, for the REST calls that open accounts.</summary>
    public HttpClient Client { get; }

    /// <summary>
    /// Boots a host whose default scheme is the production path selector rather than the test probe.
    /// Authorization is what these tests are about, so opting out of the probe is the whole point.
    /// </summary>
    public static async Task<ChatWorld> CreateAsync(string connectionString, CancellationToken cancellationToken)
    {
        var factory = await AdministrationApi.CreateHostAsync(connectionString, cancellationToken);

        return new ChatWorld(factory, factory.CreateClient());
    }

    /// <summary>The seeded administrator (FR-9), who is deliberately not a chat participant.</summary>
    public async Task<Participant> AdminAsync(CancellationToken cancellationToken)
    {
        var token = await AdministrationApi.AdminTokenAsync(Client, cancellationToken);
        var userId = await AdministrationApi.AdminUserIdAsync(Client, cancellationToken);

        return new Participant(userId, null, token, TestConfiguration.AdminEmail);
    }

    /// <summary>A dispatcher, opened through the endpoint story 7.1 ships.</summary>
    public async Task<Participant> DispatcherAsync(
        string adminToken,
        CancellationToken cancellationToken,
        string firstName = "Ігор",
        string lastName = "Ковальчук")
    {
        var (userId, email) = await AdministrationApi.CreateDispatcherAsync(
            Client, adminToken, cancellationToken, firstName, lastName);

        var token = await AdministrationApi.SignInAsync(
            Client, email, AdministrationApi.Password, cancellationToken);

        return new Participant(userId, null, token, email);
    }

    /// <summary>A client, opened through public registration (FR-1).</summary>
    public async Task<Participant> ClientAsync(CancellationToken cancellationToken)
    {
        var (userId, email) = await AdministrationApi.RegisterClientAsync(Client, cancellationToken);

        var token = await AdministrationApi.SignInAsync(
            Client, email, AdministrationApi.Password, cancellationToken);

        return new Participant(userId, null, token, email);
    }

    /// <summary>
    /// A driver, with the account and the driver row DR-3 makes inseparable.
    /// <para>
    /// Written through the unit of work rather than through a raw context so the password is hashed
    /// by Identity and sign-in accepts it, and through the unit of work rather than through the
    /// driver endpoint because that one needs a dispatcher and these tests need drivers with no
    /// dispatcher in the story.
    /// </para>
    /// </summary>
    public async Task<Participant> DriverAsync(
        CancellationToken cancellationToken,
        string firstName = "Петро",
        string lastName = "Шевченко")
    {
        var email = AdministrationApi.UniqueEmail();

        int userId;
        int driverId;

        await using (var unitOfWork = await Factory.Database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            await unitOfWork.Users.EnsureRoleAsync(UserRole.Driver, cancellationToken);

            var account = await unitOfWork.Users.CreateAsync(
                new NewUserAccount(firstName, lastName, email),
                AdministrationApi.Password,
                UserRole.Driver,
                cancellationToken);

            var driver = new Driver
            {
                UserId = account.Id,
                LicenseNumber = Guid.NewGuid().ToString("N")[..10],
            };

            unitOfWork.Drivers.Add(driver);

            await unitOfWork.CommitAsync(cancellationToken);

            userId = account.Id.Value;
            driverId = driver.Id.Value;
        }

        var token = await AdministrationApi.SignInAsync(
            Client, email, AdministrationApi.Password, cancellationToken);

        return new Participant(userId, driverId, token, email);
    }

    /// <summary>
    /// Writes a message straight into the table, for the histories a test needs to already exist.
    /// </summary>
    /// <param name="driverId">The conversation it belongs to.</param>
    /// <param name="senderUserId">Who wrote it, or null for a deleted account.</param>
    /// <param name="text">The body.</param>
    /// <param name="sentAt">When it was sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row's id, which is also its tie-break in the conversation order.</returns>
    public async Task<int> SeedMessageAsync(
        int driverId,
        int? senderUserId,
        string text,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        await using var context = await Factory.Database.CreateContextAsync(cancellationToken);

        var message = new Message
        {
            DriverId = new DriverId(driverId),
            SenderUserId = senderUserId is { } sender ? new UserId(sender) : null,
            Text = text,
            SentAt = sentAt,
        };

        context.Messages.Add(message);
        await context.SaveChangesAsync(cancellationToken);

        return message.Id;
    }

    /// <summary>
    /// A started connection to <c>/hubs/chat</c>, authenticated as <paramref name="caller"/> with a
    /// bearer token.
    /// </summary>
    public async Task<HubConnection> ConnectAsync(Participant caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var connection = Build(caller.Token);

        await connection.StartAsync(cancellationToken);

        return connection;
    }

    /// <summary>
    /// A started connection carrying <paramref name="caller"/>'s session cookie and no token at all
    /// — the credential a real browser actually holds.
    /// <para>
    /// The other connections here are bearer-authenticated, which is a credential no browser on this
    /// product ever has: the circuit holds <c>drivetrack.session</c> and nothing else, and the hub is
    /// mounted outside <c>/api</c> precisely so the path selector offers it to the cookie handler.
    /// Without a case that presents only the cookie, dropping the cookie scheme from the hub's
    /// <c>[Authorize]</c> leaves every other test in this suite green while every real browser is
    /// refused at negotiate.
    /// </para>
    /// </summary>
    public async Task<HubConnection> ConnectWithCookieAsync(
        Participant caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Minted through the sign-in form, which is the only path in the product that writes
        // Set-Cookie: a test that faked the cookie would be asserting against its own fake.
        var cookie = await ProofApi.CookieAsync(
            Factory, caller.Email, AdministrationApi.Password, cancellationToken);

        var connection = Build(token: null, cookie: cookie);

        await connection.StartAsync(cancellationToken);

        return connection;
    }

    /// <summary>
    /// An unstarted connection, for the tests that assert on the refusal to start one.
    /// <para>
    /// Long polling, because that is the transport a <c>TestServer</c> can actually serve: it has no
    /// socket to upgrade. Everything this suite asserts — negotiation, the group, the broadcast — is
    /// the same on either transport, and the browser picks WebSockets on its own.
    /// </para>
    /// <para>
    /// The protocol takes the adapter's typed-id converters, exactly as the server's does. Without
    /// them a <c>DriverId</c> arrives as the number it is and fails to read back — which is also
    /// what proves the server wrote a number rather than <c>{"value":7}</c> (AD-22, NFR-1).
    /// </para>
    /// </summary>
    /// <param name="token">A bearer token, or null for a connection presenting no token.</param>
    /// <param name="cookie">
    /// A <c>drivetrack.session</c> cookie to send on every request, or null for none. Both null is a
    /// connection with no credentials at all.
    /// </param>
    public HubConnection Build(string? token, string? cookie = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "hubs/chat"), options =>
            {
                // The test server's own handler, so the request goes through the real pipeline
                // in-process rather than over a socket.
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;

                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }

                if (cookie is not null)
                {
                    // A raw header rather than options.Cookies, so what reaches the handler is
                    // exactly the one cookie a browser would send and nothing the client added.
                    options.Headers["Cookie"] = cookie;
                }
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new UserIdJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new DriverIdJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new ClientIdJsonConverter());
            })
            .Build();

        _connections.Add(connection);

        return connection;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        Client.Dispose();

        await Factory.DisposeAsync();
    }
}
