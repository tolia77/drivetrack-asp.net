using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// What the schema actually looks like once the migration has run: the storage rules AD-11,
/// AD-13, AD-21 and AD-22 fix, read back out of <c>information_schema</c> and out of the raw
/// column values rather than out of the model that produced them.
/// </summary>
public class PersistenceMappingTests(PostgresFixture postgres)
{
    /// <summary>Every table the domain model is supposed to produce, in plural snake_case.</summary>
    private static readonly string[] ExpectedTables =
    [
        "asp_net_roles",
        "asp_net_users",
        "asp_net_user_roles",
        "clients",
        "deliveries",
        "drivers",
        "messages",
        "notification_attempts",
        "proof_assets",
        "proof_of_deliveries",
        "reviews",
        "shifts",
        "timeline_entries",
        "vehicles",
    ];

    /// <summary>Tables the model must <em>not</em> produce (AD-11, DR-3).</summary>
    private static readonly string[] ForbiddenTables =
    [
        "locations", "location", "admins", "dispatchers", "administrators",
    ];

    [Fact]
    public async Task Every_expected_table_exists_in_plural_snake_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        foreach (var table in ExpectedTables)
        {
            Assert.True(
                await TestDatabase.TableExistsAsync(database.ConnectionString, table, cancellationToken),
                $"Expected table '{table}' to exist.");
        }
    }

    [Fact]
    public async Task No_location_admin_or_dispatcher_table_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        foreach (var table in ForbiddenTables)
        {
            Assert.False(
                await TestDatabase.TableExistsAsync(database.ConnectionString, table, cancellationToken),
                $"Table '{table}' must not exist.");
        }
    }

    [Fact]
    public async Task A_location_is_columns_on_its_owner_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        string[] deliveryColumns =
        [
            "pickup_latitude", "pickup_longitude", "pickup_address", "pickup_address_resolved_at",
            "dropoff_latitude", "dropoff_longitude", "dropoff_address", "dropoff_address_resolved_at",
        ];

        foreach (var column in deliveryColumns)
        {
            Assert.NotNull(await ColumnTypeAsync(database, "deliveries", column, cancellationToken));
        }

        string[] proofColumns =
        [
            "capture_latitude", "capture_longitude", "capture_address", "capture_address_resolved_at",
        ];

        foreach (var column in proofColumns)
        {
            Assert.NotNull(await ColumnTypeAsync(database, "proof_of_deliveries", column, cancellationToken));
        }
    }

    [Fact]
    public async Task Every_timestamp_column_carries_its_zone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        // AD-13, and the defect it names: the original mixed naive and aware timestamps, so the
        // same column meant different things depending on which code path wrote it.
        var naive = await ReadStringsAsync(
            database,
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND data_type LIKE 'timestamp%'
              AND data_type <> 'timestamp with time zone'
            """,
            cancellationToken);

        Assert.Empty(naive);

        // Not vacuous: there are timestamptz columns to have got right.
        var aware = await ReadStringsAsync(
            database,
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND data_type = 'timestamp with time zone'
            """,
            cancellationToken);

        Assert.NotEmpty(aware);
    }

    [Fact]
    public async Task Every_typed_identity_column_is_stored_as_an_integer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        (string Table, string Column)[] typedIdColumns =
        [
            ("clients", "id"),
            ("clients", "user_id"),
            ("drivers", "id"),
            ("drivers", "user_id"),
            ("deliveries", "client_id"),
            ("deliveries", "driver_id"),
            ("shifts", "driver_id"),
            ("messages", "driver_id"),
            ("messages", "sender_user_id"),
            ("reviews", "client_id"),
            ("timeline_entries", "actor_user_id"),
            ("proof_of_deliveries", "captured_by_user_id"),
        ];

        foreach (var (table, column) in typedIdColumns)
        {
            Assert.Equal(
                "integer",
                await ColumnTypeAsync(database, table, column, cancellationToken));
        }
    }

    [Fact]
    public async Task An_enum_is_stored_as_its_member_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var delivery = Seed.NewDelivery(status: DeliveryStatus.InTransit);
            context.Deliveries.Add(delivery);
            await context.SaveChangesAsync(cancellationToken);
            deliveryId = delivery.Id;
        }

        // AD-21: the name, not the ordinal - so reordering DeliveryStatus cannot silently
        // reinterpret every existing row.
        Assert.Equal(
            "InTransit",
            await database.ScalarAsync(
                $"SELECT status FROM deliveries WHERE id = {deliveryId}", cancellationToken));

        // And the column is text, not a native PostgreSQL enum type.
        Assert.Equal(
            "character varying",
            await ColumnTypeAsync(database, "deliveries", "status", cancellationToken));
    }

    [Fact]
    public async Task Every_other_enum_column_is_stored_as_its_member_name_too()
    {
        // AD-21 is a rule about every persisted enum, not about deliveries.status. Asserting it
        // on one column would let the other five revert to ordinals unnoticed - EF round-trips
        // identically either way, and HasPendingModelChanges() goes quiet as soon as the
        // accompanying migration is regenerated.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(context, cancellationToken);
            var delivery = await Seed.DeliveryAsync(context, cancellationToken);

            context.TimelineEntries.Add(Seed.NewTimelineEntry(
                delivery.Id,
                new UserId(user.Id),
                previousStatus: DeliveryStatus.Pending,
                newStatus: DeliveryStatus.InTransit));

            var proof = new ProofOfDelivery
            {
                DeliveryId = delivery.Id,
                RecipientName = "Recipient",
                CaptureLocation = Seed.Location(),
                CapturedAt = Seed.Instant,
                CapturedByUserId = new UserId(user.Id),
            };
            proof.Assets.Add(new ProofAsset
            {
                Kind = ProofAssetKind.Signature,
                StorageKey = "proof/1/signature",
                ContentType = "image/png",
            });
            context.ProofOfDeliveries.Add(proof);

            context.NotificationAttempts.Add(new NotificationAttempt
            {
                DeliveryId = delivery.Id,
                Kind = NotificationKind.StatusChange,
                Recipient = "someone@drivetrack.test",
                AttemptedAt = Seed.Instant,
                Outcome = NotificationOutcome.Failed,
                Error = "SMTP timeout",
            });

            await context.SaveChangesAsync(cancellationToken);
        }

        (string Table, string Column, string Expected)[] enumColumns =
        [
            ("timeline_entries", "previous_status", "Pending"),
            ("timeline_entries", "new_status", "InTransit"),
            ("proof_assets", "kind", "Signature"),
            ("notification_attempts", "kind", "StatusChange"),
            ("notification_attempts", "outcome", "Failed"),
        ];

        foreach (var (table, column, expected) in enumColumns)
        {
            Assert.Equal(
                expected,
                await database.ScalarAsync($"SELECT {column} FROM {table} LIMIT 1", cancellationToken));

            Assert.Equal(
                "character varying",
                await ColumnTypeAsync(database, table, column, cancellationToken));
        }
    }

    [Fact]
    public async Task An_enum_survives_the_round_trip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;

        await using (var writer = await database.CreateContextAsync(cancellationToken))
        {
            var delivery = Seed.NewDelivery(status: DeliveryStatus.Failed);
            writer.Deliveries.Add(delivery);
            await writer.SaveChangesAsync(cancellationToken);
            deliveryId = delivery.Id;
        }

        await using var reader = await database.CreateContextAsync(cancellationToken);
        var loaded = await reader.Deliveries.FindAsync([deliveryId], cancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(DeliveryStatus.Failed, loaded.Status);
    }

    [Fact]
    public async Task A_timestamp_with_a_non_zero_offset_is_refused_on_write()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        // AD-13's storage rule is not advice: Npgsql refuses a non-zero offset against
        // timestamptz outright, which is what makes "convert only at the edge" enforceable
        // rather than a convention people remember to follow.
        var delivery = Seed.NewDelivery();
        delivery.CreatedAt = new DateTimeOffset(2026, 9, 7, 15, 0, 0, TimeSpan.FromHours(3));

        context.Deliveries.Add(delivery);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => context.SaveChangesAsync(cancellationToken));

        var messages = new List<string>();
        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        Assert.Contains(messages, message => message.Contains("offset", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> ColumnTypeAsync(
        TestDatabase database,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
            """;
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        TestDatabase database,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var results = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
