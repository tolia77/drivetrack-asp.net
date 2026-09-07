using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for DriveTrack, and under AD-20 the only authority for the
/// database schema. The model is empty at story 1.1 — story 1.2 adds the entities and their
/// database-level invariants. What is fixed here is the naming policy every later entity
/// inherits: tables and columns are snake_case (Consistency Conventions).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Registered as a convention rather than invoked at the end of OnModelCreating, so
        // configuration added by a later story cannot end up outside the naming policy.
        configurationBuilder.Conventions.Add(_ => new SnakeCaseNamingConvention());
    }
}
