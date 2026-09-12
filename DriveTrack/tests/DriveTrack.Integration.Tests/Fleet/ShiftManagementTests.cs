using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Shifts;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Fleet;

/// <summary>
/// Story 4.2's I/O matrix over HTTP, against the real <c>Program.cs</c> pipeline with the real JWT
/// scheme (FR-109 to FR-117).
/// <para>
/// These claims are only true over the wire and against the schema. "A driver reaches their own
/// shifts and nobody else's" is a property of the guard reached through the whole adapter; "two
/// starts racing for one driver produce exactly one row" is a property of
/// <c>IX_Shifts_DriverId_Open</c> that nothing in C# can assert — the service's friendly
/// <c>SHIFT_ALREADY_OPEN</c> is a lookup, and a lookup is precisely what concurrency defeats.
/// </para>
/// <para>
/// The refusals are checked by code rather than only by status, because two of them are the same
/// 409: being already on duty and losing a race are different sentences a caller acts on
/// differently, and NFR-2 is only interesting because both arrive as 409 while saying different
/// things.
/// </para>
/// </summary>
public class ShiftManagementTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_driver_goes_on_duty_and_off_duty_against_one_row_stamped_by_the_clock()
    {
        // The headline row, end to end: one row, both instants from the injected clock (AD-13), and
        // the row open only between the two presses. A shift whose end opened a second row would
        // satisfy every status assertion here, so the count is checked as well.
        var cancellationToken = TestContext.Current.CancellationToken;

        // Truncated to the microsecond PostgreSQL actually stores, so the assertion is about the
        // clock rather than about timestamptz precision.
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedTimeProvider(new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero));

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, clock: clock);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var started = clock.Now;

        using (var response = await ShiftApi.StartAsync(
                   client, driver.Token, driver.DriverId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var shift = await FleetApi.DataAsync(response, cancellationToken);

            Assert.Equal(driver.DriverId, shift.GetProperty("driverId").GetInt32());
            Assert.Equal(started, shift.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, shift.GetProperty("endedAt").ValueKind);
            Assert.True(shift.GetProperty("isOpen").GetBoolean());
        }

        // Eight hours later, on the same host. The end has to come from the clock too, or a shift's
        // length is whatever the machine running the tests happened to think.
        clock.Now = started.AddHours(8);

        using (var response = await ShiftApi.EndAsync(
                   client, driver.Token, driver.DriverId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var shift = await FleetApi.DataAsync(response, cancellationToken);

            Assert.Equal(started, shift.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(clock.Now, shift.GetProperty("endedAt").GetDateTimeOffset());
            Assert.False(shift.GetProperty("isOpen").GetBoolean());
        }

        // One row, not two: ending a shift closes the row that was opened rather than writing a
        // second one beside it.
        var rows = await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Single(rows);

        // And the driver's name travels with the row, read off the Identity account rather than
        // joined - a screen shows who was on duty, not a row id.
        Assert.False(string.IsNullOrWhiteSpace(rows[0].GetProperty("driverName").GetString()));
    }

    [Fact]
    public async Task A_driver_who_is_already_on_duty_cannot_start_a_second_shift()
    {
        // FR-110's friendly refusal, and the row count is half the claim: a 409 answered beside a
        // second stored shift would satisfy the status assertion on its own.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        await ShiftApi.StartedAsync(client, driver.Token, driver.DriverId, cancellationToken);

        using (var second = await ShiftApi.StartAsync(
                   client, driver.Token, driver.DriverId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                second, HttpStatusCode.Conflict, ErrorCode.SHIFT_ALREADY_OPEN, cancellationToken);
        }

        Assert.Single(await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken));
    }

    [Fact]
    public async Task Two_starts_racing_for_one_driver_leave_exactly_one_row_and_one_409()
    {
        // FR-110, AD-20, and the row of the matrix that is the reason the rule is a partial unique
        // index rather than a lookup. Both requests are in flight before either has committed, so
        // the service's "is there an open shift" read passes in both - which is precisely how a
        // driver ends up on duty twice.
        //
        // Shaped after ReviewTests' race: fire both, sort the answers, and assert the pair rather
        // than which one won. Which request wins is the database's business.
        //
        // The one difference from that race is why the loser's code is a set below rather than a
        // single value. Reviews has no pre-insert lookup at all, so its loser is always the index's
        // 409; shifts has one, because FR-110 asks for a sentence a driver can act on - so the loser
        // gets the friendly refusal when the winner committed first and the index's when it did not.
        // Both are 409, and the row count below is the claim that does not depend on which.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var first = ShiftApi.StartAsync(client, driver.Token, driver.DriverId, cancellationToken);
        var second = ShiftApi.StartAsync(client, driver.Token, driver.DriverId, cancellationToken);

        var responses = await Task.WhenAll(first, second);

        try
        {
            var statuses = responses.Select(response => response.StatusCode).Order().ToArray();

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);

            var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);

            // The loser is refused by one of two things, and which one is the database's business
            // rather than a property worth pinning: the service's lookup when the winner had already
            // committed by the time the loser read, and ix_shifts_driver_id_open when it had not.
            // Both are 409, which is the whole of NFR-2 here - a caller acts on the same status
            // either way - and the code is asserted to be one of exactly those two so a third answer
            // (a 500 from an untranslated constraint, say) fails rather than passes.
            var code = (await FleetApi.ReadAsync(loser, cancellationToken))
                .GetProperty("error")
                .GetProperty("code")
                .GetString();

            Assert.Contains(
                code,
                new[]
                {
                    nameof(ErrorCode.SHIFT_ALREADY_OPEN),
                    nameof(ErrorCode.PERSISTENCE_UNIQUE_VIOLATION),
                });

            // NFR-3 whichever of the two it was: no SQLSTATE, no constraint name, no raw code.
            await FleetApi.AssertFailureAsync(
                loser,
                HttpStatusCode.Conflict,
                Enum.Parse<ErrorCode>(code!),
                cancellationToken);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        // Exactly one row, which is the half the status codes cannot prove.
        Assert.Single(await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken));
    }

    [Fact]
    public async Task Ending_a_shift_that_was_never_started_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        using var response = await ShiftApi.EndAsync(
            client, driver.Token, driver.DriverId, cancellationToken);

        await FleetApi.AssertFailureAsync(
            response, HttpStatusCode.Conflict, ErrorCode.SHIFT_NOT_OPEN, cancellationToken);
    }

    [Fact]
    public async Task A_shift_recorded_with_a_backwards_window_is_refused_naming_the_end()
    {
        // FR-111 on the create path. The field is asserted as well as the code: NFR-4 promises
        // error.fields keys a form can attach a message to, and a refusal that named nothing would
        // leave a dispatcher with two date boxes and no idea which one is wrong.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var start = DateTimeOffset.UnixEpoch.AddYears(56);

        using (var response = await ShiftApi.CreateAsync(
                   client, dispatcher, driver.DriverId, start, start.AddHours(-1), cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.UnprocessableContent,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);

            var envelope = await FleetApi.ReadAsync(response, cancellationToken);
            var fields = envelope.GetProperty("error").GetProperty("fields");

            Assert.Equal(
                ["endedAt"],
                fields.EnumerateObject().Select(field => field.Name).ToArray());
        }

        // Nothing written: a refusal that still stored the row would be the worst of both answers.
        Assert.Empty(await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken));
    }

    [Fact]
    public async Task An_edit_that_moves_only_the_start_is_judged_on_the_window_it_would_produce()
    {
        // AD-23 and FR-111 together, over the wire. The payload carries one instant and nothing to
        // compare it with, so a service validating the payload would accept this and store a shift
        // that ends before it begins. The field named is the start, because that is the box the
        // dispatcher actually filled in.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var start = DateTimeOffset.UnixEpoch.AddYears(56);

        var shiftId = await ShiftApi.RecordedAsync(
            client, dispatcher, driver.DriverId, start, start.AddHours(8), cancellationToken);

        using (var response = await ShiftApi.EditAsync(
                   client, dispatcher, shiftId, cancellationToken, startedAt: start.AddHours(9)))
        {
            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.UnprocessableContent,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);

            var fields = (await FleetApi.ReadAsync(response, cancellationToken))
                .GetProperty("error")
                .GetProperty("fields");

            // Both ends, because a window that runs backwards is a disagreement between two values
            // rather than a fault in either - and the start is named whichever way the merge was
            // reached, which is the half this row is about.
            Assert.Equal(
                ["endedAt", "startedAt"],
                fields.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToArray());
        }

        // And the same edit inside the window is accepted, so the rule above is not a validator that
        // refuses every one-field correction - which is the same defect wearing the opposite sign.
        using var accepted = await ShiftApi.EditAsync(
            client, dispatcher, shiftId, cancellationToken, startedAt: start.AddMinutes(10));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var edited = await FleetApi.DataAsync(accepted, cancellationToken);

        Assert.Equal(start.AddMinutes(10), edited.GetProperty("startedAt").GetDateTimeOffset());

        // The end the caller never mentioned is still there: absent means "leave it alone" (AD-23).
        Assert.Equal(start.AddHours(8), edited.GetProperty("endedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_closed_shift_cannot_be_reopened_because_the_payload_cannot_say_so()
    {
        // FR-117 as a property of the contract rather than of a rule somebody could delete.
        // UpdateShiftCommand types the end as Optional<DateTimeOffset>, which has no case for a
        // present null - so the converter refuses the payload before any service sees it, and the
        // shift stays closed.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var start = DateTimeOffset.UnixEpoch.AddYears(56);

        var shiftId = await ShiftApi.RecordedAsync(
            client, dispatcher, driver.DriverId, start, start.AddHours(8), cancellationToken);

        using (var response = await ShiftApi.EditAsync(
                   client, dispatcher, shiftId, cancellationToken, sendNullEnd: true))
        {
            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.UnprocessableContent,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }

        using var read = await ShiftApi.GetAsync(client, dispatcher, shiftId, cancellationToken);

        var shift = await FleetApi.DataAsync(read, cancellationToken);

        Assert.Equal(start.AddHours(8), shift.GetProperty("endedAt").GetDateTimeOffset());
        Assert.False(shift.GetProperty("isOpen").GetBoolean());
    }

    [Fact]
    public async Task A_driver_reads_their_own_shifts_and_nobody_elses()
    {
        // FR-112. Both drivers have a shift, so an unnarrowed list would answer two rows and this
        // would fail - which is the only way to tell "narrowed correctly" from "there was only one".
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var first = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var second = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        await ShiftApi.StartedAsync(client, first.Token, first.DriverId, cancellationToken);
        await ShiftApi.StartedAsync(client, second.Token, second.DriverId, cancellationToken);

        var mine = await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, first.Token, cancellationToken),
            cancellationToken);

        Assert.Equal([first.DriverId], mine.Select(row => row.GetProperty("driverId").GetInt32()).ToArray());

        // And naming the other driver in the query string narrows to the caller rather than widening
        // to them: the narrowing is the guard's answer, and the filter is dispatch's convenience.
        var asked = await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(
                client,
                first.Token,
                cancellationToken,
                "?driverId=" + second.DriverId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);

        Assert.Equal([first.DriverId], asked.Select(row => row.GetProperty("driverId").GetInt32()).ToArray());

        // Dispatch sees both, which is what makes the two assertions above about narrowing rather
        // than about an empty database.
        Assert.Equal(
            2,
            (await ShiftApi.RowsAsync(
                await ShiftApi.ListAsync(client, dispatcher, cancellationToken),
                cancellationToken)).Length);
    }

    [Fact]
    public async Task A_driver_may_not_read_edit_or_delete_another_drivers_shift()
    {
        // FR-112's other half, on all three routes. 403 rather than 404, and before the row's
        // existence is disclosed: the guard is handed the shift's owner and refuses on that alone.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var mine = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var theirs = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var shiftId = await ShiftApi.StartedAsync(
            client, theirs.Token, theirs.DriverId, cancellationToken);

        using (var read = await ShiftApi.GetAsync(client, mine.Token, shiftId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                read, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var edited = await ShiftApi.EditAsync(
                   client,
                   mine.Token,
                   shiftId,
                   cancellationToken,
                   startedAt: DateTimeOffset.UnixEpoch.AddYears(56)))
        {
            await FleetApi.AssertFailureAsync(
                edited, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var deleted = await ShiftApi.DeleteAsync(client, mine.Token, shiftId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                deleted, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        // Neither may they open one for somebody else, which is the write the guard is handed a
        // payload for rather than a stored row.
        using (var started = await ShiftApi.StartAsync(
                   client, mine.Token, theirs.DriverId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                started, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        // A shift id that does not exist is refused the same way for a driver: 403 before 404, so
        // nobody can probe which ids are there.
        using var missing = await ShiftApi.GetAsync(client, mine.Token, 999_999, cancellationToken);

        await FleetApi.AssertFailureAsync(
            missing, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    [Fact]
    public async Task A_client_is_refused_every_shift_route()
    {
        // FR-115, and the reason the guard has a member of its own: RequireScope would have handed a
        // client the scope (null, clientId), whose null driver half reads as "unrestricted by
        // driver" - which is every shift in the system under a heading nobody wrote.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var customer = await FleetApi.TokenAsync(factory, client, UserRole.Client, cancellationToken);

        var shiftId = await ShiftApi.StartedAsync(
            client, driver.Token, driver.DriverId, cancellationToken);

        foreach (var attempt in Routes(client, customer, driver.DriverId, shiftId, cancellationToken))
        {
            using var response = await attempt;

            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_every_shift_route()
    {
        // 401 rather than 403, on every route: FR-13's session-expiry flow branches on this single
        // code, so a shift route answering 403 for a missing credential would send an expired
        // session to the wrong screen.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var shiftId = await ShiftApi.StartedAsync(
            client, driver.Token, driver.DriverId, cancellationToken);

        foreach (var attempt in Routes(client, token: null, driver.DriverId, shiftId, cancellationToken))
        {
            using var response = await attempt;

            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.Unauthorized,
                ErrorCode.AUTH_UNAUTHENTICATED,
                cancellationToken);
        }
    }

    [Theory]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Admin)]
    public async Task Dispatch_records_reads_corrects_and_deletes_another_drivers_shift(UserRole role)
    {
        // FR-113 and FR-114 for both roles that run dispatch, driven as one sequence rather than as
        // four tests: "no operation is available to one and not the other" is a claim about the
        // whole set, and two roles running the same sequence is what states it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var caller = await FleetApi.TokenAsync(factory, client, role, cancellationToken);
        var start = DateTimeOffset.UnixEpoch.AddYears(56);

        var shiftId = await ShiftApi.RecordedAsync(
            client, caller, driver.DriverId, start, start.AddHours(8), cancellationToken);

        using (var read = await ShiftApi.GetAsync(client, caller, shiftId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            var shift = await FleetApi.DataAsync(read, cancellationToken);

            Assert.Equal(driver.DriverId, shift.GetProperty("driverId").GetInt32());
            Assert.False(shift.GetProperty("isOpen").GetBoolean());
        }

        using (var edited = await ShiftApi.EditAsync(
                   client, caller, shiftId, cancellationToken, endedAt: start.AddHours(9)))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

            Assert.Equal(
                start.AddHours(9),
                (await FleetApi.DataAsync(edited, cancellationToken))
                    .GetProperty("endedAt")
                    .GetDateTimeOffset());
        }

        using (var deleted = await ShiftApi.DeleteAsync(client, caller, shiftId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.Empty(await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, caller, cancellationToken),
            cancellationToken));

        // And a shift that is not there is a 404 for dispatch rather than the 403 a driver gets:
        // they may reach every row, so the honest answer is that this one does not exist.
        using var missing = await ShiftApi.GetAsync(client, caller, 999_999, cancellationToken);

        await FleetApi.AssertFailureAsync(
            missing, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
    }

    [Fact]
    public async Task The_driver_roster_says_who_is_on_duty_and_still_offers_everybody()
    {
        // FR-116 where the assignment form reads it. The flag is derived from the open shift on
        // every read (AD-24, DR-18), so going off duty changes the answer without anything being
        // written to the driver row - which is the half a stored column would get wrong.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var working = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var resting = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        await ShiftApi.StartedAsync(client, working.Token, working.DriverId, cancellationToken);

        var roster = await RosterAsync(client, dispatcher, cancellationToken);

        // Both drivers are on the roster: FR-116 flags, and never filters.
        Assert.Equal(2, roster.Count);
        Assert.True(roster[working.DriverId]);
        Assert.False(roster[resting.DriverId]);

        // The capability's own feed agrees with the flag, because the flag is that feed.
        using (var onDuty = await ShiftApi.ListOnDutyAsync(client, dispatcher, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, onDuty.StatusCode);

            Assert.Equal(
                [working.DriverId],
                (await FleetApi.DataAsync(onDuty, cancellationToken))
                    .EnumerateArray()
                    .Select(id => id.GetInt32())
                    .ToArray());
        }

        using (var ended = await ShiftApi.EndAsync(
                   client, working.Token, working.DriverId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        }

        // Derived on demand: the shift row is still there, closed, and the flag has already changed.
        Assert.False((await RosterAsync(client, dispatcher, cancellationToken))[working.DriverId]);
    }

    [Fact]
    public async Task Going_off_duty_before_the_shift_started_is_refused()
    {
        // The one write path whose instant nobody typed, and the reason it validates at all. An open
        // shift may be dated in the future - a create carrying no end has nothing to compare its
        // start against - so a dispatcher records tomorrow's shift, the driver presses off duty
        // today, and the row would end before it began. The refusal is FR-111's rather than a
        // silently stored backwards window.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        // Far enough ahead that the host's real clock cannot overtake it while the test runs.
        var shiftId = await ShiftApi.RecordedAsync(
            client,
            dispatcher,
            driver.DriverId,
            DateTimeOffset.UtcNow.AddYears(1),
            endedAt: null,
            cancellationToken);

        using (var response = await ShiftApi.EndAsync(
                   client, driver.Token, driver.DriverId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.UnprocessableContent,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }

        // And nothing was written: the shift is still open, so the driver can go off duty once it
        // has actually started.
        using var read = await ShiftApi.GetAsync(client, dispatcher, shiftId, cancellationToken);

        Assert.True((await FleetApi.DataAsync(read, cancellationToken)).GetProperty("isOpen").GetBoolean());
    }

    [Fact]
    public async Task A_driver_records_corrects_and_deletes_a_shift_of_their_own()
    {
        // FR-112's write side, which every other driver-side row here is silent about: the rest are
        // reads or refusals, so tightening RequireShiftOwner to dispatch alone would leave the suite
        // green while MyShifts.razor shipped edit and delete buttons that could only ever 403.
        //
        // The create is here too, because the guard on it is the same member: a shift a driver
        // forgot to log at all is the first correction they need, and withholding that one route
        // while leaving them the edit would be a rule nothing else in the capability keeps.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var stranger = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var start = DateTimeOffset.UnixEpoch.AddYears(56);

        var shiftId = await ShiftApi.RecordedAsync(
            client, driver.Token, driver.DriverId, start, start.AddHours(8), cancellationToken);

        using (var edited = await ShiftApi.EditAsync(
                   client, driver.Token, shiftId, cancellationToken, endedAt: start.AddHours(9)))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

            Assert.Equal(
                start.AddHours(9),
                (await FleetApi.DataAsync(edited, cancellationToken))
                    .GetProperty("endedAt")
                    .GetDateTimeOffset());
        }

        using (var deleted = await ShiftApi.DeleteAsync(
                   client, driver.Token, shiftId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.Empty(await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken));

        // And the boundary that makes the three successes above mean something: the same route,
        // naming somebody else, is refused.
        using var forged = await ShiftApi.CreateAsync(
            client, driver.Token, stranger.DriverId, start, start.AddHours(8), cancellationToken);

        await FleetApi.AssertFailureAsync(
            forged, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    [Fact]
    public async Task A_shift_for_a_driver_no_row_holds_is_a_404_rather_than_a_broken_key()
    {
        // AD-8 leaves a foreign-key violation untranslated on purpose - an unreachable constraint
        // failure is a defect rather than a caller error - so a dispatcher's typo would arrive as a
        // 500 if the service did not answer the miss itself. Both write paths that name a driver are
        // checked, because they resolve the name separately.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using (var started = await ShiftApi.StartAsync(client, dispatcher, 999_999, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                started, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        using var recorded = await ShiftApi.CreateAsync(
            client,
            dispatcher,
            999_999,
            DateTimeOffset.UnixEpoch.AddYears(56),
            endedAt: null,
            cancellationToken);

        await FleetApi.AssertFailureAsync(
            recorded, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
    }

    [Fact]
    public async Task Dispatch_narrows_the_roster_to_one_driver_when_it_asks_to()
    {
        // FR-113's filter, which is dispatch's convenience rather than an authorization decision -
        // and is therefore the half a guard-only narrowing would silently drop. Both drivers have a
        // shift, so a filter that was ignored answers two rows here rather than nothing at all.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var first = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var second = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        await ShiftApi.StartedAsync(client, first.Token, first.DriverId, cancellationToken);
        await ShiftApi.StartedAsync(client, second.Token, second.DriverId, cancellationToken);

        var narrowed = await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, dispatcher, cancellationToken, Filter(second.DriverId)),
            cancellationToken);

        Assert.Equal(
            [second.DriverId],
            narrowed.Select(row => row.GetProperty("driverId").GetInt32()).ToArray());
    }

    [Fact]
    public async Task A_drivers_page_counts_their_own_rows_however_many_somebody_else_has()
    {
        // AD-3: the narrowing is a WHERE applied before OFFSET and LIMIT. Filtering a materialized
        // page instead would answer nothing at all here - the driver's three rows are the hundred
        // and twenty-first onward - and dropping Skip would answer the same page twice. Two rows
        // would never reach this: it takes more of somebody else's than one page holds.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var stranger = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        IReadOnlyList<int> mine;

        // Seeded through the context rather than over HTTP: a hundred and twenty round trips would
        // make this a test of the endpoint's throughput. What is under test is where the WHERE goes.
        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            await SeedShiftsAsync(context, new DriverId(stranger.DriverId), 120, cancellationToken);
            mine = await SeedShiftsAsync(context, new DriverId(driver.DriverId), 3, cancellationToken);
        }

        var first = await IdsAsync(
            await ShiftApi.ListAsync(client, driver.Token, cancellationToken, "?offset=0&limit=2"),
            cancellationToken);

        Assert.Equal(mine.Take(2), first);

        // And the offset counts the driver's rows, not everybody's: a second page of one row, not an
        // empty one and not somebody else's.
        var second = await IdsAsync(
            await ShiftApi.ListAsync(client, driver.Token, cancellationToken, "?offset=2&limit=2"),
            cancellationToken);

        Assert.Equal(mine.Skip(2), second);

        // Dispatch's filter pages over the same repository read, so its offset has to reach the
        // database too.
        var filtered = await IdsAsync(
            await ShiftApi.ListAsync(
                client,
                dispatcher,
                cancellationToken,
                "?offset=1&limit=1&driverId="
                    + driver.DriverId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);

        Assert.Equal([mine[1]], filtered);
    }

    [Fact]
    public async Task Shifts_come_back_newest_first()
    {
        // Promised by the repository, the port and the two screens, and asserted nowhere else: every
        // other list assertion here is order-insensitive, so dropping the ORDER BY would fail
        // nothing while a driver's history came back in whatever order the heap happened to hold.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        IReadOnlyList<int> newestFirst;

        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            newestFirst = await SeedShiftsAsync(
                context, new DriverId(driver.DriverId), 4, cancellationToken);
        }

        Assert.Equal(
            newestFirst,
            await IdsAsync(
                await ShiftApi.ListAsync(client, driver.Token, cancellationToken),
                cancellationToken));
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?offset=-1")]
    public async Task Paging_outside_the_bounds_is_refused_rather_than_run(string query)
    {
        // NFR-27, and the half a unit test cannot make: that the validator is actually reached from
        // the route. Deleting the ValidateAndThrowAsync call in ListAsync would leave every
        // ShiftValidationTests case green while a limit of two million became a query.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using var response = await ShiftApi.ListAsync(client, dispatcher, cancellationToken, query);

        await FleetApi.AssertFailureAsync(
            response,
            HttpStatusCode.UnprocessableContent,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);
    }

    [Fact]
    public async Task Recording_a_shift_for_a_driver_on_duty_is_allowed_closed_and_refused_open()
    {
        // Both directions of CreateAsync's one conditional. FR-113 exists so dispatch can log a
        // stretch of time somebody forgot to report, and a driver being on duty right now says
        // nothing about a window that already finished - so the closed one is written. The open one
        // is the second open shift FR-110 forbids, and it earns the friendly sentence rather than
        // the index's, because the lookup gets there first.
        //
        // Dropping the `command.EndedAt is null &&` qualifier passes every other test in this file:
        // none of the others creates a shift for a driver who already holds an open one, so the
        // correction FR-113 exists for would start answering 409 with the suite still green.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        await ShiftApi.StartedAsync(client, driver.Token, driver.DriverId, cancellationToken);

        var earlier = DateTimeOffset.UnixEpoch.AddYears(56);

        // The correction the route exists for, recorded while the driver is on duty.
        await ShiftApi.RecordedAsync(
            client,
            dispatcher,
            driver.DriverId,
            earlier,
            earlier.AddHours(8),
            cancellationToken);

        // And the one the index is there to forbid, answered by the lookup in front of it.
        using (var second = await ShiftApi.CreateAsync(
                   client,
                   dispatcher,
                   driver.DriverId,
                   earlier.AddDays(1),
                   endedAt: null,
                   cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                second, HttpStatusCode.Conflict, ErrorCode.SHIFT_ALREADY_OPEN, cancellationToken);
        }

        // The open shift and the recorded window, and nothing the refusal left behind.
        var rows = await ShiftApi.RowsAsync(
            await ShiftApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Equal(2, rows.Length);
        Assert.Single(rows, row => row.GetProperty("isOpen").GetBoolean());
    }

    [Fact]
    public async Task A_driver_is_refused_the_fleet_wide_on_duty_feed()
    {
        // The one route in the capability a driver may not reach, and the only method guarded by
        // RequireRole rather than by the two shift members. Every other route answers a driver about
        // their own rows; this one answers who is on duty across the whole fleet, which is dispatch's
        // question - FR-116 reads it through /api/drivers, never through a driver's token.
        //
        // Without this, swapping that guard to RequireShiftScope - the obvious consistency edit,
        // since every other method in the file uses the shift members - hands every driver the whole
        // fleet's roster with the suite still green: the client and anonymous sweeps are refused
        // either way, and the dispatcher's read succeeds either way.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        await ShiftApi.StartedAsync(client, driver.Token, driver.DriverId, cancellationToken);

        using var response = await ShiftApi.ListOnDutyAsync(client, driver.Token, cancellationToken);

        await FleetApi.AssertFailureAsync(
            response, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    /// <summary>A <c>?driverId=</c> query string for one driver row.</summary>
    private static string Filter(int driverId) =>
        "?driverId=" + driverId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Saves <paramref name="count"/> finished shifts for one driver, an hour each and an hour
    /// apart, and answers their ids in the order the list promises them — newest first.
    /// </summary>
    /// <remarks>
    /// All closed, deliberately: <c>IX_Shifts_DriverId_Open</c> allows one open shift per driver, so
    /// a seeded history has to be a history.
    /// </remarks>
    private static async Task<IReadOnlyList<int>> SeedShiftsAsync(
        AppDbContext context,
        DriverId driverId,
        int count,
        CancellationToken cancellationToken)
    {
        var origin = DateTimeOffset.UnixEpoch.AddYears(56);

        var shifts = Enumerable
            .Range(0, count)
            .Select(index => new Shift
            {
                DriverId = driverId,
                StartedAt = origin.AddHours(index * 2),
                EndedAt = origin.AddHours((index * 2) + 1),
            })
            .ToArray();

        context.Shifts.AddRange(shifts);

        await context.SaveChangesAsync(cancellationToken);

        return [.. shifts.Reverse().Select(shift => shift.Id)];
    }

    private static async Task<int[]> IdsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            return
            [
                .. (await ShiftApi.RowsAsync(response, cancellationToken))
                    .Select(row => row.GetProperty("id").GetInt32()),
            ];
        }
    }

    /// <summary>Whether each driver on the roster is on duty, keyed by their driver row id.</summary>
    private static async Task<Dictionary<int, bool>> RosterAsync(
        HttpClient client,
        string dispatcherToken,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/drivers", dispatcherToken, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await FleetApi.DataAsync(response, cancellationToken))
            .EnumerateArray()
            .ToDictionary(
                driver => driver.GetProperty("id").GetInt32(),
                driver => driver.GetProperty("onDuty").GetBoolean());
    }

    /// <summary>
    /// One attempt at each of the eight routes, so a role that is refused is refused everywhere
    /// rather than on the ones somebody remembered to test.
    /// </summary>
    private static IEnumerable<Task<HttpResponseMessage>> Routes(
        HttpClient client,
        string? token,
        int driverId,
        int shiftId,
        CancellationToken cancellationToken)
    {
        yield return ShiftApi.ListAsync(client, token, cancellationToken);
        yield return ShiftApi.GetAsync(client, token, shiftId, cancellationToken);
        yield return ShiftApi.ListOnDutyAsync(client, token, cancellationToken);
        yield return ShiftApi.StartAsync(client, token, driverId, cancellationToken);
        yield return ShiftApi.EndAsync(client, token, driverId, cancellationToken);
        yield return ShiftApi.CreateAsync(
            client,
            token,
            driverId,
            DateTimeOffset.UnixEpoch.AddYears(56),
            endedAt: null,
            cancellationToken);
        yield return ShiftApi.EditAsync(
            client,
            token,
            shiftId,
            cancellationToken,
            startedAt: DateTimeOffset.UnixEpoch.AddYears(56));
        yield return ShiftApi.DeleteAsync(client, token, shiftId, cancellationToken);
    }
}
