using System.Diagnostics;
using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Geocoding;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    /// <summary>Everything the adapters in this suite logged, for the outcomes only a log shows.</summary>
    private readonly RecordingLogger _logs = new();

    private const string ValidConnectionString =
        "Host=localhost;Port=5432;Database=drivetrack;Username=drivetrack;Password=irrelevant";

    /// <summary>The agent the adapter is configured with, so the request can be checked for it.</summary>
    private const string UserAgent = "DriveTrack-Tests/1.0";

    /// <summary>
    /// The pacing interval every test here runs with, in milliseconds. A deployment's default is a
    /// second, which is Nominatim's policy; a suite that waited a real second per pair of lookups
    /// would be asserting the arithmetic of <see cref="Task.Delay(TimeSpan)"/> at the price of the
    /// slowest tests in the solution. The gate is the same gate either way.
    /// </summary>
    private const int PacingInterval = 400;

    /// <summary>
    /// The adapter's logging category. A literal rather than <c>nameof</c>: the adapter is internal
    /// to Infrastructure, which is right — nothing outside its own assembly should be naming the
    /// implementation type — and a test reads a category as a string in any case.
    /// </summary>
    private const string GeocoderCategory = "NominatimGeocoder";

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

    [Fact]
    public async Task Two_lookups_through_one_adapter_start_at_least_an_interval_apart()
    {
        // Nominatim's usage policy is one request per second, and one delivery create queues two
        // reverse lookups back to back. Nothing above the adapter can space them: the worker cannot
        // see the dispatcher's forward searches, which draw on the same quota. So the gate is here,
        // and what it promises is about the *starts* of two requests, not about how long either
        // takes to answer.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """{"display_name":"Хрещатик, 1, Київ"}"""));

        var geocoder = Resolve(server);

        var elapsed = Stopwatch.StartNew();
        var first = Stopwatch.StartNew();

        await geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken);

        first.Stop();

        await geocoder.DescribeAsync(50.4502, 30.5235, cancellationToken);

        elapsed.Stop();

        Assert.Equal(2, server.Requests.Count);

        // A shade under the interval rather than the interval exactly: a timer fires at or after
        // its due time, but Stopwatch and the timer wheel are not the same clock and Windows rounds
        // the wheel to about 15ms. The claim being tested is "waited", and a first lookup that
        // slipped through unpaced would take a millisecond or two, nowhere near this floor.
        Assert.True(
            elapsed.ElapsedMilliseconds >= PacingInterval - 50,
            $"The second lookup started after {elapsed.ElapsedMilliseconds}ms, "
                + $"inside the {PacingInterval}ms pacing interval.");

        // And the first one paid nothing. A gate that waited out an interval before the very first
        // request would be a gate that charged every process start for a quota nobody had spent -
        // and the assertion above cannot see it, since waiting twice is still "at least once".
        Assert.True(
            first.ElapsedMilliseconds < PacingInterval - 50,
            $"The first lookup, with no request before it to be paced against, took "
                + $"{first.ElapsedMilliseconds}ms - long enough that it waited at the gate.");
    }

    [Fact]
    public async Task A_lookup_whose_interval_has_already_passed_is_not_made_to_wait_again()
    {
        // The gate spaces request *starts*; it is not a token that has to be re-earned. A worker
        // that geocodes one delivery a minute is inside the policy without ever waiting, and a gate
        // that made it wait anyway would add a second to every job for nothing.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """{"display_name":"Хрещатик, 1, Київ"}"""));

        var geocoder = Resolve(server, intervalMilliseconds: 200);

        await geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken);

        // Idle for longer than the interval, which is what a real deployment mostly does.
        await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);

        var elapsed = Stopwatch.StartNew();

        await geocoder.DescribeAsync(50.4502, 30.5235, cancellationToken);

        elapsed.Stop();

        Assert.Equal(2, server.Requests.Count);

        Assert.True(
            elapsed.ElapsedMilliseconds < 200,
            $"The second lookup took {elapsed.ElapsedMilliseconds}ms although its interval had "
                + "already passed, so the gate held it anyway.");
    }

    [Fact]
    public async Task Concurrent_lookups_in_both_directions_queue_behind_the_one_gate()
    {
        // The sibling test above awaits its two lookups one at a time, so only ever one caller is
        // at the gate - and a gate with no mutual exclusion would pass it. This is the case that
        // does not: four callers arrive together, each computes the remainder from the same last
        // stamp, and without the semaphore they would agree on it and hit the wire in the same
        // millisecond. Both directions are mixed in because they share one quota and therefore must
        // share one gate: a dispatcher's search is not exempt from the policy a delivery create's
        // reverse lookups are spending.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(request =>
            request.Target.StartsWith("/search", StringComparison.Ordinal)
                ? new LoopbackHttpServer.CannedResponse(
                    200,
                    """[{"display_name":"Хрещатик, 1, Київ","lat":"50.4472","lon":"30.5222"}]""")
                : new LoopbackHttpServer.CannedResponse(
                    200,
                    """{"display_name":"Хрещатик, 1, Київ"}"""));

        var geocoder = Resolve(server);

        var elapsed = Stopwatch.StartNew();

        Task[] lookups =
        [
            geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken),
            geocoder.SearchAsync("Хрещатик", cancellationToken),
            geocoder.DescribeAsync(50.4502, 30.5235, cancellationToken),
            geocoder.SearchAsync("Володимирська", cancellationToken),
        ];

        await Task.WhenAll(lookups);

        elapsed.Stop();

        // Every one of them got there. Giving up at the gate would also be slow, and would also be
        // a failure this test must not mistake for pacing.
        Assert.Equal(lookups.Length, server.Requests.Count);

        // Four requests are three intervals apart, whatever order the gate lets them through in.
        var floor = (PacingInterval * (lookups.Length - 1)) - 50;

        Assert.True(
            elapsed.ElapsedMilliseconds >= floor,
            $"Four concurrent lookups took {elapsed.ElapsedMilliseconds}ms, "
                + $"under the {floor}ms three intervals of pacing come to.");
    }

    [Fact]
    public async Task A_zero_interval_paces_nothing()
    {
        // The interval has to be a deployment's choice: a self-hosted Nominatim carries no usage
        // policy, and there a second of waiting per lookup would be a cost with nothing on the
        // other side of it. Three lookups at the committed default would cost two seconds.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """{"display_name":"Хрещатик, 1, Київ"}"""));

        var geocoder = Resolve(server, intervalMilliseconds: 0);

        var elapsed = Stopwatch.StartNew();

        await geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken);
        await geocoder.DescribeAsync(50.4502, 30.5235, cancellationToken);
        await geocoder.DescribeAsync(50.4503, 30.5236, cancellationToken);

        elapsed.Stop();

        Assert.Equal(3, server.Requests.Count);

        Assert.True(
            elapsed.ElapsedMilliseconds < 1000,
            $"Three unpaced loopback lookups took {elapsed.ElapsedMilliseconds}ms, "
                + "which is long enough that something waited.");
    }

    [Fact]
    public async Task A_lookup_cancelled_while_it_waits_for_its_turn_answers_no_address_and_says_so()
    {
        // DR-11 leaves the caller one answer for every failure, which is right - nothing above the
        // port could act on the difference. But an operator can, and "the gate gave up" and "the
        // provider has no address for those coordinates" are different problems with different
        // fixes. The log is the only place they are distinguishable, so it is asserted.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """{"display_name":"Хрещатик, 1, Київ"}"""));

        // Long enough that the second lookup is certainly still at the gate when the token trips.
        var geocoder = Resolve(server, intervalMilliseconds: 5000);

        await geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken);

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        giveUp.CancelAfter(TimeSpan.FromMilliseconds(50));

        Assert.Null(await geocoder.DescribeAsync(50.4502, 30.5235, giveUp.Token));

        // Never reached the wire, which is the point: a request the caller has abandoned must not
        // spend the provider's quota on the way out.
        Assert.Single(server.Requests);

        var warning = Assert.Single(_logs.Messages(LogLevel.Warning, GeocoderCategory));

        Assert.Contains("pacing gate", warning, StringComparison.Ordinal);
        Assert.Contains("reverse lookup", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_that_would_arrive_after_the_lookup_budget_is_given_up_on()
    {
        // The wait is bounded by the lookup's own timeout, and it has to be: a dispatcher's search
        // shares the quota with a queue of reverse lookups, and without a budget it would sit at
        // the gate behind all of them - well past the deadline the deployment already set for one
        // lookup. Nothing waits for an address (FR-95), so giving up is the correct answer.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            """[{"display_name":"Хрещатик, 1, Київ","lat":"50.4472","lon":"30.5222"}]"""));

        var geocoder = Resolve(server, intervalMilliseconds: 30_000, timeoutSeconds: 1);

        await geocoder.SearchAsync("Хрещатик", cancellationToken);

        var elapsed = Stopwatch.StartNew();

        Assert.Empty(await geocoder.SearchAsync("Володимирська", cancellationToken));

        elapsed.Stop();

        Assert.Single(server.Requests);

        // The budget, not the interval: a gate that waited out its 30 seconds would have made the
        // caller wait thirty times the deadline it was given. The ceiling is a small multiple of
        // the one-second budget rather than a large one - at ten seconds a gate that honoured nine
        // of its thirty would pass, which is the regression this case exists to catch.
        Assert.True(
            elapsed.ElapsedMilliseconds < 3_000,
            $"The gate held the caller for {elapsed.ElapsedMilliseconds}ms against a 1000ms "
                + "budget, so the wait was bounded by the interval rather than by the deadline.");

        var warning = Assert.Single(_logs.Messages(LogLevel.Warning, GeocoderCategory));

        Assert.Contains("pacing gate", warning, StringComparison.Ordinal);
        Assert.Contains("forward search", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lookup_that_waited_at_the_gate_gets_only_the_rest_of_its_budget_on_the_wire()
    {
        // One deadline for the whole lookup, not one for the gate and another for the wire. The two
        // are configured from the same key, so a second budget stays invisible until a lookup both
        // waits and then meets a provider that will not answer - and then it costs the interval
        // plus the timeout instead of the timeout, twice what the deployment asked for, on the path
        // a delivery create's two back-to-back reverse lookups take every time. The gate half of
        // this is covered above; without this case the wire half could be handed the caller's raw
        // token again and nothing here would notice.
        var cancellationToken = TestContext.Current.CancellationToken;

        using var release = new ManualResetEventSlim(false);

        var served = 0;

        await using var server = LoopbackHttpServer.Start(_ =>
        {
            // The first lookup answers at once; the second one hangs, so its wire call can only end
            // at a deadline. Each connection is served on its own task, so this holds nothing else
            // up, and the wait is bounded so a failing assertion cannot leave a thread parked.
            if (Interlocked.Increment(ref served) > 1)
            {
                release.Wait(TimeSpan.FromSeconds(10));
            }

            return new LoopbackHttpServer.CannedResponse(
                200,
                """{"display_name":"Хрещатик, 1, Київ","lat":"50.45","lon":"30.52"}""");
        });

        var geocoder = Resolve(server, intervalMilliseconds: 1000, timeoutSeconds: 2);

        Assert.NotNull(await geocoder.DescribeAsync(50.4501, 30.5234, cancellationToken));

        var elapsed = Stopwatch.StartNew();

        Assert.Null(await geocoder.DescribeAsync(50.4600, 30.5300, cancellationToken));

        elapsed.Stop();

        release.Set();

        // It did reach the wire. Otherwise this would be another gate test wearing a timeout.
        Assert.Equal(2, server.Requests.Count);

        // Two budgets would be 1000ms at the gate and then a fresh 2000ms on the wire. One is the
        // 2000ms the deployment configured, the wait at the gate included in it.
        Assert.True(
            elapsed.ElapsedMilliseconds < 2_600,
            $"The second lookup cost {elapsed.ElapsedMilliseconds}ms against a 2000ms timeout, so "
                + "the wait at the gate was not deducted from the wire call's budget.");
    }

    [Theory]
    [InlineData(429)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task A_provider_refusing_the_deployment_records_the_refusal_and_its_status(int status)
    {
        // The three ways Nominatim says the pacing is wrong for it: 429 to a caller asking too
        // often, 403 to one it has decided is abusive, 503 when it is shedding load. Flattened to a
        // null address each reads exactly like DR-11's legitimate absent one, and a deployment could
        // be refused every lookup it made without anything, anywhere, saying so.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(
            _ => new LoopbackHttpServer.CannedResponse(status, "refused"));

        Assert.Null(await Resolve(server).DescribeAsync(50.4501, 30.5234, cancellationToken));

        var warning = Assert.Single(_logs.Messages(LogLevel.Warning, GeocoderCategory));

        Assert.Contains("refused by the provider", warning, StringComparison.Ordinal);
        Assert.Contains(
            status.ToString(CultureInfo.InvariantCulture),
            warning,
            StringComparison.Ordinal);
        Assert.Contains("reverse lookup", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The registered <see cref="IGeocoder"/>, pointed at the server — through
    /// <c>AddInfrastructure</c>, so the options binding and the client construction are the ones the
    /// application runs rather than a hand-assembled approximation of them.
    /// </summary>
    private IGeocoder Resolve(
        LoopbackHttpServer server,
        int intervalMilliseconds = PacingInterval,
        int timeoutSeconds = 10)
    {
        var values = TestConfiguration.Defaults();
        values["ConnectionStrings:Default"] = ValidConnectionString;
        values["Geocoder:Endpoint"] = server.BaseAddress + "reverse";
        values["Geocoder:SearchEndpoint"] = server.BaseAddress + "search";
        values["Geocoder:UserAgent"] = UserAgent;
        values[GeocoderOptions.TimeoutSecondsConfigurationKey] =
            timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        values[GeocoderOptions.MinimumRequestIntervalMillisecondsConfigurationKey] =
            intervalMilliseconds.ToString(CultureInfo.InvariantCulture);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs).SetMinimumLevel(LogLevel.Debug));
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
