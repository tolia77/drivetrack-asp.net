using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DriveTrack.Application.Chat;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Identity;
using DriveTrack.Integration.Tests.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DriveTrack.Integration.Tests.Chat;

/// <summary>
/// FR-68 to FR-76 driven end to end, against a real PostgreSQL container and a real
/// <c>HubConnection</c>.
/// <para>
/// NFR-17 names messaging as the one feature the original never tested, and the two defects this
/// rewrite exists to fix are both invisible to any test that stops short of the wire: a message
/// stored against a key it could never be read back from, and a history endpoint anyone could walk
/// by iterating ids. So every row of the story's matrix is asserted through a connection that had to
/// negotiate, authenticate and be added to a group before it heard anything, and the persisted rows
/// are read back outside EF where it matters.
/// </para>
/// </summary>
public class ChatHubTests(PostgresFixture postgres)
{
    /// <summary>
    /// How long a test waits for a broadcast before calling it lost. Long enough that a loaded CI
    /// agent is not the thing under test, short enough that a genuine failure is not a hung suite.
    /// </summary>
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a test waits before concluding that a message did <em>not</em> arrive. Shorter, and
    /// deliberately so: this one is spent on every run of the tests that assert an absence.
    /// </summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(2);

    private static readonly DateTimeOffset Instant = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    // =====================================================================================
    // Who may reach what
    // =====================================================================================

    [Fact]
    public async Task A_driver_opening_their_own_thread_is_given_its_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var driver = await world.DriverAsync(cancellationToken);

        await world.SeedMessageAsync(
            driver.DriverId!.Value, driver.UserId, "Виїхав.", Instant, cancellationToken);

        var connection = await world.ConnectAsync(driver, cancellationToken);

        var thread = await connection.InvokeAsync<ChatThread>(
            "JoinThread", driver.DriverId!.Value, cancellationToken);

        // The thread key on the way back is the driver row it was keyed on, not a user id (AD-22).
        Assert.Equal(driver.DriverId!.Value, thread.DriverId.Value);
        Assert.NotEqual(driver.UserId, thread.DriverId.Value);

        var message = Assert.Single(thread.Messages);

        Assert.Equal("Виїхав.", message.Text);
        Assert.Equal(driver.UserId, message.SenderUserId);
    }

    [Fact]
    public async Task A_driver_opening_another_drivers_thread_is_refused_and_told_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var intruder = await world.DriverAsync(cancellationToken);
        var other = await world.DriverAsync(cancellationToken, "Олена", "Мельник");

        await world.SeedMessageAsync(
            other.DriverId!.Value, other.UserId, "Приватна розмова.", Instant, cancellationToken);

        var connection = await world.ConnectAsync(intruder, cancellationToken);

        var failure = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync<ChatThread>(
                "JoinThread", other.DriverId!.Value, cancellationToken));

        AssertRefusal(failure, ErrorCode.AUTH_FORBIDDEN);

        // The refusal disclosed nothing, and it left no group membership behind: a message written
        // into that conversation afterwards must not reach this connection. This is the assertion
        // the original could not have passed - it served history from an unauthenticated endpoint.
        Assert.DoesNotContain("Приватна розмова", failure.Message, StringComparison.Ordinal);

        var received = Listen(connection);

        var owner = await world.ConnectAsync(other, cancellationToken);
        await owner.InvokeAsync("JoinThread", other.DriverId!.Value, cancellationToken);
        await owner.InvokeAsync("Send", other.DriverId!.Value, "Наживо.", cancellationToken);

        Assert.False(await Arrived(received, Silence));
    }

    [Fact]
    public async Task A_dispatcher_reaches_every_thread_including_lines_another_dispatcher_wrote()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var first = await world.DispatcherAsync(admin.Token, cancellationToken);
        var second = await world.DispatcherAsync(admin.Token, cancellationToken, "Богдан", "Кравець");
        var driver = await world.DriverAsync(cancellationToken);

        await world.SeedMessageAsync(
            driver.DriverId!.Value, second.UserId, "Забери вантаж.", Instant, cancellationToken);

        var connection = await world.ConnectAsync(first, cancellationToken);

        var thread = await connection.InvokeAsync<ChatThread>(
            "JoinThread", driver.DriverId!.Value, cancellationToken);

        var message = Assert.Single(thread.Messages);

        Assert.Equal("Забери вантаж.", message.Text);
        Assert.Equal(second.UserId, message.SenderUserId);

        // FR-71: the conversation is about the driver, so the header names them rather than
        // whichever dispatcher happens to be reading it.
        Assert.Equal("Петро Шевченко", thread.DriverName);
    }

    [Fact]
    public async Task A_dispatcher_lists_every_driver_by_name_and_a_driver_lists_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var dispatcher = await world.DispatcherAsync(admin.Token, cancellationToken);

        var yatsenko = await world.DriverAsync(cancellationToken, "Петро", "Яценко");
        var androsova = await world.DriverAsync(cancellationToken, "Олена", "Андросова");

        var desk = await world.ConnectAsync(dispatcher, cancellationToken);

        var roster = await desk.InvokeAsync<IReadOnlyList<ChatThreadSummary>>(
            "ListThreads", cancellationToken);

        Assert.Equal(2, roster.Count);
        Assert.Contains(roster, entry => entry.DriverId.Value == yatsenko.DriverId);
        Assert.Contains(roster, entry => entry.DriverId.Value == androsova.DriverId);

        // FR-68 names the roster by person, so it is ordered by person rather than by the order the
        // drivers were taken on - which is the order the driver capability's own list comes back in.
        Assert.Equal(
            roster.Select(entry => entry.DriverName).Order(StringComparer.Ordinal).ToArray(),
            roster.Select(entry => entry.DriverName).ToArray());

        // A driver has one conversation and no roster: the null argument the guard reads as "the
        // roster" never equals a driver's own id, so this is refused without a second rule.
        var cab = await world.ConnectAsync(yatsenko, cancellationToken);

        var failure = await Assert.ThrowsAsync<HubException>(
            () => cab.InvokeAsync<IReadOnlyList<ChatThreadSummary>>("ListThreads", cancellationToken));

        AssertRefusal(failure, ErrorCode.AUTH_FORBIDDEN);
    }

    [Fact]
    public async Task A_roster_row_carries_the_vehicle_the_plate_and_the_duty_the_driver_capability_answered_with()
    {
        // The roster read the driver capability's summary and kept the name out of it. Everything
        // else on that summary - the van, its plate, whether the person is at the wheel - was loaded
        // and dropped on the floor, leaving a dispatcher to tell two drivers of the same name apart
        // by nothing at all. This is the assertion that the work already done reaches the screen.
        //
        // End to end rather than against the mapping, because there are three boundaries between
        // the driver capability's answer and the browser's: the chat service's projection, the hub's
        // return type, and the protocol that serializes it. A unit test of the middle one would pass
        // with any of the other two dropping the fields.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var dispatcher = await world.DispatcherAsync(admin.Token, cancellationToken);

        var driving = await world.DriverAsync(cancellationToken, "Олена", "Андросова");
        var resting = await world.DriverAsync(cancellationToken, "Петро", "Яценко");

        await world.PutOnDutyWithAVehicleAsync(
            driving.DriverId!.Value, "Renault Master", "АА1234ВС", Instant, cancellationToken);

        var desk = await world.ConnectAsync(dispatcher, cancellationToken);

        var roster = await desk.InvokeAsync<IReadOnlyList<ChatThreadSummary>>(
            "ListThreads", cancellationToken);

        var equipped = Assert.Single(roster, entry => entry.DriverId.Value == driving.DriverId);

        Assert.Equal("Renault Master", equipped.VehicleModel);
        Assert.Equal("АА1234ВС", equipped.VehicleLicensePlate);
        Assert.True(equipped.OnDuty);

        // And the other direction, which is the half that catches a projection filling the fields in
        // with something rather than reading them: a driver with no vehicle has no vehicle, and
        // absent is absent rather than an empty string in the roster row.
        var bare = Assert.Single(roster, entry => entry.DriverId.Value == resting.DriverId);

        Assert.Null(bare.VehicleModel);
        Assert.Null(bare.VehicleLicensePlate);
        Assert.False(bare.OnDuty);
    }

    [Fact]
    public async Task A_client_and_an_admin_reach_neither_a_thread_nor_the_roster()
    {
        // The row a reader will assume is a mistake. AD-4 makes an administrator satisfy every other
        // check by rule; chat is the one capability where the PRD locks them out on purpose, and the
        // original's defect list records "chat accepts clients and admins" as a fault to fix.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var customer = await world.ClientAsync(cancellationToken);
        var driver = await world.DriverAsync(cancellationToken);

        await world.SeedMessageAsync(
            driver.DriverId!.Value, driver.UserId, "Уже їду.", Instant, cancellationToken);

        foreach (var outsider in new[] { admin, customer })
        {
            var connection = await world.ConnectAsync(outsider, cancellationToken);

            var join = await Assert.ThrowsAsync<HubException>(
                () => connection.InvokeAsync<ChatThread>(
                    "JoinThread", driver.DriverId!.Value, cancellationToken));

            AssertRefusal(join, ErrorCode.AUTH_FORBIDDEN);

            // Nothing of the conversation leaked with the refusal.
            Assert.DoesNotContain("Уже їду", join.Message, StringComparison.Ordinal);

            var roster = await Assert.ThrowsAsync<HubException>(
                () => connection.InvokeAsync<IReadOnlyList<ChatThreadSummary>>(
                    "ListThreads", cancellationToken));

            AssertRefusal(roster, ErrorCode.AUTH_FORBIDDEN);

            // No group membership was created, so a message sent into the conversation never
            // reaches them.
            var received = Listen(connection);

            var owner = await world.ConnectAsync(driver, cancellationToken);
            await owner.InvokeAsync("JoinThread", driver.DriverId!.Value, cancellationToken);
            await owner.InvokeAsync("Send", driver.DriverId!.Value, "Наживо.", cancellationToken);

            Assert.False(await Arrived(received, Silence));
        }
    }

    [Fact]
    public async Task An_anonymous_connection_is_refused_at_the_hub_before_any_service_runs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        // The negotiate itself, asserted at the HTTP level: the status is the contract, and reading
        // it off the connection's own exception would be reading a client's phrasing.
        using var response = await world.Client.PostAsJsonAsync(
            new Uri("/hubs/chat/negotiate?negotiateVersion=1", UriKind.Relative),
            new { },
            cancellationToken);

        // 401 rather than the cookie handler's redirect to a sign-in page, which a programmatic
        // caller cannot read - the two challenges run in order and the bearer handler's envelope
        // writer runs last.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // And the client agrees: a connection with no credentials never starts.
        var connection = world.Build(token: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(cancellationToken));

        // Nothing was written and nothing was read, because no application service ever ran: the
        // refusal happened in the pipeline, one hop before the hub.
        Assert.Equal(
            0L,
            await AdministrationApi.CountAsync(world.Factory, "messages", "TRUE", cancellationToken));
    }

    [Fact]
    public async Task A_caller_holding_only_the_session_cookie_is_accepted_by_the_hub()
    {
        // Every other connection in this suite is bearer-authenticated, and a bearer token is a
        // credential no browser on this product ever holds: the circuit holds drivetrack.session and
        // nothing else. The hub is mounted outside /api for exactly that reason - AD-22 makes /api
        // bearer-only - and its [Authorize] names the cookie scheme first.
        //
        // Without this case, removing the cookie scheme from that attribute leaves every other test
        // here green while every real browser is refused at negotiate.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var driver = await world.DriverAsync(cancellationToken);

        await world.SeedMessageAsync(
            driver.DriverId!.Value, driver.UserId, "З кабіни.", Instant, cancellationToken);

        var browser = await world.ConnectWithCookieAsync(driver, cancellationToken);

        var thread = await browser.InvokeAsync<ChatThread>(
            "JoinThread", driver.DriverId!.Value, cancellationToken);

        // Not merely connected: the cookie carried a caller all the way to the guard, which is what
        // ICurrentUser had to read out of the hub scope for the join to be allowed at all.
        Assert.Equal(driver.DriverId!.Value, thread.DriverId.Value);
        Assert.Equal("З кабіни.", Assert.Single(thread.Messages).Text);
    }

    [Fact]
    public async Task A_thread_for_a_driver_that_does_not_exist_is_not_found_after_the_guard_passes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var dispatcher = await world.DispatcherAsync(admin.Token, cancellationToken);

        var connection = await world.ConnectAsync(dispatcher, cancellationToken);

        var failure = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync<ChatThread>("JoinThread", 999, cancellationToken));

        // 404, not 403: the guard let this caller through, so the honest answer is that there is no
        // such conversation. A dispatcher may reach every conversation, including ones that do not
        // exist.
        AssertRefusal(failure, ErrorCode.CHAT_THREAD_NOT_FOUND);
    }

    [Fact]
    public async Task A_refusal_carries_its_code_and_the_host_never_turns_on_detailed_errors()
    {
        // The two halves of NFR-3 for the hub. A HubException's message is the one thing SignalR
        // sends a client verbatim, and the hub puts exactly one resource key in it - so the screen
        // can say what went wrong. Everything else stays a generic sentence, and this pins the
        // switch that would change that: with detailed errors on, every unhandled exception's type
        // and message would reach the browser.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var options = world.Factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;

        // Null is the unset case and behaves as false; both are fine and true is not. Written as a
        // refusal of `true` rather than an assertion of `false`, so leaving the switch alone keeps
        // passing and turning it on has to argue with this line.
        Assert.NotEqual(true, options.EnableDetailedErrors);

        var customer = await world.ClientAsync(cancellationToken);
        var connection = await world.ConnectAsync(customer, cancellationToken);

        var failure = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync<IReadOnlyList<ChatThreadSummary>>("ListThreads", cancellationToken));

        AssertRefusal(failure, ErrorCode.AUTH_FORBIDDEN);
    }

    // =====================================================================================
    // Writing, and reading back what was written
    // =====================================================================================

    [Fact]
    public async Task A_message_a_driver_sends_is_stored_against_the_thread_it_is_read_back_from()
    {
        // The defect this whole story exists to fix: the original stored a driver's message against
        // a recipient nobody, so it could never be retrieved. Written through the hub, read back out
        // of the table by hand, and then read back again through a fresh connection.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var driver = await world.DriverAsync(cancellationToken);
        var connection = await world.ConnectAsync(driver, cancellationToken);

        await connection.InvokeAsync("JoinThread", driver.DriverId!.Value, cancellationToken);
        await connection.InvokeAsync("Send", driver.DriverId!.Value, "привіт", cancellationToken);

        const string predicate = "text = 'привіт'";

        Assert.Equal(1L, await AdministrationApi.CountAsync(world.Factory, "messages", predicate, cancellationToken));

        Assert.Equal(
            (long)driver.DriverId!.Value,
            Convert.ToInt64(
                await world.Factory.Database.ScalarAsync(
                    $"SELECT driver_id FROM messages WHERE {predicate}", cancellationToken),
                CultureInfo.InvariantCulture));

        Assert.Equal(
            (long)driver.UserId,
            Convert.ToInt64(
                await world.Factory.Database.ScalarAsync(
                    $"SELECT sender_user_id FROM messages WHERE {predicate}", cancellationToken),
                CultureInfo.InvariantCulture));

        // A fresh connection, so the history comes from the table rather than from anything the
        // first connection is still holding.
        var later = await world.ConnectAsync(driver, cancellationToken);

        var thread = await later.InvokeAsync<ChatThread>(
            "JoinThread", driver.DriverId!.Value, cancellationToken);

        Assert.Contains(thread.Messages, message => message.Text == "привіт");
    }

    [Fact]
    public async Task A_message_reaches_the_other_participant_without_a_reload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var dispatcher = await world.DispatcherAsync(admin.Token, cancellationToken);
        var driver = await world.DriverAsync(cancellationToken);

        var cab = await world.ConnectAsync(driver, cancellationToken);
        var desk = await world.ConnectAsync(dispatcher, cancellationToken);

        await cab.InvokeAsync("JoinThread", driver.DriverId!.Value, cancellationToken);
        await desk.InvokeAsync("JoinThread", driver.DriverId!.Value, cancellationToken);

        var atTheDesk = Listen(desk);
        var inTheCab = Listen(cab);

        await cab.InvokeAsync("Send", driver.DriverId!.Value, "Затримка на годину.", cancellationToken);

        var delivered = await Wait(atTheDesk, Delivery);

        Assert.Equal(driver.DriverId!.Value, delivered.DriverId);
        Assert.Equal("Затримка на годину.", delivered.Message.Text);
        Assert.Equal(driver.UserId, delivered.Message.SenderUserId);

        // The row the server stored carries the id it was given, which is what makes the broadcast
        // an echo of the database rather than of what was typed (FR-73: no optimistic echo).
        Assert.True(delivered.Message.Id > 0);

        // And the sender hears their own line back the same way, because that is what puts it on
        // their screen.
        Assert.Equal("Затримка на годину.", (await Wait(inTheCab, Delivery)).Message.Text);

        // The other direction, which is the one FR-70 is mostly about: the desk answering a driver.
        // It is not the same code path read backwards - the guard passes a dispatcher on a thread
        // that is not their own, and the stored row carries a sender who has no driver row at all -
        // so it is asserted rather than assumed.
        var backInTheCab = Listen(cab);

        await desk.InvokeAsync("Send", driver.DriverId!.Value, "Прийняв, чекаємо.", cancellationToken);

        var answered = await Wait(backInTheCab, Delivery);

        Assert.Equal(driver.DriverId!.Value, answered.DriverId);
        Assert.Equal("Прийняв, чекаємо.", answered.Message.Text);

        // Stored against the driver's thread and against the dispatcher who wrote it: the thread
        // key is the conversation, never the sender (AD-15).
        Assert.Equal(dispatcher.UserId, answered.Message.SenderUserId);
        Assert.True(answered.Message.Id > 0);
    }

    [Fact]
    public async Task A_connection_that_left_a_thread_is_told_nothing_more_about_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var dispatcher = await world.DispatcherAsync(admin.Token, cancellationToken);
        var driver = await world.DriverAsync(cancellationToken);

        var cab = await world.ConnectAsync(driver, cancellationToken);
        var desk = await world.ConnectAsync(dispatcher, cancellationToken);

        await cab.InvokeAsync("JoinThread", driver.DriverId!.Value, cancellationToken);
        await desk.InvokeAsync("JoinThread", driver.DriverId!.Value, cancellationToken);

        // What a dispatcher does on picking a different driver. Nothing else in the suite invokes
        // it, so without this the method could give up no membership at all and every other
        // assertion would still pass - the desk would simply go on receiving conversations it had
        // left, for the life of the connection.
        await desk.InvokeAsync("LeaveThread", driver.DriverId!.Value, cancellationToken);

        var atTheDesk = Listen(desk);
        var inTheCab = Listen(cab);

        await cab.InvokeAsync("Send", driver.DriverId!.Value, "Вже в дорозі.", cancellationToken);

        // The driver is still joined, so the broadcast did happen - which is what makes the desk's
        // silence a membership that was given up rather than a message that was never sent.
        Assert.Equal("Вже в дорозі.", (await Wait(inTheCab, Delivery)).Message.Text);

        Assert.False(await Arrived(atTheDesk, Silence));
    }

    [Fact]
    public async Task A_driver_sending_into_another_drivers_thread_is_refused_and_writes_nothing()
    {
        // Send is an independent hub entry point: JoinThread being proved to refuse a foreign driver
        // says nothing about it. This is the write half of the defect the story exists to fix, and
        // the one a group membership cannot mediate - a caller can invoke Send without ever joining.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var intruder = await world.DriverAsync(cancellationToken);
        var other = await world.DriverAsync(cancellationToken, "Олена", "Мельник");

        var connection = await world.ConnectAsync(intruder, cancellationToken);

        var failure = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Send", other.DriverId!.Value, "Чуже.", cancellationToken));

        AssertRefusal(failure, ErrorCode.AUTH_FORBIDDEN);

        Assert.Equal(
            0L,
            await AdministrationApi.CountAsync(world.Factory, "messages", "TRUE", cancellationToken));
    }

    [Fact]
    public async Task A_client_and_an_admin_send_into_no_thread_at_all()
    {
        // The write half of the admin lockout. AD-4 makes an administrator satisfy every other check
        // in the system, so "the join refuses them" is the assertion a reader would assume covers
        // this - and it does not: Send takes its own decision, in its own body.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var customer = await world.ClientAsync(cancellationToken);
        var driver = await world.DriverAsync(cancellationToken);

        foreach (var outsider in new[] { admin, customer })
        {
            var connection = await world.ConnectAsync(outsider, cancellationToken);

            var failure = await Assert.ThrowsAsync<HubException>(
                () => connection.InvokeAsync(
                    "Send", driver.DriverId!.Value, "Стороннє.", cancellationToken));

            AssertRefusal(failure, ErrorCode.AUTH_FORBIDDEN);
        }

        Assert.Equal(
            0L,
            await AdministrationApi.CountAsync(world.Factory, "messages", "TRUE", cancellationToken));
    }

    [Fact]
    public async Task Sending_into_a_thread_that_does_not_exist_is_not_found_and_writes_nothing()
    {
        // The guard passes a dispatcher for every conversation, including ones with no driver behind
        // them, so the honest answer is 404 - and the row must not be written against a driver id no
        // row holds, which the foreign key would refuse anyway in a shape nobody can read.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var admin = await world.AdminAsync(cancellationToken);
        var dispatcher = await world.DispatcherAsync(admin.Token, cancellationToken);

        var connection = await world.ConnectAsync(dispatcher, cancellationToken);

        var failure = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Send", 999, "У порожнечу.", cancellationToken));

        AssertRefusal(failure, ErrorCode.CHAT_THREAD_NOT_FOUND);

        Assert.Equal(
            0L,
            await AdministrationApi.CountAsync(world.Factory, "messages", "TRUE", cancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // Null is a designed input, not a hostile one: the screen sends its draft, which is null until
    // the box has been typed in. The hub's parameter is nullable so this reaches AD-8's validator
    // rather than the hub binder's own answer, and this row is what proves it survives the wire.
    [InlineData(null)]
    public async Task An_empty_message_is_refused_and_nothing_is_written(string? text)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var driver = await world.DriverAsync(cancellationToken);
        var connection = await world.ConnectAsync(driver, cancellationToken);

        await connection.InvokeAsync("JoinThread", driver.DriverId!.Value, cancellationToken);

        var failure = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Send", driver.DriverId!.Value, text, cancellationToken));

        AssertRefusal(failure, ErrorCode.CHAT_MESSAGE_TEXT_REQUIRED);

        // Judged over the trimmed text, so three spaces is nothing rather than three characters.
        Assert.Equal(
            0L,
            await AdministrationApi.CountAsync(world.Factory, "messages", "TRUE", cancellationToken));
    }

    [Fact]
    public async Task A_message_over_two_thousand_characters_is_refused_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var driver = await world.DriverAsync(cancellationToken);
        var connection = await world.ConnectAsync(driver, cancellationToken);

        await connection.InvokeAsync("JoinThread", driver.DriverId!.Value, cancellationToken);

        var failure = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync(
                "Send", driver.DriverId!.Value, new string('я', 2001), cancellationToken));

        // The validator's limit is the column's, so a message the database would truncate is a 422
        // naming the field rather than a 500 naming a constraint.
        AssertRefusal(failure, ErrorCode.CHAT_MESSAGE_TEXT_TOO_LONG);

        Assert.Equal(
            0L,
            await AdministrationApi.CountAsync(world.Factory, "messages", "TRUE", cancellationToken));

        // And the boundary itself is accepted, or the limit would be off by one in the safe-looking
        // direction and nobody would notice until a long message was silently refused.
        await connection.InvokeAsync(
            "Send", driver.DriverId!.Value, new string('я', 2000), cancellationToken);

        Assert.Equal(
            1L,
            await AdministrationApi.CountAsync(world.Factory, "messages", "TRUE", cancellationToken));
    }

    // =====================================================================================
    // What a history is
    // =====================================================================================

    [Fact]
    public async Task History_is_chronological_and_ties_are_broken_by_id()
    {
        // Two rows committed in one instant share a SentAt. Without the tie-break they interleave at
        // the planner's discretion, and FR-72 asks for an order rather than an almost-order.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var driver = await world.DriverAsync(cancellationToken);

        var second = await world.SeedMessageAsync(
            driver.DriverId!.Value, driver.UserId, "друге", Instant, cancellationToken);
        var third = await world.SeedMessageAsync(
            driver.DriverId!.Value, driver.UserId, "третє", Instant, cancellationToken);
        var first = await world.SeedMessageAsync(
            driver.DriverId!.Value, driver.UserId, "перше", Instant.AddMinutes(-5), cancellationToken);

        var connection = await world.ConnectAsync(driver, cancellationToken);

        var thread = await connection.InvokeAsync<ChatThread>(
            "JoinThread", driver.DriverId!.Value, cancellationToken);

        Assert.Equal(
            new[] { first, second, third },
            thread.Messages.Select(message => message.Id).ToArray());
    }

    [Fact]
    public async Task A_conversation_holds_only_its_own_drivers_lines()
    {
        // The other half of "read back from the same key it was written against": a conversation
        // keyed on one driver must not pick up another's, which a read that forgot its WHERE would
        // do while every ordering assertion above still passed.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var mine = await world.DriverAsync(cancellationToken);
        var theirs = await world.DriverAsync(cancellationToken, "Олена", "Мельник");

        await world.SeedMessageAsync(mine.DriverId!.Value, mine.UserId, "моє", Instant, cancellationToken);
        await world.SeedMessageAsync(theirs.DriverId!.Value, theirs.UserId, "чуже", Instant, cancellationToken);

        var connection = await world.ConnectAsync(mine, cancellationToken);

        var thread = await connection.InvokeAsync<ChatThread>(
            "JoinThread", mine.DriverId!.Value, cancellationToken);

        var message = Assert.Single(thread.Messages);

        Assert.Equal("моє", message.Text);
    }

    [Fact]
    public async Task A_line_whose_sender_was_deleted_still_reads_back_unattributed()
    {
        // AD-20's set-null, seen from the screen. Cascade would delete half a conversation to close
        // one account and restrict would make closing it impossible; the line survives with no
        // sender, and the name is left empty because the label is a sentence the catalogue owns.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await ChatWorld.CreateAsync(postgres.ConnectionString, cancellationToken);

        var driver = await world.DriverAsync(cancellationToken);
        var admin = await world.AdminAsync(cancellationToken);
        var dispatcher = await world.DispatcherAsync(admin.Token, cancellationToken);

        await world.SeedMessageAsync(
            driver.DriverId!.Value, dispatcher.UserId, "Заберіть накладну.", Instant, cancellationToken);

        await world.Factory.Database.ExecuteAsync(
            $"DELETE FROM asp_net_users WHERE id = {dispatcher.UserId}", cancellationToken);

        var connection = await world.ConnectAsync(driver, cancellationToken);

        var thread = await connection.InvokeAsync<ChatThread>(
            "JoinThread", driver.DriverId!.Value, cancellationToken);

        var message = Assert.Single(thread.Messages);

        Assert.Equal("Заберіть накладну.", message.Text);
        Assert.Null(message.SenderUserId);
        Assert.Equal(string.Empty, message.SenderName);
    }

    // =====================================================================================
    // Listening
    // =====================================================================================

    /// <summary>
    /// Asserts that a refusal named its contract code and nothing else.
    /// <para>
    /// SignalR wraps a <c>HubException</c>'s message in a sentence of its own, which is why this
    /// reads the code out rather than comparing the whole string — and why the test above pins
    /// <c>EnableDetailedErrors</c> off, so what reaches a caller is the code and never a stack
    /// frame, a SQL fragment or a constraint name (NFR-3).
    /// </para>
    /// </summary>
    private static void AssertRefusal(HubException failure, ErrorCode expected)
    {
        Assert.Contains(expected.ToString(), failure.Message, StringComparison.Ordinal);

        foreach (var leak in new[] { "   at ", "SELECT ", "Npgsql", "DriveTrack.Application" })
        {
            Assert.DoesNotContain(leak, failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>What a broadcast carries: the conversation it belongs to, and the row that was stored.</summary>
    private sealed record Delivered(int DriverId, ChatMessage Message);

    /// <summary>
    /// Registers a listener for the next broadcast on this connection.
    /// <para>
    /// A completion source rather than a flag and a sleep: a test that polls is a test whose timing
    /// is part of what it asserts, and this suite is about delivery rather than about latency.
    /// </para>
    /// </summary>
    private static TaskCompletionSource<Delivered> Listen(HubConnection connection)
    {
        var arrived = new TaskCompletionSource<Delivered>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<int, ChatMessage>(
            "ReceiveMessage",
            (driverId, message) => arrived.TrySetResult(new Delivered(driverId, message)));

        return arrived;
    }

    private static async Task<Delivered> Wait(TaskCompletionSource<Delivered> arrived, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(arrived.Task, Task.Delay(timeout));

        Assert.Same(arrived.Task, completed);

        return await arrived.Task;
    }

    private static async Task<bool> Arrived(TaskCompletionSource<Delivered> arrived, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(arrived.Task, Task.Delay(timeout));

        return completed == arrived.Task;
    }
}
