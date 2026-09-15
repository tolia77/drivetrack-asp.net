using DriveTrack.Application.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DriveTrack.Infrastructure.Persistence;

/// <summary>
/// AD-8's single translation point: the one file in the system that knows what a PostgreSQL
/// SQLSTATE means. Reached only from <see cref="UnitOfWork.CommitAsync"/>, the single production
/// commit path, so a constraint the schema enforces (AD-20) surfaces as a typed failure with a
/// contract code instead of the raw 500 the original system returned.
/// <para>
/// <c>PostgresException</c> is named nowhere else under <c>src/</c>, and a source scan in
/// <c>ConstraintTranslationTests</c> keeps that true - "one place" is only a property of the
/// system while nothing has quietly added a second.
/// </para>
/// </summary>
internal static class PostgresConstraintTranslator
{
    /// <summary>
    /// SQLSTATE for a unique violation: a genuine conflict with another row. Exclusion
    /// constraints raise 23P01, not this, and the schema declares none - one arriving would fall
    /// to the default arm and surface as the 500 envelope, which is the honest answer until a
    /// migration adds one and a code is minted for it.
    /// </summary>
    private const string UniqueViolation = "23505";

    /// <summary>SQLSTATE for a check-constraint violation: a value the caller should not have sent.</summary>
    private const string CheckViolation = "23514";

    /// <summary>
    /// The unique index behind FR-123's "one proof per delivery".
    /// <para>
    /// Named here because the capability refuses the same thing twice and the two refusals must say
    /// the same sentence. <c>ProofOfDeliveryService.CaptureAsync</c> re-checks inside its writing
    /// transaction and throws <c>DELIVERY_PROOF_ALREADY_CAPTURED</c>; two captures closer together
    /// than a round trip both pass that check and the index refuses the loser instead. Without this
    /// arm that loser hears the generic <c>PERSISTENCE_UNIQUE_VIOLATION</c> — a different code, for
    /// the same 409, for the same reason, decided by timing the caller cannot see.
    /// </para>
    /// <para>
    /// One name and not a registry. Every other constraint keeps the generic answer, because a code
    /// per index would make the failure vocabulary a mirror of the schema — and AD-8's point is that
    /// it is a contract instead.
    /// </para>
    /// </summary>
    private const string ProofOfDeliveryPerDelivery = "ix_proof_of_deliveries_delivery_id";

    /// <summary>
    /// Maps a failed save onto the failure vocabulary, or returns it unchanged.
    /// </summary>
    /// <param name="exception">The exception EF raised from <c>SaveChangesAsync</c>.</param>
    /// <returns>
    /// A <see cref="ConflictException"/> for SQLSTATE 23505 - carrying the generic code, or the
    /// capability's own where the constraint is one it also states - a <see cref="ValidationException"/>
    /// for 23514, and <paramref name="exception"/> itself for anything else - a foreign-key violation
    /// (23503) or a serialization failure is not a caller error, and dressing it as one would hide
    /// a defect behind a 4xx. Those surface as the 500 envelope.
    /// </returns>
    public static Exception Translate(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var postgres = FindPostgresException(exception);

        if (postgres is null)
        {
            return exception;
        }

        // The constraint name goes in the exception Message and nowhere else. The wire message is
        // always the localized string for the code (NFR-3), so this detail reaches the log and
        // stops there - which is what makes "ix_reviews_delivery_id" diagnosable without ever
        // telling a client the shape of the schema.
        var constraint = postgres.ConstraintName ?? "(unnamed)";

        return postgres.SqlState switch
        {
            UniqueViolation => new ConflictException(
                UniqueViolationCode(constraint),
                "Unique constraint '" + constraint + "' rejected the write (SQLSTATE 23505).",
                exception),

            CheckViolation => new ValidationException(
                ErrorCode.PERSISTENCE_CHECK_VIOLATION,
                "Check constraint '" + constraint + "' rejected the write (SQLSTATE 23514).",
                [],
                exception),

            _ => exception,
        };
    }

    /// <summary>
    /// The contract code a 23505 answers with: the capability's own conflict where one constraint is
    /// the database's spelling of a rule the capability also states, and the generic one everywhere
    /// else.
    /// </summary>
    /// <param name="constraint">The constraint name PostgreSQL reported, or <c>(unnamed)</c>.</param>
    /// <returns>The code the envelope carries. Both are 409.</returns>
    private static ErrorCode UniqueViolationCode(string constraint) =>
        string.Equals(constraint, ProofOfDeliveryPerDelivery, StringComparison.Ordinal)
            ? ErrorCode.DELIVERY_PROOF_ALREADY_CAPTURED
            : ErrorCode.PERSISTENCE_UNIQUE_VIOLATION;

    /// <summary>
    /// Walks the cause chain for the PostgreSQL error. EF wraps it, and a retrying execution
    /// strategy can wrap it again, so the chain is walked rather than the direct inner checked.
    /// </summary>
    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
