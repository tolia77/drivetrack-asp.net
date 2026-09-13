using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Chat;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Support;

[assembly: AssemblyFixture(typeof(DriveTrack.Integration.Tests.Authorization.AuthorizationWorldFixture))]

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>
/// One host and one seeded cast, shared by every suite in this namespace.
/// <para>
/// The sweep asks the same question of roughly fifty endpoints and four roles, which is a couple of
/// hundred requests. A host and a migrated database per case would add minutes to a fifty-second
/// suite for no extra claim: every one of those calls is a call the sweep expects to be
/// <em>refused</em>, and a refusal writes nothing, so there is nothing for one case to leave behind
/// for the next one to trip over.
/// </para>
/// <para>
/// <b>Nothing in this namespace writes.</b> That is not a convention, it is what makes
/// <c>EndpointBoundaryTests.Replaying_every_refusal_inserts_and_deletes_no_row_in_any_table</c>
/// meaningful:
/// the snapshot it takes is only stable because the other suites here, running in parallel with it,
/// are read-only. The allowed-role probes in the matrix are shaped so that passing authorization
/// still cannot mutate — a missing row id for the load-then-guard methods, a body the validator
/// rejects for the guard-then-validate ones.
/// </para>
/// <para>
/// Built with <c>useProbeAuthentication: false</c>, so the scheme under test is the production
/// policy scheme — bearer under <c>/api</c>, cookie everywhere else — rather than the probe handler
/// the Epic 1 envelope suites install.
/// </para>
/// </summary>
internal sealed class AuthorizationWorld : IAsyncDisposable
{
    /// <summary>
    /// A row id no table holds, and the reason most of the matrix can probe an allowed role safely.
    /// <para>
    /// Every single-row method in this system loads, guards, and only then answers 404 — so a
    /// refused role is refused at a missing id exactly as it would be at a real one, and an allowed
    /// role reaches a 404 rather than a write.
    /// </para>
    /// </summary>
    public const int MissingId = 999_999;

    /// <summary>The seeded driver's given name.</summary>
    public const string DriverAFirstName = "Оверко";

    /// <summary>
    /// The seeded driver's family name: a distinctive string, so <c>DisclosureBoundaryTests</c> can
    /// look for it in a raw response body rather than argue from the shape of a DTO (AD-17).
    /// </summary>
    public const string DriverALastName = "Нездоланний";

    /// <summary>The seeded client's given name.</summary>
    public const string ClientAFirstName = "Ярина";

    /// <summary>The seeded client's family name.</summary>
    public const string ClientALastName = "Голуб";

    /// <summary>
    /// The seeded client's telephone number — distinctive for the same reason the driver's family
    /// name is, and on the other side of the disclosure rule (FR-27).
    /// </summary>
    public const string ClientAPhone = "+380671112233";

    /// <summary>Who took the parcel at the door. Deliberately nobody else in the cast.</summary>
    public const string ProofRecipientName = "Панас Одержувач";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static AuthorizationWorld? _instance;

    private AuthorizationWorld(ApiFactory factory, HttpClient client)
    {
        Factory = factory;
        Client = client;
    }

    /// <summary>The host, running the production <c>Program.cs</c> on a database of its own.</summary>
    public ApiFactory Factory { get; }

    /// <summary>An HTTP client onto that host. Thread-safe, and shared by every suite here.</summary>
    public HttpClient Client { get; }

    /// <summary>The seeded administrator's bearer token (FR-9).</summary>
    public string AdminToken { get; private init; } = string.Empty;

    /// <summary>A dispatcher's bearer token.</summary>
    public string DispatcherToken { get; private init; } = string.Empty;

    /// <summary>The address the dispatcher account signs in with.</summary>
    public string DispatcherEmail { get; private init; } = string.Empty;

    /// <summary>The driver who carries the seeded delivery.</summary>
    public DeliveryApi.DriverCaller DriverA { get; private init; } = null!;

    /// <summary>A second driver, party to nothing — the wrong owner of every driver-owned row.</summary>
    public DeliveryApi.DriverCaller DriverB { get; private init; } = null!;

    /// <summary>The client the seeded delivery belongs to.</summary>
    public DeliveryApi.ClientCaller ClientA { get; private init; } = null!;

    /// <summary>A second client, party to nothing.</summary>
    public DeliveryApi.ClientCaller ClientB { get; private init; } = null!;

    /// <summary>
    /// Client A's <em>user</em> row id — which is what <c>RequireSelf</c> compares and is a
    /// different id from the client row one a delivery refers to them by (AD-22).
    /// </summary>
    public int ClientAUserId { get; private init; }

    /// <summary>The vehicle driver A holds.</summary>
    public int VehicleId { get; private init; }

    /// <summary>The delivery: client A's, carried by driver A, proved and delivered.</summary>
    public int DeliveryId { get; private init; }

    /// <summary>An open shift belonging to driver A.</summary>
    public int ShiftId { get; private init; }

    /// <summary>A review client A wrote about the seeded delivery.</summary>
    public int ReviewId { get; private init; }

    /// <summary>The signature asset of the seeded delivery's proof.</summary>
    public int ProofAssetId { get; private init; }

    /// <summary>A line already in driver A's conversation, so history exists to be refused.</summary>
    public int MessageId { get; private init; }

    /// <summary>The administrator's session cookie, for the screen and hub assertions.</summary>
    public string AdminCookie { get; private init; } = string.Empty;

    /// <summary>The dispatcher's session cookie.</summary>
    public string DispatcherCookie { get; private init; } = string.Empty;

    /// <summary>Driver A's session cookie.</summary>
    public string DriverACookie { get; private init; } = string.Empty;

    /// <summary>Client A's session cookie.</summary>
    public string ClientACookie { get; private init; } = string.Empty;

    /// <summary>The driver's display name as the system renders it: given name, then family name.</summary>
    public static string DriverAName => DriverAFirstName + " " + DriverALastName;

    /// <summary>
    /// The one world, built on first use and reused by every suite in this namespace.
    /// <para>
    /// A static rather than a second assembly fixture injected into the first: xUnit constructs an
    /// assembly fixture for <em>every</em> run of this assembly, including the single filtered run
    /// the NFR-29 design-token gate performs from <c>AfterTargets="Build"</c>, which touches no
    /// database at all. <see cref="AuthorizationWorldFixture"/> is what disposes this, and it is
    /// deliberately inert until something here has actually asked for a world.
    /// </para>
    /// </summary>
    /// <param name="connectionString">The container's own database.</param>
    /// <param name="cancellationToken">The asking test's token.</param>
    public static async Task<AuthorizationWorld> InstanceAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Volatile, because this read is outside the gate: an ordinary read of a static reference
        // may be reordered against the writes that built the object it points at, so the fast path
        // could hand a second thread a world whose fields it cannot yet see.
        if (Volatile.Read(ref _instance) is { } built)
        {
            return built;
        }

        await Gate.WaitAsync(cancellationToken);

        try
        {
            // CancellationToken.None for the build itself, deliberately. The world is shared, so
            // the asking test's token is the wrong lifetime for it: one test timing out would abort
            // a build every other suite in this namespace is waiting on. The wait above still takes
            // the caller's token, so a cancelled test stops waiting rather than stopping the build.
            return _instance ??= await CreateAsync(connectionString, CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Tears the world down, if one was ever built.</summary>
    public static async ValueTask ShutdownAsync()
    {
        // Under the gate, like every other write to the field: a build racing this would otherwise
        // publish an instance into a static nobody will dispose again.
        await Gate.WaitAsync();

        try
        {
            var built = _instance;
            _instance = null;

            if (built is not null)
            {
                await built.DisposeAsync();
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A bearer token for one of the four roles.</summary>
    public string TokenFor(UserRole role) => role switch
    {
        UserRole.Admin => AdminToken,
        UserRole.Dispatcher => DispatcherToken,
        UserRole.Driver => DriverA.Token,
        UserRole.Client => ClientA.Token,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No caller of that role is seeded."),
    };

    /// <summary>A <c>drivetrack.session</c> cookie for one of the four roles.</summary>
    public string CookieFor(UserRole role) => role switch
    {
        UserRole.Admin => AdminCookie,
        UserRole.Dispatcher => DispatcherCookie,
        UserRole.Driver => DriverACookie,
        UserRole.Client => ClientACookie,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No caller of that role is seeded."),
    };

    /// <summary>Sends a request built by the matrix, as the caller the token names.</summary>
    /// <param name="request">The request, freshly built — content cannot be replayed.</param>
    /// <param name="token">A bearer token, or null to present no credentials at all.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string? token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return Client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// One row count per table in the <c>public</c> schema, read outside EF.
    /// <para>
    /// Every table rather than a list somebody has to remember to extend: a capability that lands
    /// with a table of its own is then covered by the "and the action did not occur" assertion
    /// without anyone editing it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, long>> RowCountsAsync(CancellationToken cancellationToken)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);

        await using var connection = await Factory.Database.OpenConnectionAsync(cancellationToken);

        var tables = new List<string>();

        await using (var listing = connection.CreateCommand())
        {
            // The migration history is excluded: it records what the schema is, not what a caller
            // did, and no request can change it.
            // Ordinary, partitioned and foreign tables alike. A capability that arrives with a
            // partitioned table would otherwise be invisible to the snapshot, which is the one
            // shape of new table this assertion most needs to see.
            listing.CommandText = """
                SELECT c.relname
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'f')
                  AND n.nspname = 'public'
                  AND c.relname <> '__EFMigrationsHistory'
                ORDER BY c.relname
                """;

            await using var reader = await listing.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            await using var counting = connection.CreateCommand();

            // The name comes from the catalogue rather than from a caller, and is quoted so a
            // mixed-case identifier is read as itself. Schema-qualified to match the listing above,
            // which filtered on nspname = 'public': an unqualified name is resolved through
            // search_path instead, so a same-named table in another schema would be counted here
            // while the one a request can write goes unwatched.
            counting.CommandText = "SELECT COUNT(*) FROM \"public\".\"" + table + "\"";

            counts[table] = Convert.ToInt64(
                await counting.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        return counts;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        await Factory.DisposeAsync();
    }

    private static async Task<AuthorizationWorld> CreateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // AD-12's outbound ports answered inside the process. The geocoder and the mail transport
        // are reached by the side effects a seeded delivery queues, and the object store by the
        // proof capture below; a sweep that let any of them out would assert against somebody
        // else's uptime.
        var factory = await FleetApi.CreateAsync(
            connectionString,
            cancellationToken,
            OutboundPorts.Replace(new FakeGeocoder(), new FakeEmailSender(), new FakeAssetStore()));

        var client = factory.CreateClient();

        try
        {
            return await SeedAsync(factory, client, cancellationToken);
        }
        catch
        {
            // A seeding step that throws leaves _instance null, so the fixture would have nothing to
            // shut down and this host, its hosted workers and its database would be held for the
            // rest of the assembly run. The failure is rethrown unchanged; only the leak is fixed.
            client.Dispose();

            await factory.DisposeAsync();

            throw;
        }
    }

    private static async Task<AuthorizationWorld> SeedAsync(
        ApiFactory factory,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var adminToken = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var (dispatcherEmail, dispatcherToken) = await DispatcherAsync(factory, client, cancellationToken);

        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcherToken, 1_200m, cancellationToken);

        var driverA = await DriverAsync(
            client, dispatcherToken, vehicleId, DriverAFirstName, DriverALastName, cancellationToken);
        var driverB = await DriverAsync(
            client, dispatcherToken, vehicleId: null, "Панько", "Другий", cancellationToken);

        var (clientA, clientAUserId) = await ClientAsync(
            client, dispatcherToken, ClientAFirstName, ClientALastName, ClientAPhone, cancellationToken);
        var (clientB, _) = await ClientAsync(
            client, dispatcherToken, "Мотря", "Друга", "+380445556677", cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcherToken,
            DeliveryApi.NewDelivery(driverId: driverA.DriverId, clientId: clientA.ClientId),
            cancellationToken);

        var proofAssetId = await CaptureProofAsync(client, driverA.Token, deliveryId, cancellationToken);

        await AdvanceAsync(client, driverA.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken);
        await AdvanceAsync(
            client, driverA.Token, deliveryId, DeliveryStatus.Delivered, cancellationToken, "вручено");

        var reviewId = await ReviewApiWriteAsync(client, clientA.Token, deliveryId, cancellationToken);
        var shiftId = await ShiftApi.StartedAsync(
            client, dispatcherToken, driverA.DriverId, cancellationToken);
        var messageId = await SeedMessageAsync(factory, driverA.DriverId, cancellationToken);

        // The queued side effects finish before anything is counted. A notification attempt landing
        // halfway through the row-count snapshot would fail a test about authorization with a fact
        // about a background worker.
        await OutboundPorts.DrainAsync(factory, cancellationToken);

        return new AuthorizationWorld(factory, client)
        {
            AdminToken = adminToken,
            DispatcherToken = dispatcherToken,
            DispatcherEmail = dispatcherEmail,
            DriverA = driverA,
            DriverB = driverB,
            ClientA = clientA,
            ClientB = clientB,
            ClientAUserId = clientAUserId,
            VehicleId = vehicleId,
            DeliveryId = deliveryId,
            ShiftId = shiftId,
            ReviewId = reviewId,
            ProofAssetId = proofAssetId,
            MessageId = messageId,
            AdminCookie = await ProofApi.CookieAsync(
                factory, TestConfiguration.AdminEmail, TestConfiguration.AdminPassword, cancellationToken),
            DispatcherCookie = await ProofApi.CookieAsync(
                factory, dispatcherEmail, FleetApi.Password, cancellationToken),
            DriverACookie = await ProofApi.CookieAsync(
                factory, driverA.Email, FleetApi.Password, cancellationToken),
            ClientACookie = await ProofApi.CookieAsync(
                factory, clientA.Email, FleetApi.Password, cancellationToken),
        };
    }

    /// <summary>
    /// A dispatcher, created through the Identity port and signed in over HTTP — and the address
    /// kept, which <see cref="FleetApi.TokenAsync"/> does not hand back and the cookie needs.
    /// </summary>
    private static async Task<(string Email, string Token)> DispatcherAsync(
        ApiFactory factory,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var email = FleetApi.UniqueEmail();

        await using (var unitOfWork = await factory.Database.UnitOfWorkFactory
                         .CreateAsync(cancellationToken))
        {
            await unitOfWork.Users.CreateAsync(
                new NewUserAccount("Ігор", "Диспетчерський", email),
                FleetApi.Password,
                UserRole.Dispatcher,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        return (email, await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken));
    }

    /// <summary>
    /// Takes a driver on through <c>POST /api/drivers</c>, with a name of the caller's choosing.
    /// <para>
    /// Through the endpoint rather than by writing rows, for the reason <c>DeliveryApi</c> does it
    /// that way: a driver is an account <em>and</em> a driver row, and an account holding role
    /// <c>Driver</c> with nothing behind it carries a null driver claim — which is the state
    /// <c>RequireScope</c> refuses, so a sweep arranged that way would assert the wrong failure.
    /// </para>
    /// </summary>
    private static async Task<DeliveryApi.DriverCaller> DriverAsync(
        HttpClient client,
        string dispatcherToken,
        int? vehicleId,
        string firstName,
        string lastName,
        CancellationToken cancellationToken)
    {
        var email = FleetApi.UniqueEmail();

        int driverId;

        using (var created = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/drivers",
                   dispatcherToken,
                   new
                   {
                       firstName,
                       lastName,
                       email,
                       password = FleetApi.Password,
                       licenseNumber = Guid.NewGuid().ToString("N")[..10],
                       vehicleId,
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            driverId = (await FleetApi.DataAsync(created, cancellationToken))
                .GetProperty("id")
                .GetInt32();
        }

        var token = await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);

        return new DeliveryApi.DriverCaller(driverId, token, email);
    }

    /// <summary>
    /// Registers a client through FR-1's public endpoint and reads both of their ids back: the
    /// client row a delivery refers to them by, and the user row <c>RequireSelf</c> compares.
    /// </summary>
    private static async Task<(DeliveryApi.ClientCaller Caller, int UserId)> ClientAsync(
        HttpClient client,
        string dispatcherToken,
        string firstName,
        string lastName,
        string phoneNumber,
        CancellationToken cancellationToken)
    {
        var email = FleetApi.UniqueEmail();

        using (var registration = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/auth/register",
                   token: null,
                   new
                   {
                       firstName,
                       lastName,
                       email,
                       phoneNumber,
                       password = FleetApi.Password,
                       passwordConfirmation = FleetApi.Password,
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        }

        var token = await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);

        using var roster = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/clients", dispatcherToken, body: null, cancellationToken);

        var row = (await FleetApi.DataAsync(roster, cancellationToken))
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("email").GetString() == email);

        return (
            new DeliveryApi.ClientCaller(row.GetProperty("clientId").GetInt32(), token, email),
            row.GetProperty("userId").GetInt32());
    }

    private static async Task<int> CaptureProofAsync(
        HttpClient client,
        string driverToken,
        int deliveryId,
        CancellationToken cancellationToken)
    {
        using var captured = await ProofApi.CaptureAsync(
            client,
            driverToken,
            deliveryId,
            cancellationToken,
            recipientName: ProofRecipientName,
            signature: ProofApi.Png);

        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        return (await FleetApi.DataAsync(captured, cancellationToken))
            .GetProperty("assets")
            .EnumerateArray()
            .First(asset => asset.GetProperty("kind").GetString() == "Signature")
            .GetProperty("id")
            .GetInt32();
    }

    private static async Task AdvanceAsync(
        HttpClient client,
        string driverToken,
        int deliveryId,
        DeliveryStatus status,
        CancellationToken cancellationToken,
        string? note = null)
    {
        using var response = await DeliveryApi.ChangeStatusAsync(
            client, driverToken, deliveryId, status, cancellationToken, note);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<int> ReviewApiWriteAsync(
        HttpClient client,
        string clientToken,
        int deliveryId,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/reviews",
            clientToken,
            new { deliveryId, rating = 5, text = "усе вчасно" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }

    /// <summary>
    /// One line already in driver A's conversation, written straight into the table.
    /// <para>
    /// Seeded rather than sent, because sending it would need a dispatcher's hub connection and the
    /// only claim it supports here is FR-75's: that history is unreachable without credentials.
    /// </para>
    /// </summary>
    private static async Task<int> SeedMessageAsync(
        ApiFactory factory,
        int driverId,
        CancellationToken cancellationToken)
    {
        await using var context = await factory.Database.CreateContextAsync(cancellationToken);

        var message = new Message
        {
            DriverId = new DriverId(driverId),
            SenderUserId = null,
            Text = "рядок історії, який ніхто сторонній не побачить",
            SentAt = new DateTimeOffset(2026, 9, 13, 9, 0, 0, TimeSpan.Zero),
        };

        context.Messages.Add(message);

        await context.SaveChangesAsync(cancellationToken);

        return message.Id;
    }
}

/// <summary>
/// Disposes <see cref="AuthorizationWorld"/> at the end of the assembly run, and does nothing
/// whatsoever before that.
/// <para>
/// An assembly fixture is constructed for every run of this assembly — including the filtered
/// design-token run <c>dotnet build</c> performs, which speaks to no database and must not start a
/// container. Everything expensive stays behind <see cref="AuthorizationWorld.InstanceAsync"/>,
/// exactly as <c>PostgresFixture</c> keeps the container behind its own lazy field.
/// </para>
/// </summary>
public sealed class AuthorizationWorldFixture : IAsyncLifetime
{
    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => AuthorizationWorld.ShutdownAsync();
}
