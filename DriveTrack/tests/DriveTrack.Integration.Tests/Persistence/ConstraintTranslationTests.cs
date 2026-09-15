using DriveTrack.Application.Common;
using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ValidationException = DriveTrack.Application.Common.ValidationException;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// AD-8's translation, driven through <c>IUnitOfWork.CommitAsync</c> against a real PostgreSQL 18
/// container.
/// <para>
/// Story 1.2 put the invariants in the schema; on their own they surface as a raw
/// <c>DbUpdateException</c>, which is precisely the 500-instead-of-409 defect this rewrite exists to
/// remove. Neither half satisfies the concurrency requirements alone, so this suite asserts the
/// join: the constraint refuses the row, and the commit path turns that refusal into a typed
/// failure carrying a contract code.
/// </para>
/// </summary>
public class ConstraintTranslationTests(PostgresFixture postgres)
{
    /// <summary>The only file under <c>src/</c> permitted to name <c>PostgresException</c>.</summary>
    private const string TranslatorPath =
        "DriveTrack.Infrastructure/Persistence/PostgresConstraintTranslator.cs";

    [Fact]
    public async Task A_unique_violation_becomes_a_conflict()
    {
        // Two reviews for one delivery: SQLSTATE 23505, and a genuine conflict with another row.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        var (deliveryId, clientId) = await SeedDeliveryAsync(database, cancellationToken);

        await using (var first = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            first.Reviews.Add(NewReview(deliveryId, clientId, rating: 5));
            await first.CommitAsync(cancellationToken);
        }

        await using var second = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);
        second.Reviews.Add(NewReview(deliveryId, clientId, rating: 4));

        var failure = await Assert.ThrowsAsync<ConflictException>(
            () => second.CommitAsync(cancellationToken));

        Assert.Equal(ErrorCode.PERSISTENCE_UNIQUE_VIOLATION, failure.Code);

        // The cause is kept, so the log still has the SQL detail the wire never sees (NFR-3).
        Assert.IsType<DbUpdateException>(failure.InnerException);
    }

    [Fact]
    public async Task A_second_proof_for_one_delivery_becomes_the_capability_s_own_conflict()
    {
        // The one constraint the translator names. FR-123's "one proof per delivery" is stated twice
        // - by ProofOfDeliveryService's re-check inside its writing transaction, and by
        // ix_proof_of_deliveries_delivery_id behind it - and two captures closer together than a
        // round trip are refused by the second rather than the first. Without this arm the same
        // refusal for the same reason carries a different code depending on timing the caller cannot
        // see, so the driver whose upload lost a race is told something the other one is not.
        //
        // Driven through IUnitOfWork rather than through two racing requests, so what is pinned here
        // is the translation itself: the race is asserted over HTTP in ProofOfDeliveryTests, where
        // it is a race, and this case is deterministic.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        var (deliveryId, _) = await SeedDeliveryAsync(database, cancellationToken);

        await using (var first = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            first.ProofOfDeliveries.Add(NewProof(deliveryId));
            await first.CommitAsync(cancellationToken);
        }

        await using var second = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);
        second.ProofOfDeliveries.Add(NewProof(deliveryId));

        var failure = await Assert.ThrowsAsync<ConflictException>(
            () => second.CommitAsync(cancellationToken));

        Assert.Equal(ErrorCode.DELIVERY_PROOF_ALREADY_CAPTURED, failure.Code);

        // Still 23505, and the constraint name is still in the message for the log - the code is
        // what changed, and the wire never sees either (NFR-3).
        Assert.IsType<DbUpdateException>(failure.InnerException);
        Assert.Contains("ix_proof_of_deliveries_delivery_id", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_violation_becomes_a_validation_failure()
    {
        // A rating of 6: SQLSTATE 23514, and a value the caller should not have sent. AD-8 splits
        // this from the unique case, which is what makes 422 the answer whether the validator or
        // the database catches it (NFR-2).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        var (deliveryId, clientId) = await SeedDeliveryAsync(database, cancellationToken);

        await using var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);
        unitOfWork.Reviews.Add(NewReview(deliveryId, clientId, rating: 6));

        var failure = await Assert.ThrowsAsync<ValidationException>(
            () => unitOfWork.CommitAsync(cancellationToken));

        Assert.Equal(ErrorCode.PERSISTENCE_CHECK_VIOLATION, failure.Code);
        Assert.IsType<DbUpdateException>(failure.InnerException);
    }

    [Fact]
    public async Task Any_other_database_failure_is_rethrown_unchanged()
    {
        // A foreign-key violation (23503) is not a caller error, and dressing it as one would hide
        // a defect behind a 4xx. It stays a DbUpdateException and surfaces as the 500 envelope.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);
        unitOfWork.Reviews.Add(NewReview(deliveryId: 987_654, new ClientId(987_654), rating: 3));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(
            () => unitOfWork.CommitAsync(cancellationToken));

        Assert.IsNotAssignableFrom<DriveTrackException>(failure);

        var postgresFailure = await PostgresFailure.CapturedAsync(() => Task.FromException(failure));

        Assert.Equal("23503", postgresFailure.SqlState);
    }

    [Fact]
    public void PostgresException_is_named_in_exactly_one_source_file()
    {
        // AD-8's "one place in Infrastructure" is the claim; the scan is what keeps it true. Without
        // it, the second capability that needs a 409 translates its own violation inline and the
        // vocabulary quietly stops being closed.
        var root = RepositoryLayout.Source.FullName;

        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, root))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.Equals(TranslatorPath, StringComparison.Ordinal))
            .Where(relative => File.ReadLines(Path.Combine(root, relative)).Any(NamesPostgresException))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_translator_is_where_the_scan_says_it_is()
    {
        // Guards the scan above: a renamed or moved translator would make it vacuously true.
        Assert.True(File.Exists(Path.Combine(RepositoryLayout.Source.FullName, TranslatorPath)));
    }

    /// <summary>
    /// True when a line of code names the type. Comment lines are skipped, following the idiom
    /// <c>PersistenceContractTests</c> already uses for the ambient-clock scan: the prose explaining
    /// why a constraint exists is not a second translation site, and a rule that forbids discussing
    /// itself is a rule people work around by deleting the explanation.
    /// </summary>
    private static bool NamesPostgresException(string line)
    {
        var code = line.TrimStart();

        if (code.StartsWith("//", StringComparison.Ordinal)
            || code.StartsWith("*", StringComparison.Ordinal)
            || code.StartsWith("///", StringComparison.Ordinal))
        {
            return false;
        }

        return code.Contains("PostgresException", StringComparison.Ordinal);
    }

    private static async Task<(int DeliveryId, ClientId ClientId)> SeedDeliveryAsync(
        TestDatabase database,
        CancellationToken cancellationToken)
    {
        await using var context = await database.CreateContextAsync(cancellationToken);

        var client = await Seed.ClientAsync(context, cancellationToken);
        var delivery = await Seed.DeliveryAsync(context, cancellationToken, client.Id);

        return (delivery.Id, client.Id);
    }

    /// <summary>A proof for a delivery, with no assets: the unique index is on the parent row.</summary>
    private static ProofOfDelivery NewProof(int deliveryId) => new()
    {
        DeliveryId = deliveryId,
        RecipientName = "Translation probe",
        CaptureLocation = new Location(50.4501, 30.5234, Address: null, AddressResolvedAt: null),
        CapturedAt = Seed.Instant,
    };

    private static Review NewReview(int deliveryId, ClientId clientId, int rating) => new()
    {
        DeliveryId = deliveryId,
        ClientId = clientId,
        Rating = rating,
        Text = "Translation probe",
        CreatedAt = Seed.Instant,
    };

    private static bool IsBuildOutput(string path, string root)
    {
        var segments = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
}
