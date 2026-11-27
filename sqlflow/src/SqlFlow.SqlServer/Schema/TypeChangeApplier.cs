using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>The opt-ins a flow gives schema sync for changing the type of a column that already exists.</summary>
/// <param name="AllowTableRewrite">A widening that rewrites the table may run (<c>schema.allowTableRewrite</c>).</param>
/// <param name="ConvertTypes">A checked conversion may run (<c>schema.convertTypes</c>).</param>
public sealed record TypeChangePolicy(bool AllowTableRewrite, bool ConvertTypes)
{
    public bool Permits(ColumnTypeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.RequiresOptIn switch
        {
            null => true,
            ColumnTypeChange.AllowTableRewrite => AllowTableRewrite,
            ColumnTypeChange.ConvertTypes => ConvertTypes,
            _ => false,
        };
    }
}

/// <summary>
/// Carries out and reports the type changes schema sync planned for one target: finishes or rolls back a safe rewrite an
/// earlier run left, applies the shadow rewrites the flow opted into (after checking a checked conversion's data), and
/// describes every change, applied or not, as a <see cref="TypeChangeReport"/>. The in-place part (one ALTER COLUMN in
/// metadata) runs with the rest of schema sync's DDL in <see cref="SchemaSyncService.EvolveAsync"/>.
/// </summary>
public static class TypeChangeApplier
{
    /// <summary>Finishes the rewrites an earlier run left on the table when this run still wants exactly that change and
    /// is allowed it, and rolls the others back. Returns what was done.</summary>
    public static async Task<IReadOnlyList<TypeChangeReport>> ResumeAsync(
        SchemaSyncService schemaSync, string connectionString, RelationalObject target, IReadOnlyList<SqlColumn> desired,
        IReadOnlySet<string> keyColumns, TypeChangePolicy policy, RetypeOptions options, Action<string, string> trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schemaSync);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trace);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var pending = await ColumnRetyper.FindPendingAsync(connection, target, ct).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return [];
        }

        var context = new RetypeContext(connection, target, options, trace);
        var plan = await schemaSync.PlanAsync(connection, target, desired, keyColumns, ct).ConfigureAwait(false);
        var dependencies = await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false);
        var reports = new List<TypeChangeReport>();
        foreach (var left in pending)
        {
            var change = plan.TypeChanges.FirstOrDefault(c =>
                string.Equals(c.Column, left.Column, StringComparison.OrdinalIgnoreCase)
                && c.Mechanism == TypeChangeMechanism.ShadowRewrite
                && string.Equals(c.ToType.Render(), left.ToType, StringComparison.OrdinalIgnoreCase));

            if (change is null || !policy.Permits(change))
            {
                await ColumnRetyper.RollBackAsync(context, left, ct).ConfigureAwait(false);
                var reason = change is null
                    ? $"the source no longer asks for [{left.Column}] as {left.ToType}"
                    : $"the flow no longer sets {change.RequiresOptIn}";
                reports.Add(new TypeChangeReport
                {
                    Layer = TypeChangeReport.TargetLayer,
                    Table = target.QualifiedName,
                    Column = left.Column,
                    FromType = plan.TypeChanges.FirstOrDefault(c => string.Equals(c.Column, left.Column, StringComparison.OrdinalIgnoreCase))?.FromType.Render() ?? string.Empty,
                    ToType = left.ToType,
                    Tier = change?.Tier ?? TypeChangeTier.CheckedConversion,
                    Status = TypeChangeStatus.RolledBack,
                    RewritesTable = true,
                    Message = $"An earlier run's conversion of {target.QualifiedName}.[{left.Column}] to {left.ToType} was left unfinished and has " +
                              $"been rolled back because {reason}; the column keeps its type and every value.",
                });
                continue;
            }

            if (!left.TriggerIntact)
            {
                // Writes may have reached the column without reaching the shadow: start over from a clean table.
                await ColumnRetyper.RollBackAsync(context, left, ct).ConfigureAwait(false);
                reports.Add(await ApplyOneAsync(context, target, change, dependencies, resume: false, ct).ConfigureAwait(false));
                continue;
            }

            var report = await ApplyOneAsync(context, target, change, dependencies, resume: true, ct).ConfigureAwait(false);
            reports.Add(report.Status == TypeChangeStatus.Applied
                ? report with
                {
                    Status = TypeChangeStatus.Resumed,
                    Message = $"An earlier run's conversion of {target.QualifiedName}.[{change.Column}] to {change.ToType.Render()} was finished. " + report.Message,
                }
                : report);
        }

        return reports;
    }

    /// <summary>
    /// Applies the shadow rewrites of <paramref name="timing"/> the flow opted into, each after its data check, and
    /// reports every shadow rewrite of that timing (applied, pending an opt-in, or refused by the check).
    /// </summary>
    public static async Task<IReadOnlyList<TypeChangeReport>> ApplyAsync(
        SchemaSyncService schemaSync, string connectionString, RelationalObject target, IReadOnlyList<SqlColumn> desired,
        IReadOnlySet<string> keyColumns, TypeChangeTiming timing, TypeChangePolicy policy, RetypeOptions options,
        Action<string, string> trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schemaSync);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var plan = await schemaSync.PlanAsync(connection, target, desired, keyColumns, ct).ConfigureAwait(false);
        var shadow = plan.TypeChanges.Where(c => c.Mechanism == TypeChangeMechanism.ShadowRewrite && c.Timing == timing).ToList();
        if (shadow.Count == 0)
        {
            return [];
        }

        var context = new RetypeContext(connection, target, options, trace);
        var dependencies = await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false);
        var reports = new List<TypeChangeReport>();
        foreach (var change in shadow)
        {
            if (!policy.Permits(change))
            {
                reports.Add(NotPermitted(change, target, dependencies.RowCount));
                continue;
            }

            if (change.Tier == TypeChangeTier.CheckedConversion)
            {
                var check = await ColumnRetyper.CheckAsync(context, change, ct).ConfigureAwait(false);
                if (check.Failing > 0)
                {
                    reports.Add(Refused(change, target, check));
                    continue;
                }
            }

            reports.Add(await ApplyOneAsync(context, target, change, dependencies, resume: false, ct).ConfigureAwait(false));
            dependencies = await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false);
        }

        return reports;
    }

    /// <summary>Describes the changes schema sync did not hand to the safe rewrite: the in-place ones (applied, or
    /// pending <c>schema.allowTableRewrite</c> when they rewrite) and the tier 3 ones, with the merging keys of a key
    /// column change counted. <paramref name="appliedInPlace"/> names the columns the DDL batch altered.</summary>
    public static async Task<IReadOnlyList<TypeChangeReport>> DescribeAsync(
        string connectionString, RelationalObject target, EvolutionPlan plan, IReadOnlySet<string> appliedInPlace,
        RetypeOptions options, Action<string, string> trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(appliedInPlace);

        var described = plan.TypeChanges.Where(c => c.Mechanism != TypeChangeMechanism.ShadowRewrite).ToList();
        if (described.Count == 0)
        {
            return [];
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var context = new RetypeContext(connection, target, options, trace);
        var rows = (await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false)).RowCount;
        var reports = new List<TypeChangeReport>();
        foreach (var change in described)
        {
            if (change.Mechanism is TypeChangeMechanism.InPlace or TypeChangeMechanism.InPlaceRewrite)
            {
                reports.Add(appliedInPlace.Contains(change.Column)
                    ? TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Applied,
                        $"{change.Describe(target.QualifiedName)}: applied in place. {Capitalize(change.Reason)}. {TypeChangeReport.Downstream}", rows)
                    : NotPermitted(change, target, rows));
                continue;
            }

            if (change.IsNarrowing)
            {
                reports.Add(TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.KeptWider,
                    $"{change.Describe(target.QualifiedName)}: kept. {Capitalize(change.Reason)}.", rows));
                continue;
            }

            var message = $"{change.Describe(target.QualifiedName)}: refused, the engine does not apply a tier 3 change. {Capitalize(change.Reason)}.";
            if (change.IsKey && TypeChangeClassifier.IsCheckedPair(change.FromType, change.SourceType))
            {
                var merges = await ColumnRetyper.KeyMergesAsync(context, change, ct).ConfigureAwait(false);
                message += merges.MergedKeys == 0
                    ? $" No two stored keys would become one as {change.SourceType.Render()}."
                    : $" {merges.MergedKeys.ToString(CultureInfo.InvariantCulture)} converted key(s) would each stand for several stored keys " +
                      $"(for example {DescribeSample(merges.Sample)}).";
            }

            reports.Add(TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Manual,
                message + " Manual steps: " + string.Join(" ", change.ManualSteps), rows));
        }

        return reports;
    }

    private static async Task<TypeChangeReport> ApplyOneAsync(
        RetypeContext context, RelationalObject target, ColumnTypeChange change, TableDependencies dependencies, bool resume, CancellationToken ct)
    {
        try
        {
            await ColumnRetyper.ApplyAsync(context, change, dependencies, resume, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 1505 or 2601 or 2627)
        {
            // A unique index over the new type found two rows equal: the conversion merges values the index keeps apart.
            await RollBackAsync(context, change, ct).ConfigureAwait(false);
            return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Refused,
                $"{change.Describe(target.QualifiedName)}: refused and rolled back, the table is as it was. A unique index over [{change.Column}] " +
                $"finds duplicate values once they are {change.ToType.Render()}: {SecretHygiene.RedactedMessage(ex)}",
                dependencies.RowCount);
        }

        var failedViews = await ColumnRetyper.RefreshViewsAsync(context, dependencies, ct).ConfigureAwait(false);
        var message = $"{change.Describe(target.QualifiedName)}: applied by the safe rewrite; every row, its identity, system dates, indexes " +
                      $"and statistics were kept. {Capitalize(change.Reason)}. {TypeChangeReport.Downstream}";
        if (failedViews.Count > 0)
        {
            message += " These views over the table could not be refreshed and need attention: " + string.Join("; ", failedViews) + ".";
        }

        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Applied, message, dependencies.RowCount);
    }

    private static async Task RollBackAsync(RetypeContext context, ColumnTypeChange change, CancellationToken ct)
    {
        var left = (await ColumnRetyper.FindPendingAsync(context.Connection, context.Table, ct).ConfigureAwait(false))
            .FirstOrDefault(p => string.Equals(p.Column, change.Column, StringComparison.OrdinalIgnoreCase));
        if (left is not null)
        {
            await ColumnRetyper.RollBackAsync(context, left, ct).ConfigureAwait(false);
        }
    }

    private static TypeChangeReport NotPermitted(ColumnTypeChange change, RelationalObject target, long rows)
    {
        if (change.IsNarrowing)
        {
            return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.KeptWider,
                $"{change.Describe(target.QualifiedName)}: kept wider, which holds every value of the source; set {change.RequiresOptIn} to narrow it " +
                "after a check that every stored value fits.", rows);
        }

        var what = change.RequiresOptIn == ColumnTypeChange.ConvertTypes
            ? "checks every stored value converts without loss, then converts the column by the safe rewrite"
            : "rewrites the column by the safe rewrite, which blocks readers only for the final swap";
        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Pending,
            $"{change.Describe(target.QualifiedName)}: pending, the table is untouched. {Capitalize(change.Reason)}. Set {change.RequiresOptIn}: true " +
            $"on the flow and the next run {what}.", rows);
    }

    private static TypeChangeReport Refused(ColumnTypeChange change, RelationalObject target, StoredValueCheck check)
    {
        var count = check.Failing.ToString(CultureInfo.InvariantCulture);
        var next = change.Timing == TypeChangeTiming.AfterLoad
            ? "Render them in the source as values of the new type (an expression in its transform, such as NULLIF(@ColName, 'NA') or a " +
              "CONVERT with the style the text is written in), then run this flow once with a full load: the load writes the source's values " +
              "into the column first, and the conversion then checks those. Or correct them in the table."
            : $"Declare a type in the source whose values fit (a longer {change.ToType.BaseType}), or correct them in the table.";
        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Refused,
            $"{change.Describe(target.QualifiedName)}: refused, the table is untouched. {count} of {check.Rows.ToString(CultureInfo.InvariantCulture)} " +
            $"stored value(s) do not convert to {change.ToType.Render()} without loss (for example {DescribeSample(check.Sample)}). {next}",
            check.Rows, check.Failing, check.Sample);
    }

    internal static string DescribeSample(IReadOnlyList<TypeChangeValueSample> sample)
        => sample.Count == 0
            ? "none sampled"
            : string.Join(", ", sample.Select(s => $"'{s.Value}' in {s.Rows.ToString(CultureInfo.InvariantCulture)} row(s)"));

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
