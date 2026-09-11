using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Geocoding;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Configuration;

/// <summary>
/// The real <see cref="IGeocoder"/>, resolved from the container <c>AddInfrastructure</c> builds and
/// pointed at a loopback server serving canned Nominatim JSON.
/// <para>
/// Every other suite in this solution replaces this port with a fake, which is right — a test of
/// FR-95's non-blocking promise has no business depending on somebody else's uptime. But it leaves
/// the adapter itself unexecuted, and the ways it can be wrong are all silent: swap <c>lat</c> and
/// <c>lon</c> in the query string, read <c>name</c> instead of <c>display_name</c>, or drop the
/// <c>User-Agent</c> Nominatim insists on, and the whole solution stays green while a deployment
/// resolves nothing — indistinguishable, from the outside, from DR-11's perfectly legitimate absent
/// address. So the bytes the adapter sends and the bytes it reads back are asserted here.
/// </para>
/// <para>
/// No database and no container: <c>AddInfrastructure</c> needs a connection string to be present,
/// not to be reachable, and nothing here opens one.
/// </para>
/// </summary>
public class GeocoderAdapterTests : IDisposable
{
    /// <summary>
    /// Every container <see cref="Resolve"/> built, so each one's <c>HttpClient</c> is released with
    /// the test rather than left to a finalizer.
    /// </summary>
    private readonly List<ServiceProvider> _providers = [];

    private const string ValidConnectionString =
        "Host=localhost;Port=5432;Database=drivetrack;Username=drivetrack;Password=irrelevant";

    /// <summary>The agent the adapter is configured with, so the request can be checked for it.</summary>
    private const string UserAgent = "DriveTrack-Tests/1.0";

    [Fact]
    public async Task A_reverse_lookup_answers_the_display_name_the_provider_returned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // `name` alongside `display_name`, deliberately: Nominatim sends both, the short one first,
        // and reading the wrong key yields a plausible-looking string that is not an address.
        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """{"name":"1","display_name":"Хрещатик, 1, Київ, Україна","lat":"50.45","lon":"30.52"}"""));

        var geocoder = Resolve(server);

        var address = await geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken);

        Assert.Equal("Хрещатик, 1, Київ, Україна", address);
    }

    [Fact]
    public async Task A_reverse_lookup_sends_the_coordinates_the_right_way_round_and_names_itself()
    {
        // Two failures that cannot be seen from the answer. Swapped coordinates return an address -
        // just somebody else's, half a world away - and a missing agent header is refused by
        // Nominatim's own policy, which is a rule of the protocol rather than a courtesy.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """{"display_name":"Хрещатик, 1, Київ, Україна"}"""));

        var geocoder = Resolve(server);

        await geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken);

        var request = Assert.Single(server.Requests);

        Assert.StartsWith("/reverse?", request.Target, StringComparison.Ordinal);
        Assert.Contains("format=json", request.Target, StringComparison.Ordinal);

        // Invariant, and this is the one place it is load-bearing: a Ukrainian culture writes
        // 50,4501, and a comma is a value separator to every geocoding service there is.
        Assert.Contains("lat=50.4501", request.Target, StringComparison.Ordinal);
        Assert.Contains("lon=30.5234", request.Target, StringComparison.Ordinal);

        Assert.Equal(UserAgent, request.UserAgent);
    }

    [Fact]
    public async Task A_refusal_arriving_as_a_success_answers_no_address()
    {
        // Nominatim reports "I cannot place that" as a 200 carrying an error object, so the status
        // alone is not enough - the shape has to be checked. Read carelessly this is a JSON document
        // with no display_name, and the answer must be an absent address rather than an exception.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """{"error":"Unable to geocode"}"""));

        var geocoder = Resolve(server);

        Assert.Null(await geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken));
    }

    [Fact]
    public async Task A_refusal_and_a_body_that_is_not_json_both_answer_no_address()
    {
        // DR-11 has one answer for every way a lookup can fail, and the port promises the adapter
        // never throws: nothing above it has a caller left to tell.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var refusing = LoopbackHttpServer.Start(
            _ => new LoopbackHttpServer.CannedResponse(429, "rate limited"));

        Assert.Null(await Resolve(refusing).DescribeAsync(50.45, 30.52, cancellationToken));

        await using var babbling = LoopbackHttpServer.Start(
            _ => new LoopbackHttpServer.CannedResponse(200, "<html>not json</html>"));

        Assert.Null(await Resolve(babbling).DescribeAsync(50.45, 30.52, cancellationToken));
        Assert.Empty(await Resolve(babbling).SearchAsync("Хрещатик", cancellationToken));
    }

    [Fact]
    public async Task A_forward_search_reads_coordinates_that_arrive_as_strings()
    {
        // Nominatim writes lat and lon as JSON strings, not numbers. A reader that expected numbers
        // would drop every row of every search and answer an empty list, which reads exactly like a
        // query that matched nothing.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """
            [
              {"display_name":"Хрещатик, 1, Київ","lat":"50.4472","lon":"30.5222"},
              {"display_name":"Хрещатик, 22, Київ","lat":50.4489,"lon":30.5231}
            ]
            """));

        var places = await Resolve(server).SearchAsync("Хрещатик", cancellationToken);

        Assert.Equal(2, places.Count);
        Assert.Equal("Хрещатик, 1, Київ", places[0].Address);
        Assert.Equal(50.4472, places[0].Latitude);
        Assert.Equal(30.5222, places[0].Longitude);

        // A number is read too, so the adapter is not merely tolerant of one shape.
        Assert.Equal(50.4489, places[1].Latitude);

        var request = Assert.Single(server.Requests);

        Assert.StartsWith("/search?", request.Target, StringComparison.Ordinal);
        Assert.Contains("q=" + Uri.EscapeDataString("Хрещатик"), request.Target, StringComparison.Ordinal);
        Assert.Equal(UserAgent, request.UserAgent);
    }

    [Fact]
    public async Task A_row_this_system_could_not_use_is_dropped_rather_than_thrown_on()
    {
        // MapLocation's constructor throws on a coordinate outside the decimal-degree range, and it
        // is right to: everywhere else in the system that is a programming error. This is the one
        // place the numbers come from outside it, so a third party's malformed row is dropped here -
        // otherwise it would surface as a 500 on a dispatcher's search.
        //
        // NaN is the case a range check written as `is < -90 or > 90` waves through, because every
        // comparison against NaN is false, and it is reachable: double.TryParse accepts "NaN".
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """
            [
              {"display_name":"Поза межами","lat":"500","lon":"30.5"},
              {"display_name":"Не число","lat":"NaN","lon":"NaN"},
              {"display_name":"Без координат"},
              {"lat":"50.45","lon":"30.52"},
              {"display_name":"Хрещатик, 1, Київ","lat":"50.4472","lon":"30.5222"}
            ]
            """));

        var places = await Resolve(server).SearchAsync("Хрещатик", cancellationToken);

        var place = Assert.Single(places);

        Assert.Equal("Хрещатик, 1, Київ", place.Address);
    }

    /// <summary>
    /// The registered <see cref="IGeocoder"/>, pointed at the server — through
    /// <c>AddInfrastructure</c>, so the options binding and the client construction are the ones the
    /// application runs rather than a hand-assembled approximation of them.
    /// </summary>
    private IGeocoder Resolve(LoopbackHttpServer server)
    {
        var values = TestConfiguration.Defaults();
        values["ConnectionStrings:Default"] = ValidConnectionString;
        values["Geocoder:Endpoint"] = server.BaseAddress + "reverse";
        values["Geocoder:SearchEndpoint"] = server.BaseAddress + "search";
        values["Geocoder:UserAgent"] = UserAgent;
        values[GeocoderOptions.TimeoutSecondsConfigurationKey] = "10";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new TestHostEnvironment());

        var provider = services.BuildServiceProvider();

        _providers.Add(provider);

        return provider.GetRequiredService<IGeocoder>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _providers.Clear();

        GC.SuppressFinalize(this);
    }
}
