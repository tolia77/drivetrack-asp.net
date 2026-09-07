using DriveTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// The naming policy every entity from story 1.2 onward inherits (Consistency Conventions:
/// tables plural snake_case). The model AppDbContext ships today is empty, so nothing else in
/// the suite would notice if the policy stopped working; this builds a model with a
/// PascalCase entity and asserts the names it actually produces. No database is involved —
/// building a model does not open a connection.
/// </summary>
public class SnakeCaseNamingConventionTests
{
    private static readonly IModel Model = BuildModel();

    private static IEntityType Entity =>
        Model.FindEntityType(typeof(DeliveryProofRecord))
        ?? throw new InvalidOperationException("The test entity was not mapped.");

    [Fact]
    public void Table_name_is_snake_case()
    {
        Assert.Equal("delivery_proof_records", Entity.GetTableName());
    }

    [Theory]
    // Plain PascalCase.
    [InlineData(nameof(DeliveryProofRecord.DeliveryId), "delivery_id")]
    // An acronym must not become one underscore per letter.
    [InlineData(nameof(DeliveryProofRecord.ProofURL), "proof_url")]
    // An acronym followed by a word breaks before the last capital.
    [InlineData(nameof(DeliveryProofRecord.HTTPStatusCode), "http_status_code")]
    // Already snake_case, and explicitly configured with HasColumnName: left alone.
    [InlineData(nameof(DeliveryProofRecord.CapturedAt), "captured_at")]
    public void Column_name_is_snake_case(string propertyName, string expected)
    {
        var property = Entity.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(expected, property.GetColumnName());
    }

    [Fact]
    public void Index_name_is_snake_case()
    {
        var index = Assert.Single(Entity.GetIndexes());

        Assert.Equal("ix_delivery_proof_records_delivery_id", index.GetDatabaseName());
    }

    [Fact]
    public void Key_name_is_snake_case()
    {
        var key = Assert.Single(Entity.GetKeys());

        Assert.Equal("pk_delivery_proof_records", key.GetName());
    }

    [Fact]
    public void Check_constraint_name_is_snake_case()
    {
        var constraint = Assert.Single(Entity.GetCheckConstraints());

        Assert.Equal("ck_delivery_proof_records_http_status_code", constraint.Name);
    }

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=naming-probe;Username=probe;Password=probe")
            .Options;

        using var context = new NamingProbeDbContext(options);

        // The design-time model is the one migrations are generated from, and the only one
        // that carries check constraints.
        return context.GetService<IDesignTimeModel>().Model;
    }

    /// <summary>
    /// Derives from the shipped <see cref="AppDbContext"/> and adds only a probe entity, so the
    /// policy under test is the one AppDbContext itself registers. Registering the convention
    /// here instead would leave this suite green after AppDbContext stopped registering it.
    /// </summary>
    private sealed class NamingProbeDbContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options)
    {
        // Present so the table name is pluralised the way a real entity's DbSet does it.
        public DbSet<DeliveryProofRecord> DeliveryProofRecords => Set<DeliveryProofRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DeliveryProofRecord>(entity =>
            {
                entity.HasKey(e => e.Id).HasName("PK_DeliveryProofRecords");
                entity.Property(e => e.CapturedAt).HasColumnName("captured_at");
                entity.HasIndex(e => e.DeliveryId)
                    .HasDatabaseName("IX_DeliveryProofRecords_DeliveryId");
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_DeliveryProofRecords_HTTPStatusCode",
                    "\"HTTPStatusCode\" > 0"));
            });
        }
    }

    private sealed class DeliveryProofRecord
    {
        public int Id { get; set; }
        public int DeliveryId { get; set; }
        public string ProofURL { get; set; } = string.Empty;
        public int HTTPStatusCode { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
    }
}
