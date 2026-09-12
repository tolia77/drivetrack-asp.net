using System.Net.Http;
using System.Text.Json;
using DriveTrack.Application.Reviews;
using DriveTrack.Domain.Identity;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Reviews;

/// <summary>
/// AD-17 over the review payloads: what an author is shown, and what they are not told.
/// <para>
/// The claim the type system already makes is that <see cref="AuthoredReviewSummary"/> has no party
/// field. This asserts the same thing over the wire, because that is the level at which it can
/// fail: a later story that reused <see cref="ReviewSummary"/> for the author's list — the obvious
/// economy, since it carries strictly more — would compile, pass every rule test, and ship the
/// driver's name to the client whose delivery they carried (FR-27).
/// </para>
/// <para>
/// So the body is read as text and searched for names that must not be in it, and then as a
/// document and searched for property names that must not exist. The second half is the stronger
/// claim: a <c>driverId</c> with no name beside it still lets a client correlate two reviews onto
/// one driver.
/// </para>
/// </summary>
public class ReviewDisclosureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_author_reading_their_own_reviews_learns_nothing_about_either_party()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 5, cancellationToken);

        using var response = await ReviewApi.ListMineAsync(
            client, carried.Client.Token, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // The driver who carried it. DeliveryApi names every driver it creates the same way, so
        // this is the name that would appear if the wrong summary type were ever mapped here.
        Assert.DoesNotContain("Шевченко", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Тарас", body, StringComparison.Ordinal);

        // And the author's own name, which is not withheld for privacy but because the payload has
        // no field for it: a party on a summary is a party a mapping can fill in.
        Assert.DoesNotContain("Петренко", body, StringComparison.Ordinal);

        var row = Assert.Single(await ReviewApi.RowsAsync(response, cancellationToken));

        // The stronger half: no key a party could be written into, named or not. A driverId alone
        // would let a client correlate two reviews onto one driver without ever being told a name.
        foreach (var forbidden in new[] { "driver", "driverId", "client", "clientId" })
        {
            Assert.False(
                row.TryGetProperty(forbidden, out _),
                "The author's own review carried a '" + forbidden + "' property: " + row.ToString());
        }

        // And what it does carry, so the assertions above are not passing on an empty object.
        Assert.True(row.TryGetProperty("id", out _));
        Assert.True(row.TryGetProperty("deliveryId", out _));
        Assert.True(row.TryGetProperty("rating", out _));
        Assert.True(row.TryGetProperty("band", out _));
        Assert.True(row.TryGetProperty("text", out _));
        Assert.True(row.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task The_payload_an_author_gets_back_from_a_write_carries_no_party_either()
    {
        // The route the type-level claim is easiest to lose on. A create and an edit both answer
        // the author directly, so a summary with parties on it here would reach a client without
        // anybody having to list anything - and an edit answers an administrator too, which is
        // exactly the argument for giving both the party-free shape rather than branching on who
        // asked.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        using var written = await ReviewApi.WriteAsync(
            client, carried.Client.Token, carried.DeliveryId, 4, "добре", cancellationToken);

        await AssertNoPartyAsync(written, cancellationToken);

        var reviewId = (await FleetApi.DataAsync(written, cancellationToken))
            .GetProperty("id")
            .GetInt32();

        using var edited = await ReviewApi.EditAsync(
            client, carried.Client.Token, reviewId, 5, "ще краще", cancellationToken);

        await AssertNoPartyAsync(edited, cancellationToken);
    }

    [Fact]
    public async Task The_moderation_list_does_name_both_parties()
    {
        // The other side of AD-17, and the reason the two types are two types rather than one that
        // is sometimes filled in. Without this the disclosure assertions above would pass equally
        // well against a system that had simply stopped resolving parties anywhere.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 5, cancellationToken);

        var row = Assert.Single(await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken));

        Assert.Equal("Тарас Шевченко", row.GetProperty("driver").GetProperty("name").GetString());
        Assert.Equal("Олена Петренко", row.GetProperty("client").GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_authors_page_is_scoped_in_the_query_rather_than_filtered_after_it()
    {
        // AD-3's rule on the review lists, and the defect it exists to make unrepresentable.
        // A hundred and twenty reviews belong to a stranger and three to this author; a page
        // fetched unscoped and narrowed afterwards answers an empty first page, and no amount of
        // paging further finds them. Two rows would never reach this: it takes more of somebody
        // else's than one page holds.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var author = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var mine = new List<int>();

        // Seeded through the context rather than over HTTP: writing a review means carrying a
        // parcel end to end, and a hundred and twenty of those would make this a test of the
        // endpoint's throughput. What is under test is where the WHERE goes.
        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            var stranger = (await Seed.ClientAsync(context, cancellationToken)).Id;

            await SeedReviewsAsync(context, stranger, 120, cancellationToken);
            mine.AddRange(await SeedReviewsAsync(
                context, new ClientId(author.ClientId), 3, cancellationToken));
        }

        var first = await IdsAsync(
            await ReviewApi.ListMineAsync(client, author.Token, cancellationToken, "?offset=0&limit=2"),
            cancellationToken);

        // The author's own first two, rather than nothing at all.
        Assert.Equal(mine.Take(2), first);

        // And the offset counts the author's rows, not everybody's: a second page of one row, not
        // an empty one and not somebody else's.
        var second = await IdsAsync(
            await ReviewApi.ListMineAsync(client, author.Token, cancellationToken, "?offset=2&limit=2"),
            cancellationToken);

        Assert.Equal(mine.Skip(2), second);

        // The moderation list pages over the same repository read, and its offset has to reach the
        // database too: the author's three rows are the hundred and twenty-first onward, so a route
        // that ignored the offset would answer the stranger's first rows here.
        var moderated = await IdsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken, "?offset=120&limit=100"),
            cancellationToken);

        Assert.Equal(mine, moderated);
    }

    /// <summary>
    /// Saves <paramref name="count"/> reviews written by one client, each on a delivery of its own
    /// (DR-6), and answers their row ids in the order they were written.
    /// </summary>
    private static async Task<IReadOnlyList<int>> SeedReviewsAsync(
        AppDbContext context,
        ClientId clientId,
        int count,
        CancellationToken cancellationToken)
    {
        var deliveries = Enumerable
            .Range(0, count)
            .Select(_ => Seed.NewDelivery(clientId: clientId))
            .ToArray();

        context.Deliveries.AddRange(deliveries);

        // One round trip for the whole batch: the ids the reviews need are assigned by this save.
        await context.SaveChangesAsync(cancellationToken);

        var reviews = deliveries
            .Select(delivery => Seed.NewReview(delivery.Id, clientId))
            .ToArray();

        context.Reviews.AddRange(reviews);
        await context.SaveChangesAsync(cancellationToken);

        return [.. reviews.Select(review => review.Id)];
    }

    private static async Task<int[]> IdsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            return
            [
                .. (await ReviewApi.RowsAsync(response, cancellationToken))
                    .Select(row => row.GetProperty("id").GetInt32()),
            ];
        }
    }

    private static async Task AssertNoPartyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await FleetApi.DataAsync(response, cancellationToken);

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);

        foreach (var forbidden in new[] { "driver", "driverId", "client", "clientId" })
        {
            Assert.False(
                payload.TryGetProperty(forbidden, out _),
                "A review answered to its author carried a '" + forbidden + "' property: "
                    + payload.ToString());
        }
    }
}
