using Npgsql;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// Captures the PostgreSQL error behind a failed write.
/// <para>
/// EF wraps it in a <c>DbUpdateException</c>, so asserting on the outer type would prove only
/// that something went wrong. These tests exist to prove <em>which</em> constraint refused the
/// row, which is the difference between "the database said no" and "the database enforces the
/// rule the requirement names".
/// </para>
/// </summary>
internal static class PostgresFailure
{
    /// <summary>Runs the write, expects it to fail, and returns the PostgreSQL error.</summary>
    public static async Task<PostgresException> CapturedAsync(Func<Task> write)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(write);

        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new InvalidOperationException(
            $"Expected a PostgresException somewhere in the chain, but got: {thrown}");
    }

    /// <summary>Asserts the write failed on a specific constraint with a specific SQLSTATE.</summary>
    public static async Task RejectedByAsync(
        Func<Task> write,
        string sqlState,
        string constraintName)
    {
        var failure = await CapturedAsync(write);

        Assert.Equal(sqlState, failure.SqlState);
        Assert.Equal(constraintName, failure.ConstraintName);
    }
}
