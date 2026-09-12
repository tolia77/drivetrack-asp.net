using System.Net;
using DriveTrack.Application.Common;
using DriveTrack.Application.Reviews;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Reviews;

/// <summary>
/// Story 7.2's I/O matrix over HTTP, against the real <c>Program.cs</c> pipeline with the real JWT
/// scheme (FR-62 to FR-67).
/// <para>
/// These claims are only true over the wire and against the schema. "An administrator may moderate
/// a review and may not write one" is a property of the guard reached through the whole adapter,
/// and "two authors racing for one delivery produce exactly one row" is a property of
/// <c>ix_reviews_delivery_id</c> that nothing in C# can assert — which is the whole point of DR-6:
/// the original enforced it with an <c>if</c>, and an <c>if</c> is what concurrency defeats.
/// </para>
/// </summary>
public class ReviewTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_client_reviews_their_own_finished_delivery_and_it_is_stored_against_it()
    {
        // The first row of the matrix, end to end: the review is persisted with the caller's client
        // id - which is the guard's answer and never a field of the payload - and with the injected
        // clock's instant (AD-13).
        var cancellationToken = TestContext.Current.CancellationToken;

        // Truncated to the microsecond PostgreSQL actually stores, so the assertion is about the
        // clock rather than about timestamptz precision.
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedTimeProvider(new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero));

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, clock: clock);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        using var written = await ReviewApi.WriteAsync(
            client, carried.Client.Token, carried.DeliveryId, 4, "водій був чемний", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var created = await FleetApi.DataAsync(written, cancellationToken);

        Assert.Equal(carried.DeliveryId, created.GetProperty("deliveryId").GetInt32());
        Assert.Equal(4, created.GetProperty("rating").GetInt32());

        // FR-98: the band travels with the number, decided by RatingScale in Domain rather than by
        // a class name a screen picked.
        Assert.Equal(nameof(RatingBand.Favourable), created.GetProperty("band").GetString());

        // Read back rather than trusted from the write: the instant has been through the column.
        var mine = await ReviewApi.RowsAsync(
            await ReviewApi.ListMineAsync(client, carried.Client.Token, cancellationToken),
            cancellationToken);

        var row = Assert.Single(mine);

        Assert.Equal(carried.DeliveryId, row.GetProperty("deliveryId").GetInt32());
        Assert.Equal("водій був чемний", row.GetProperty("text").GetString());
        Assert.Equal(clock.Now, row.GetProperty("createdAt").GetDateTimeOffset());

        // And it is on the dispatch list too, with both parties named (FR-64).
        var all = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        var moderated = Assert.Single(all);

        Assert.Equal(carried.Client.ClientId, moderated.GetProperty("client").GetProperty("id").GetInt32());
        Assert.Equal(carried.Driver.DriverId, moderated.GetProperty("driver").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task A_failed_delivery_can_be_reviewed_too()
    {
        // FR-62 names two end states, not one. A parcel that never arrived is exactly the delivery
        // a client has something to say about, so refusing a review here would refuse the case the
        // requirement is most about.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(
            client, dispatcher, cancellationToken, DeliveryStatus.Failed);

        using var written = await ReviewApi.WriteAsync(
            client, carried.Client.Token, carried.DeliveryId, 1, "нічого не привезли", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var created = await FleetApi.DataAsync(written, cancellationToken);

        Assert.Equal(nameof(RatingBand.Unfavourable), created.GetProperty("band").GetString());
    }

    [Fact]
    public async Task Two_authors_racing_for_one_delivery_leave_exactly_one_row_and_one_409()
    {
        // DR-6, AD-20, and the row of the matrix that is the reason the rule is a unique index
        // rather than a lookup. Both requests are in flight before either has committed, so a
        // pre-insert "does a review exist" check would pass in both - which is precisely how the
        // original system ended up with two reviews on one delivery.
        //
        // There is no prior concurrency test in this suite to copy, so this is the shape: fire
        // both, sort the answers, and assert the pair rather than which one won. Which request
        // wins is the database's business and is not a property worth pinning.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        var first = ReviewApi.WriteAsync(
            client, carried.Client.Token, carried.DeliveryId, 5, "перший", cancellationToken);
        var second = ReviewApi.WriteAsync(
            client, carried.Client.Token, carried.DeliveryId, 1, "другий", cancellationToken);

        var responses = await Task.WhenAll(first, second);

        try
        {
            var statuses = responses.Select(response => response.StatusCode).Order().ToArray();

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);

            // NFR-3: the loser is told the contract's code, and nothing of the index that produced
            // it - no SQLSTATE, no constraint name.
            var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);

            await FleetApi.AssertFailureAsync(
                loser,
                HttpStatusCode.Conflict,
                ErrorCode.PERSISTENCE_UNIQUE_VIOLATION,
                cancellationToken);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        // Exactly one row, which is the half the status codes cannot prove: a 409 answered beside a
        // second stored review would satisfy every assertion above.
        var all = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Single(all);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.InTransit)]
    public async Task A_delivery_that_has_not_been_carried_cannot_be_reviewed(DeliveryStatus status)
    {
        // FR-62. A verdict on a journey that has not happened yet, refused with a code of its own
        // rather than a generic one: the caller's next move is to pick a different delivery, and a
        // COMMON_VALIDATION_FAILED naming a field would not say that.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken, status);

        using var refused = await ReviewApi.WriteAsync(
            client, carried.Client.Token, carried.DeliveryId, 5, "зарано", cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.UnprocessableEntity,
            ErrorCode.REVIEW_DELIVERY_NOT_COMPLETED,
            cancellationToken);

        // Nothing written, which is what "refused" has to mean.
        var all = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Empty(all);
    }

    [Fact]
    public async Task A_client_reviewing_somebody_elses_delivery_is_told_it_does_not_exist()
    {
        // 404 and not 403: a client who is told "forbidden" has learned that delivery 7 exists and
        // is somebody's. The scoped read answers the same "not found" for a row that is missing and
        // for one that is merely not theirs, so the non-disclosure falls out of the WHERE clause
        // rather than having to be remembered here (AD-3, FR-27).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);
        var stranger = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        using var refused = await ReviewApi.WriteAsync(
            client, stranger.Token, carried.DeliveryId, 1, "не моє", cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.NotFound,
            ErrorCode.COMMON_NOT_FOUND,
            cancellationToken);

        // And a delivery that really does not exist answers the same thing, which is what makes the
        // answer above non-disclosing rather than merely polite.
        using var missing = await ReviewApi.WriteAsync(
            client, stranger.Token, 999_999, 1, "не існує", cancellationToken);

        await FleetApi.AssertFailureAsync(
            missing,
            HttpStatusCode.NotFound,
            ErrorCode.COMMON_NOT_FOUND,
            cancellationToken);
    }

    [Fact]
    public async Task Neither_an_administrator_nor_a_dispatcher_may_author_a_review()
    {
        // The PRD retired FR-97, and this is the assertion that says so. It is the second place
        // AD-4's admin override deliberately stops: an administrator authoring customer feedback is
        // manufacturing it rather than moderating it - and moderation, which they do have, is
        // asserted below.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        foreach (var token in new[] { admin, dispatcher })
        {
            using var refused = await ReviewApi.WriteAsync(
                client, token, carried.DeliveryId, 5, "не мій відгук", cancellationToken);

            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }
    }

    [Fact]
    public async Task A_driver_is_refused_on_every_review_route()
    {
        // The party a review judges, refused everywhere rather than shown a read-only view. Stated
        // route by route because the guard answers it twice - RequireReviewAuthor on the two the
        // author uses, RequireRole on the collection, RequireReviewOwner on the two writes - and a
        // single spot check would prove only one of them.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);
        var reviewId = await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 4, cancellationToken);

        var driver = carried.Driver.Token;

        var attempts = new[]
        {
            await ReviewApi.ListAsync(client, driver, cancellationToken),
            await ReviewApi.ListMineAsync(client, driver, cancellationToken),
            await ReviewApi.WriteAsync(client, driver, carried.DeliveryId, 5, "я був чемний", cancellationToken),
            await ReviewApi.EditAsync(client, driver, reviewId, 5, "виправлено", cancellationToken),
            await ReviewApi.DeleteAsync(client, driver, reviewId, cancellationToken),
        };

        try
        {
            foreach (var attempt in attempts)
            {
                await FleetApi.AssertFailureAsync(
                    attempt, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
            }
        }
        finally
        {
            foreach (var attempt in attempts)
            {
                attempt.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_client_is_refused_the_moderation_list_rather_than_shown_their_own_rows()
    {
        // FR-64 is a dispatcher's and an administrator's, and the refusal is the half that is easy
        // to get wrong: narrowing /api/reviews to the caller's own rows would look right on a
        // client's screen and would quietly make the route mean two different things. The author's
        // own list is a separate route with a separate type, so this one refuses.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 5, cancellationToken);

        using var refused = await ReviewApi.ListAsync(client, carried.Client.Token, cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);

        // Nothing disclosed with the refusal: not the review they themselves wrote, and not the
        // parties the moderation shape would have named.
        var body = await refused.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("deliveryId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_before_anything_is_read()
    {
        // 401 and not 403, and FR-13 branches on exactly this code: a caller with no credentials is
        // not one who lacks permission.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var listed = await ReviewApi.ListAsync(client, token: null, cancellationToken);
        using var written = await ReviewApi.WriteAsync(
            client, token: null, 1, 5, "анонім", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, written.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task A_rating_off_the_scale_is_refused_as_a_field_error(int rating)
    {
        // NFR-4: the refusal names the field the caller *sent* - camelCase, so a form can attach
        // the message to the control that produced it - and the message is the catalogue's sentence
        // for REVIEW_RATING_OUT_OF_RANGE rather than the generic one, which is what tells "off the
        // scale" from "missing text" on a screen that only ever shows one of them.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        using var refused = await ReviewApi.WriteAsync(
            client, carried.Client.Token, carried.DeliveryId, rating, "текст", cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.UnprocessableEntity,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);

        Assert.Equal(
            Localized(factory, nameof(ErrorCode.REVIEW_RATING_OUT_OF_RANGE)),
            await FieldMessageAsync(refused, "rating", cancellationToken));
    }

    [Fact]
    public async Task An_empty_or_oversized_comment_is_refused_before_the_column_sees_it()
    {
        // The validator's ceiling is the column's, which is what turns a truncation PostgreSQL
        // would raise into a 422 naming the field (NFR-2, NFR-4).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        using var empty = await ReviewApi.WriteAsync(
            client, carried.Client.Token, carried.DeliveryId, 5, string.Empty, cancellationToken);

        using var oversized = await ReviewApi.WriteAsync(
            client,
            carried.Client.Token,
            carried.DeliveryId,
            5,
            new string('я', 2_001),
            cancellationToken);

        await FleetApi.AssertFailureAsync(
            empty, HttpStatusCode.UnprocessableEntity, ErrorCode.COMMON_VALIDATION_FAILED, cancellationToken);
        await FleetApi.AssertFailureAsync(
            oversized, HttpStatusCode.UnprocessableEntity, ErrorCode.COMMON_VALIDATION_FAILED, cancellationToken);

        // Two different sentences, not one generic refusal: an author told "too long" knows to cut
        // and an author told "required" knows to write something.
        Assert.Equal(
            Localized(factory, nameof(ErrorCode.REVIEW_TEXT_REQUIRED)),
            await FieldMessageAsync(empty, "text", cancellationToken));
        Assert.Equal(
            Localized(factory, nameof(ErrorCode.REVIEW_TEXT_TOO_LONG)),
            await FieldMessageAsync(oversized, "text", cancellationToken));

        var all = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Empty(all);
    }

    [Fact]
    public async Task An_edit_is_judged_by_the_rules_that_judged_the_write()
    {
        // AD-23's merged state has to be judged, not merely built. Without this the state validator
        // could be deleted and the suite would stay green while a moderator emptied a review's text
        // or drove 2001 characters at a column that holds 2000 - which PostgreSQL answers as a 500,
        // not as the 422 naming the field the create path gives for exactly the same content.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        var reviewId = await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 5, cancellationToken);

        using var offScale = await ReviewApi.EditAsync(
            client, carried.Client.Token, reviewId, 6, "текст", cancellationToken);

        using var empty = await ReviewApi.EditAsync(
            client, carried.Client.Token, reviewId, 4, string.Empty, cancellationToken);

        using var oversized = await ReviewApi.EditAsync(
            client, carried.Client.Token, reviewId, 4, new string('я', 2_001), cancellationToken);

        foreach (var refused in new[] { offScale, empty, oversized })
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }

        Assert.Equal(
            Localized(factory, nameof(ErrorCode.REVIEW_RATING_OUT_OF_RANGE)),
            await FieldMessageAsync(offScale, "rating", cancellationToken));
        Assert.Equal(
            Localized(factory, nameof(ErrorCode.REVIEW_TEXT_REQUIRED)),
            await FieldMessageAsync(empty, "text", cancellationToken));
        Assert.Equal(
            Localized(factory, nameof(ErrorCode.REVIEW_TEXT_TOO_LONG)),
            await FieldMessageAsync(oversized, "text", cancellationToken));

        // And none of the three landed: the stored row is still the one that was written.
        var row = Assert.Single(await ReviewApi.RowsAsync(
            await ReviewApi.ListMineAsync(client, carried.Client.Token, cancellationToken),
            cancellationToken));

        Assert.Equal(5, row.GetProperty("rating").GetInt32());
        Assert.Equal("усе добре", row.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?offset=-1")]
    public async Task A_page_outside_the_bounds_is_refused_before_any_query(string query)
    {
        // NFR-27 on both review routes: validated rather than passed through, so ?limit=2000000 is
        // a refusal and not an unbounded read. A limit of zero is refused too - it is a malformed
        // request, not a request for nothing - which is why the controller's parameters are
        // nullable and only an absent one means "the default page".
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var author = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        using (var refused = await ReviewApi.ListAsync(client, dispatcher, cancellationToken, query))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }

        // The author's list validates the same query with the same validator, and would lose it
        // with the same line: two routes, two calls, one rule.
        using (var refused = await ReviewApi.ListMineAsync(client, author.Token, cancellationToken, query))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }
    }

    [Fact]
    public async Task An_author_edits_and_deletes_their_own_review_and_nobody_elses()
    {
        // FR-65 and FR-66 from the author's side, and the refusal that pairs with them: a second
        // client is not "a client" for this row, which is the distinction RequireReviewOwner makes
        // and RequireRole could not.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);
        var stranger = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var reviewId = await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 2, cancellationToken);

        using (var refused = await ReviewApi.EditAsync(
                   client, stranger.Token, reviewId, 5, "не мій", cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var edited = await ReviewApi.EditAsync(
                   client, carried.Client.Token, reviewId, 5, "передумав", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

            var updated = await FleetApi.DataAsync(edited, cancellationToken);

            Assert.Equal(5, updated.GetProperty("rating").GetInt32());
            Assert.Equal("передумав", updated.GetProperty("text").GetString());
            Assert.Equal(nameof(RatingBand.Favourable), updated.GetProperty("band").GetString());
        }

        using (var refused = await ReviewApi.DeleteAsync(
                   client, stranger.Token, reviewId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var deleted = await ReviewApi.DeleteAsync(
                   client, carried.Client.Token, reviewId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var all = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Empty(all);
    }

    [Fact]
    public async Task A_review_that_is_not_there_refuses_a_client_before_it_answers_an_administrator()
    {
        // AD-3's ordering, pinned as the two different answers it produces. UpdateAsync and
        // DeleteAsync call the guard with the loaded row's owner - a null for a row that is not
        // there - and only then answer 404. Swapping the two statements would ship green against
        // every other test in this class and would let any client walk the id space asking which
        // reviews exist: a 403 where a 404 came back is "somebody else wrote that one".
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var stranger = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        const int Missing = 999_999;

        using (var edit = await ReviewApi.EditAsync(
                   client, stranger.Token, Missing, 5, "нема такого", cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                edit, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var delete = await ReviewApi.DeleteAsync(
                   client, stranger.Token, Missing, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                delete, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        // An administrator passes the guard by rule (AD-4), so for them the next question really is
        // whether the row exists - and the honest answer is that it does not.
        using (var edit = await ReviewApi.EditAsync(
                   client, admin, Missing, 5, "нема такого", cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                edit, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        using (var delete = await ReviewApi.DeleteAsync(client, admin, Missing, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                delete, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }
    }

    [Fact]
    public async Task A_dispatcher_reads_every_review_and_may_change_none_of_them()
    {
        // FR-64 against FR-65: the one role whose answer differs between reading and writing, which
        // is why the collection's guard and the row's guard are different members.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        var reviewId = await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 3, cancellationToken);

        var all = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Single(all);

        using var edit = await ReviewApi.EditAsync(
            client, dispatcher, reviewId, 5, "виправлено", cancellationToken);
        using var delete = await ReviewApi.DeleteAsync(client, dispatcher, reviewId, cancellationToken);

        await FleetApi.AssertFailureAsync(
            edit, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        await FleetApi.AssertFailureAsync(
            delete, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);

        // A dispatcher has no reviews of their own either: the author's list is a client's, and the
        // guard refuses them rather than answering an empty page that would read as "you have none".
        using var mine = await ReviewApi.ListMineAsync(client, dispatcher, cancellationToken);

        await FleetApi.AssertFailureAsync(
            mine, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    [Fact]
    public async Task An_administrator_moderates_any_review_they_did_not_write()
    {
        // AD-4 as it applies to the half of the pair it does apply to. The same caller refused in
        // Neither_an_administrator_nor_a_dispatcher_may_author_a_review edits and deletes here, and
        // the two tests together are the whole of the product decision.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        var reviewId = await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 1, cancellationToken);

        using (var edited = await ReviewApi.EditAsync(
                   client, admin, reviewId, 3, "відредаговано модератором", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

            var updated = await FleetApi.DataAsync(edited, cancellationToken);

            Assert.Equal(3, updated.GetProperty("rating").GetInt32());
            Assert.Equal(nameof(RatingBand.Neutral), updated.GetProperty("band").GetString());
        }

        using (var deleted = await ReviewApi.DeleteAsync(client, admin, reviewId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var all = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, admin, cancellationToken),
            cancellationToken);

        Assert.Empty(all);
    }

    [Fact]
    public async Task An_author_reads_only_the_reviews_they_wrote()
    {
        // FR-63, and AD-3's reason for narrowing in SQL rather than filtering a page: the scope is a
        // WHERE, so a second client's rows are not fetched and then removed - they were never in
        // the answer.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var ours = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);
        var theirs = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        await ReviewApi.WrittenAsync(client, ours.Client.Token, ours.DeliveryId, 5, cancellationToken);
        await ReviewApi.WrittenAsync(client, theirs.Client.Token, theirs.DeliveryId, 1, cancellationToken);

        var mine = await ReviewApi.RowsAsync(
            await ReviewApi.ListMineAsync(client, ours.Client.Token, cancellationToken),
            cancellationToken);

        var row = Assert.Single(mine);

        Assert.Equal(ours.DeliveryId, row.GetProperty("deliveryId").GetInt32());

        // And the moderation list sees both, which is what makes the narrowing above a narrowing
        // rather than an empty database.
        var all = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Equal(2, all.Length);
    }

    [Fact]
    public async Task The_list_answers_every_review_newest_first()
    {
        // The twin of the delivery board's ordering test, and the reason it is not merely a
        // restatement of the reversed seed order: the middle review is edited after all three are
        // written. PostgreSQL answers an UPDATE by writing a new row version rather than by changing
        // the old one in place, so a read with no ORDER BY hands the edited row back last - and
        // deleting the OrderByDescending would show up here as 1, 3, 2 instead of 3, 2, 1.
        //
        // Newest first, and that is the fix rather than a preference: nobody pages past the first
        // hundred, so an ascending order put the reviews just written on a page a moderator never
        // asked for.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        var written = new List<int>();

        for (var index = 0; index < 3; index++)
        {
            var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

            written.Add(await ReviewApi.WrittenAsync(
                client, carried.Client.Token, carried.DeliveryId, 4, cancellationToken));
        }

        using (var edited = await ReviewApi.EditAsync(
                   client, admin, written[1], 2, "переглянуто", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        var ids = (await ReviewApi.RowsAsync(
                await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
                cancellationToken))
            .Select(row => row.GetProperty("id").GetInt32())
            .ToArray();

        Assert.Equal(Enumerable.Reverse(written), ids);

        // The author's own list reads through the same repository method, so it is ordered by the
        // same line - and would lose its ordering with it.
        var carriedAgain = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);
        var second = await ReviewApi.DeliveryAsync(
            client, dispatcher, cancellationToken, existing: carriedAgain.Client);

        var mine = new[]
        {
            await ReviewApi.WrittenAsync(
                client, carriedAgain.Client.Token, carriedAgain.DeliveryId, 5, cancellationToken),
            await ReviewApi.WrittenAsync(
                client, second.Client.Token, second.DeliveryId, 3, cancellationToken),
        };

        using (var edited = await ReviewApi.EditAsync(
                   client, carriedAgain.Client.Token, mine[0], 1, "передумав", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        var ownIds = (await ReviewApi.RowsAsync(
                await ReviewApi.ListMineAsync(client, carriedAgain.Client.Token, cancellationToken),
                cancellationToken))
            .Select(row => row.GetProperty("id").GetInt32())
            .ToArray();

        Assert.Equal(Enumerable.Reverse(mine), ownIds);
    }

    [Fact]
    public async Task A_review_written_past_the_first_page_is_still_the_first_row_a_moderator_sees()
    {
        // DW-46 at the surface that matters. The ordering test above orders three rows, so it would
        // pass on a list of any size; this one crosses the default page boundary, which is where the
        // defect actually lived: nobody pages past the first page, so with an ascending order the
        // newest review sat on a page no moderator would ever ask for. One more than a full page is
        // the smallest seed that can tell the two orders apart.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var written = new List<int>();

        // Seeded through the context rather than over HTTP: writing a review means carrying a parcel
        // end to end, and a page of those would make this a test of the endpoint's throughput.
        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            var author = (await Seed.ClientAsync(context, cancellationToken)).Id;

            written.AddRange(await ReviewDisclosureTests.SeedReviewsAsync(
                context, author, ListReviewsQueryValidator.MaximumLimit + 1, cancellationToken));
        }

        // No query string: the moderation list's own default page, which is the only page anybody
        // fetches.
        var ids = (await ReviewApi.RowsAsync(
                await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
                cancellationToken))
            .Select(row => row.GetProperty("id").GetInt32())
            .ToArray();

        // Exactly one page, and the review written last is the row at the top of it. The one left
        // over is the oldest, which is the row that may fall off.
        Assert.Equal(ListReviewsQueryValidator.MaximumLimit, ids.Length);
        Assert.Equal(written[^1], ids[0]);
        Assert.DoesNotContain(written[0], ids);
    }

    /// <summary>
    /// The single message the envelope attached to one field. Asserted as a sentence rather than as
    /// a key, because the key is what the validator says and the sentence is what a user reads —
    /// NFR-3 puts exactly one localized message on the wire and nothing else.
    /// </summary>
    private static async Task<string> FieldMessageAsync(
        HttpResponseMessage response,
        string field,
        CancellationToken cancellationToken)
    {
        var envelope = await FleetApi.ReadAsync(response, cancellationToken);
        var fields = envelope.GetProperty("error").GetProperty("fields");

        Assert.True(
            fields.TryGetProperty(field, out var messages),
            "The envelope named no field '" + field + "': " + fields.ToString());

        return Assert.Single(messages.EnumerateArray()).GetString()!;
    }

    /// <summary>
    /// The catalogue's sentence for a code, read through the host's own localizer — so this asserts
    /// the wiring rather than restating the Ukrainian, which would pass after somebody deleted the
    /// resource key.
    /// </summary>
    private static string Localized(ApiFactory factory, string key)
    {
        using var scope = factory.Services.CreateScope();

        return scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Web.Resources.ErrorMessages>>()[key];
    }
}
