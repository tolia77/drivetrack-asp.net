using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace DriveTrack.Infrastructure.Persistence;

/// <summary>
/// Rewrites every table, column, key, foreign key, index and check-constraint name in the
/// model to snake_case (Consistency Conventions).
/// <para>
/// It runs as a model-finalizing convention rather than as a statement at the end of
/// <c>OnModelCreating</c>, so it cannot be outrun by configuration a later story adds after
/// that call — finalizing conventions see the completed model whatever order it was built in.
/// </para>
/// </summary>
public sealed class SnakeCaseNamingConvention : IModelFinalizingConvention
{
    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entity in modelBuilder.Metadata.GetEntityTypes())
        {
            var tableName = entity.GetTableName();
            if (tableName is not null)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            var storeObject = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);

            foreach (var property in entity.GetProperties())
            {
                // Falls back to the property's own configured column name, never to the CLR
                // member name, so an explicit HasColumnName survives the rewrite.
                var columnName = storeObject is null
                    ? property.GetColumnName()
                    : property.GetColumnName(storeObject.Value) ?? property.GetColumnName();

                if (columnName is not null)
                {
                    property.SetColumnName(ToSnakeCase(columnName));
                }
            }

            foreach (var key in entity.GetKeys())
            {
                var name = key.GetName();
                if (name is not null)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var name = foreignKey.GetConstraintName();
                if (name is not null)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName();
                if (name is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }

            // Story 1.2 declares most of AD-20's invariants as check constraints; their names
            // are part of the schema and follow the same policy.
            foreach (var checkConstraint in entity.GetCheckConstraints().ToList())
            {
                var name = checkConstraint.Name;
                if (name is not null)
                {
                    checkConstraint.SetName(ToSnakeCase(name));
                }
            }
        }
    }

    /// <summary>
    /// Converts a PascalCase or camelCase identifier to snake_case, leaving an identifier
    /// that is already snake_case unchanged.
    /// </summary>
    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            if (current == '_')
            {
                builder.Append('_');
                continue;
            }

            if (char.IsUpper(current) && i > 0 && name[i - 1] != '_')
            {
                var previous = name[i - 1];
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);

                if (!char.IsUpper(previous) || nextIsLower)
                {
                    builder.Append('_');
                }
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}
