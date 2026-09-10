using System.Net;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// The arrangement the two delivery suites share: a caller of each role, a vehicle of a chosen
/// capacity, and a delivery posted through the endpoint that ships it.
/// <para>
/// The HTTP plumbing itself is <see cref="FleetApi"/>'s and is reused unchanged — it is generic,
/// not fleet-specific. What lives here is the arrangement a delivery needs and the fleet did not:
/// a driver who can sign in, a client whose row id is known, and a vehicle whose capacity is the
/// number a test is about to exceed.
/// </para>
/// </summary>
internal static class DeliveryApi
{
    /// <summary>A driver, signed in, with the vehicle they hold.</summary>
    /// <param name="DriverId">The driver row's id, which a delivery refers to them by.</param>
    /// <param name="Token">A bearer token for the driver's own account.</param>
    internal sealed record DriverCaller(int DriverId, string Token);

    /// <summary>A client, signed in.</summary>
    /// <param name="ClientId">The client row's id, which a delivery refers to them by.</param>
    /// <param name="Token">A bearer token for the client's own account.</param>
    internal sealed record ClientCaller(int ClientId, string Token);

    /// <summary>
    /// Takes on a driver through the fleet endpoint and signs them in.
    /// <para>
    /// Through <c>POST /api/drivers</c> rather than by creating an account directly, for the reason
    /// <see cref="FleetApi.TokenAsync"/> refuses to do the latter: a driver is an account
    /// <em>and</em> a driver row, and an account holding role <c>Driver</c> with nothing behind it
    /// carries a null driver claim — which is precisely the state <c>RequireScope</c> refuses, so a
    /// test arranged that way would assert the wrong failure.
    /// </para>
    /// </summary>
    public static async Task<DriverCaller> DriverAsync(
        HttpClient client,
        string dispatcherToken,
        int? vehicleId,
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
                       firstName = "Тарас",
                       lastName = "Шевченко",
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

        return new DriverCaller(driverId, token);
    }

    /// <summary>
    /// Registers a client through the public registration endpoint, signs them in, and reads their
    /// client row id back off the roster — the id a delivery refers to them by, which the account
    /// payload does not carry.
    /// </summary>
    public static async Task<ClientCaller> ClientAsync(
        HttpClient client,
        string dispatcherToken,
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
                       firstName = "Олена",
                       lastName = "Петренко",
                       email,
                       phoneNumber = "+380441234567",
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

        return new ClientCaller(row.GetProperty("clientId").GetInt32(), token);
    }

    /// <summary>A vehicle of a chosen capacity, which is the figure a capacity test is about to cross.</summary>
    public static async Task<int> VehicleAsync(
        HttpClient client,
        string dispatcherToken,
        decimal capacityKg,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/vehicles",
            dispatcherToken,
            new
            {
                model = "Рено Мастер",
                licensePlate = FleetApi.UniquePlate(),
                capacityKg,
                mileage = 42_000,
                nextMaintenanceDate = "2026-12-01",
            },
            cancellationToken);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }

    /// <summary>
    /// The body of a create request, with the two coordinates every delivery needs and everything
    /// else optional — which is what FR-16 says a delivery is.
    /// </summary>
    public static object NewDelivery(
        decimal weight = 12.5m,
        int? driverId = null,
        int? clientId = null,
        string? notes = null,
        DateTimeOffset? windowEarliestAt = null,
        DateTimeOffset? windowLatestAt = null) =>
        new
        {
            pickup = new { latitude = 50.4501, longitude = 30.5234 },
            dropoff = new { latitude = 49.8397, longitude = 24.0297 },
            packageDetails = "Одна палета",
            packageWeightKg = weight,
            deliveryNotes = notes,
            windowEarliestAt,
            windowLatestAt,
            driverId,
            clientId,
        };

    /// <summary>Posts a delivery and returns its id, failing loudly if it was refused.</summary>
    public static async Task<int> PostAsync(
        HttpClient client,
        string dispatcherToken,
        object body,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client, HttpMethod.Post, "/api/deliveries", dispatcherToken, body, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }

    /// <summary>A dispatcher's bearer token, which every arrangement above needs first.</summary>
    public static Task<string> DispatcherAsync(
        ApiFactory factory,
        HttpClient client,
        CancellationToken cancellationToken) =>
        FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
}
