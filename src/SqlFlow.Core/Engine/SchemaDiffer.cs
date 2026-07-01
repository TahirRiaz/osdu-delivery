using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>Pure schema-diff logic: compares the desired schema against the live target.</summary>
public static class SchemaDiffer
{
    public static SchemaDelta Diff(TableSchema desired, TableSchema? actual, SchemaEvolution evolve)
    {
        ArgumentNullException.ThrowIfNull(desired);

        if (actual is null)
        {
            return new SchemaDelta { CreateTable = true, ColumnsToAdd = desired.Columns };
        }

        var existing = actual.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = desired.Columns.Where(c => !existing.Contains(c.Name)).ToList();

        return evolve switch
        {
            SchemaEvolution.Create => new SchemaDelta(),
            SchemaEvolution.Widen => new SchemaDelta { ColumnsToAdd = missing },
            SchemaEvolution.Strict => missing.Count == 0
                ? new SchemaDelta()
                : throw new SchemaDriftException(desired, missing),
            _ => throw new ArgumentOutOfRangeException(nameof(evolve), evolve, "Unknown schema evolution policy."),
        };
    }
}
