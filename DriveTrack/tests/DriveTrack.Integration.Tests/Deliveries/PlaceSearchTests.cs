using System.Net;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// FR-104 over HTTP: the address search behind the delivery form, and the two refusals that guard
/// it.
/// <para>
/// The order of those refusals is the interesting part and is only visible from here. A driver who
/// types two characters must be refused for being a driver, not told their query is too short —
/// which means the guard runs before the validator — and a query that is too short must be refused
/// before anything leaves the process, which means the validator runs before the port. Both are
/// claims about sequence, and the fake geocoder's own record of what it was asked is what makes the
/// second one assertable at all.
/// </para>
/// </summary>
public class PlaceSearchTests(PostgresFixture postgres)
{
    private static readonly GeocodedPlace[] Matches =
    [
        new("Київ, вулиця Хрещатик, 1", 50.4472, 30.5222),
        new("Київ, вулиця Хрещатик, 22", 50.4489, 30.5231),
    ];

    [Fact]
    public async Task A_dispatcher_gets_a_match_with_the_point_a_map_click_would_have_set()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Matches = Matches };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using var response = await SearchAsync(client, dispatcher, "Хрещатик", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var places = (await FleetApi.DataAsync(response, cancellationToken)).EnumerateArray().ToArray();

        Assert.Equal(2, places.Length);
        Assert.Equal(Matches[0].Address, places[0].GetProperty("address").GetString());

        // The adapter's shape, not the port's: a match carries the same MapLocation a picker
        // produces, so choosing one and clicking the map write the same field.
        var point = places[0].GetProperty("point");

        Assert.Equal(Matches[0].Latitude, point.GetProperty("latitude").GetDouble());
        Assert.Equal(Matches[0].Longitude, point.GetProperty("longitude").GetDouble());

        // Trimmed on the way out, so a query typed with a trailing space is the same query.
        Assert.Equal(new[] { "Хрещатик" }, geocoder.Searched);
    }

    [Fact]
    public async Task An_administrator_gets_matches_for_the_reason_they_get_everything_else()
    {
        // AD-4: the guard says RequireRole(Dispatcher) and an admin satisfies every check by rule
        // inside it. Asserted rather than assumed, because the alternative reading - dispatcher and
        // nobody else - is the one a reader of the service would get from the method name alone.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Matches = Matches };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        using var response = await SearchAsync(client, admin, "Хрещатик", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_driver_is_refused_before_the_query_is_even_looked_at()
    {
        // The guard before the validator. The caller sends a query that would pass validation, so
        // the only thing that can refuse them is their role - and nothing reaches the port, which
        // is what stops a public geocoder being spendable by anyone with a session.
        //
        // A driver alone, since story 7.4. The rule the search follows is "may this caller compose
        // a delivery" rather than a role list, and a driver composes nothing: FR-25 makes them a
        // reader of the parcels they carry.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Matches = Matches };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        using (var response = await SearchAsync(client, driver.Token, "Хрещатик", cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }

        Assert.Empty(geocoder.Searched);
    }

    [Fact]
    public async Task A_client_gets_matches_because_their_own_request_form_sets_two_points()
    {
        // The reservation story 7.4 retires, asserted where FR-104 lives. The justification for
        // dispatcher-only was "a client reads deliveries rather than composes them" - which stopped
        // being true the moment /my-deliveries grew a request form with a pickup and a dropoff on
        // it. FR-104 itself is unqualified about who sets a location by typing.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Matches = Matches };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var customer = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        using var response = await SearchAsync(client, customer.Token, "Хрещатик", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var places = (await FleetApi.DataAsync(response, cancellationToken)).EnumerateArray().ToArray();

        Assert.Equal(2, places.Length);
        Assert.Equal(Matches[0].Address, places[0].GetProperty("address").GetString());
    }

    [Fact]
    public async Task A_client_with_no_client_row_is_refused_rather_than_widened()
    {
        // The missing-claim row, which the search inherits from the guard member it now asks. An
        // account holding role Client with no clients row behind it cannot compose anything, and
        // answering "go ahead" on the strength of a missing claim is the failure worth pinning.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Matches = Matches };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var email = FleetApi.UniqueEmail();

        await using (var unitOfWork = await factory.Database.UnitOfWorkFactory
                         .CreateAsync(cancellationToken))
        {
            await unitOfWork.Users.EnsureRoleAsync(UserRole.Client, cancellationToken);

            await unitOfWork.Users.CreateAsync(
                new NewUserAccount("Олена", "Петренко", email),
                FleetApi.Password,
                UserRole.Client,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        var token = await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);

        using (var response = await SearchAsync(client, token, "Хрещатик", cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }

        Assert.Empty(geocoder.Searched);
    }

    [Theory]
    [InlineData("Хр")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_query_under_three_characters_is_refused_before_anything_leaves_the_process(
        string query)
    {
        // AD-9's explicit validation, and the reason the floor exists at all: without it a search
        // box wired to `oninput` would be one outbound request per keystroke, which is both wasteful
        // and the sort of traffic a public geocoder blocks outright.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Matches = Matches };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using (var response = await SearchAsync(client, dispatcher, query, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }

        Assert.Empty(geocoder.Searched);
    }

    [Fact]
    public async Task An_omitted_query_is_refused_like_a_blank_one()
    {
        // The wire can leave the parameter out entirely, and the record has to carry that absence as
        // far as the refusal rather than binding it to something the validator would accept.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Matches = Matches };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using var response = await FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            "/api/deliveries/places",
            dispatcher,
            body: null,
            cancellationToken);

        await FleetApi.AssertFailureAsync(
            response,
            HttpStatusCode.UnprocessableEntity,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);

        Assert.Empty(geocoder.Searched);
    }

    private static Task<HttpResponseMessage> SearchAsync(
        HttpClient client,
        string? token,
        string query,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            "/api/deliveries/places?query=" + Uri.EscapeDataString(query),
            token,
            body: null,
            cancellationToken);
}
