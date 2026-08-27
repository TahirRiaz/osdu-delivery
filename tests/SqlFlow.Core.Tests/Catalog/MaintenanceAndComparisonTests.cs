using SqlFlow.Core;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Maintenance;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The trust boundary for the two data-operations families: the standard warehouse maintenance actions and the
/// old-versus-new baseline comparison. Both run against live production estates from an interactive surface,
/// so what they REFUSE matters as much as what they measure. These tests pin the refusals and the contract
/// both the control plane and the node validate against.
/// </summary>
public sealed class MaintenanceAndComparisonTests
{
    private static MaintenanceRequest Request(
        string action, string? schema = null, string? objectName = null) => new()
    {
        Action = action,
        Scope = new MaintenanceScope { Database = "dwh", Schema = schema, ObjectName = objectName },
    };

    [Fact]
    public void EveryDescriptor_IsImplemented_AndEveryImplementationIsDescribed()
    {
        // The descriptors live in Core so the control plane can validate and list without referencing a
        // provider; the implementations live with the provider. This is what keeps the two from drifting.
        var described = MaintenanceActions.All.Select(a => a.Name).Order(StringComparer.Ordinal).ToArray();
        var implemented = SqlFlow.SqlServer.Maintenance.SqlServerMaintenanceActions.All
            .Select(a => a.Descriptor.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(described, implemented);
    }

    [Fact]
    public void EveryDescriptor_HasCoherentScopeBounds()
    {
        foreach (var action in MaintenanceActions.All)
        {
            Assert.True(
                action.WidestScope <= action.NarrowestScope,
                $"{action.Name} declares a widest scope narrower than its narrowest scope.");
            Assert.NotEmpty(action.SupportedKinds);
            Assert.False(string.IsNullOrWhiteSpace(action.Description));
        }
    }

    [Fact]
    public void Validate_RejectsAnUnknownAction_NamingTheValidSet()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => MaintenanceActions.Validate(Request("shrinkDatabase"), DataSourceKind.MSSQL));
        Assert.Contains("shrinkDatabase", ex.Message, StringComparison.Ordinal);
        Assert.Contains(MaintenanceActions.IndexFragmentation, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsANonSqlServerSource_BecauseEveryActionIsTSql()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => MaintenanceActions.Validate(Request(MaintenanceActions.TableSpace), DataSourceKind.PostgreSQL));
        Assert.Contains("T-SQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RefusesToNarrowAWarehouseHealthProbe_RatherThanReturningAPageAsTheWholePicture()
    {
        // The DMV probes rank and truncate before a schema is known, so a "schema-scoped" answer would be a
        // silently partial one. Refusing is the honest behaviour.
        var ex = Assert.Throws<SqlFlowException>(() => MaintenanceActions.Validate(
            Request(MaintenanceActions.MissingIndexes, schema: "arc"), DataSourceKind.MSSQL));
        Assert.Contains("cannot be narrowed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RequiresAnObjectScope_ForAnActionThatMeasuresOneTable()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => MaintenanceActions.Validate(Request(MaintenanceActions.DuplicateKeys), DataSourceKind.MSSQL));
        Assert.Contains("singleobject", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AcceptsAnObjectScopedDuplicateCheck()
    {
        MaintenanceActions.Validate(
            Request(MaintenanceActions.DuplicateKeys, "arc", "Ferde_Passeringer"), DataSourceKind.AZDB);
    }

    [Fact]
    public void Validate_RejectsAnObjectScopeWithoutItsSchema()
    {
        var request = Request(MaintenanceActions.DuplicateKeys) with
        {
            Scope = new MaintenanceScope { ObjectName = "Ferde_Passeringer" },
        };

        Assert.Throws<SqlFlowException>(() => MaintenanceActions.Validate(request, DataSourceKind.MSSQL));
    }

    [Fact]
    public void Validate_RejectsAnUnknownThreshold_SoATypoNeverRunsWithADefault()
    {
        var request = Request(MaintenanceActions.IndexFragmentation) with
        {
            Thresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["rebuildTreshold"] = 30, // deliberate misspelling
            },
        };

        var ex = Assert.Throws<SqlFlowException>(() => MaintenanceActions.Validate(request, DataSourceKind.MSSQL));
        Assert.Contains("rebuildTreshold", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rebuildThreshold", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsAThresholdOutsideItsDeclaredBounds()
    {
        var request = Request(MaintenanceActions.IndexFragmentation) with
        {
            Thresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["rebuildThreshold"] = 500 },
        };

        Assert.Throws<SqlFlowException>(() => MaintenanceActions.Validate(request, DataSourceKind.MSSQL));
    }

    [Fact]
    public void Validate_RejectsColumnsOnAnActionThatDoesNotTakeThem()
    {
        var request = Request(MaintenanceActions.TableSpace) with { Columns = ["Dato"] };
        var ex = Assert.Throws<SqlFlowException>(() => MaintenanceActions.Validate(request, DataSourceKind.MSSQL));
        Assert.Contains("does not work over a named column list", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsAColumnNameCarryingItsOwnQuoting()
    {
        var request = Request(MaintenanceActions.DuplicateKeys, "arc", "T") with { Columns = ["Dato]; DROP TABLE x --"] };
        Assert.Throws<SqlFlowException>(() => MaintenanceActions.Validate(request, DataSourceKind.MSSQL));
    }

    // ----------------------------------------------------------------------------------------------------
    // Baseline comparison.
    // ----------------------------------------------------------------------------------------------------

    private static readonly string[] Allowed = ["old-dwh-prod", "old-pre-prod", "OLDPROD"];

    private static BaselineComparisonRequest Comparison(BaselineCompareMode mode) => new()
    {
        Mode = mode,
        LinkedServer = "old-dwh-prod",
        BaselineDatabase = "dw-dwh-prod",
        Schema = "arc",
        ObjectName = mode == BaselineCompareMode.Inventory ? null : "Ferde_Passeringer",
    };

    [Fact]
    public void Comparison_RefusesALinkedServerThatIsNotAllowlisted()
    {
        var request = Comparison(BaselineCompareMode.Inventory) with { LinkedServer = "SOMEWHERE_ELSE" };
        var ex = Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
        Assert.Contains("not allowlisted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_RefusesEveryLinkedServerWhenNoneIsConfigured()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Comparison(BaselineCompareMode.Inventory).Validate([]));
        Assert.Contains("LinkedServers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_MatchesTheAllowlistCaseInsensitively()
    {
        var request = Comparison(BaselineCompareMode.Inventory) with { LinkedServer = "OLD-DWH-PROD" };
        Assert.Equal("OLD-DWH-PROD", request.Validate(Allowed).LinkedServer);
    }

    [Fact]
    public void Comparison_RequiresAnObjectForSchemaAndDataModes()
    {
        foreach (var mode in new[] { BaselineCompareMode.Schema, BaselineCompareMode.Data })
        {
            var request = Comparison(mode) with { ObjectName = null };
            Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
        }
    }

    [Fact]
    public void Comparison_RefusesAnObjectInInventoryMode_RatherThanIgnoringIt()
    {
        var request = Comparison(BaselineCompareMode.Inventory) with { ObjectName = "Ferde_Passeringer" };
        Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
    }

    [Fact]
    public void Comparison_RequiresALogicalKeyInDataMode()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Comparison(BaselineCompareMode.Data).Validate(Allowed));
        Assert.Contains("LOGICAL key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_AcceptsAnExpressionKeyAndDefaultsTheOldSideToTheNewSide()
    {
        var request = (Comparison(BaselineCompareMode.Data) with
        {
            KeyExpressions = ["Dato", "Sted", "CAST(Klokkeslett AS time)"],
            Where = "Dato >= '2025-01-01'",
        }).Validate(Allowed);

        Assert.Equal("arc", request.EffectiveBaselineSchema);
        Assert.Equal("Ferde_Passeringer", request.EffectiveBaselineObject);
        Assert.Equal(3, request.KeyExpressions.Count);
    }

    [Fact]
    public void Comparison_RefusesAnInjectedKeyExpression()
    {
        var request = Comparison(BaselineCompareMode.Data) with
        {
            KeyExpressions = ["Dato) DROP TABLE arc.Sales --"],
        };

        Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
    }

    [Fact]
    public void Comparison_RefusesAnInjectedFilter()
    {
        var request = Comparison(BaselineCompareMode.Data) with
        {
            KeyExpressions = ["Dato"],
            Where = "1=1; TRUNCATE TABLE arc.Sales",
        };

        Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
    }

    [Fact]
    public void Comparison_RefusesKeysAndFiltersOutsideDataMode()
    {
        var request = Comparison(BaselineCompareMode.Schema) with { KeyExpressions = ["Dato"] };
        var ex = Assert.Throws<SqlFlowException>(() => request.Validate(Allowed));
        Assert.Contains("data mode only", ex.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------
    // The compute payload that carries both families through the queue.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Payload_RoundTripsAMaintenanceRequestThroughTheQueue()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.DwhMaintenance,
            SourceRef = "${env:SQLFLOW_DWH}",
            Database = "dwh",
            MaintenanceAction = MaintenanceActions.IndexFragmentation,
            Thresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["minimumPages"] = 5000 },
        };

        var restored = ComputeTaskPayload.FromJson(payload.ToJson());
        var request = restored.ToMaintenanceRequest();

        Assert.Equal(MaintenanceActions.IndexFragmentation, request.Action);
        Assert.Equal(5000, request.Threshold("minimumPages", 0));
    }

    [Fact]
    public void Payload_RoundTripsAComparisonRequestThroughTheQueue()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.CompareBaseline,
            SourceRef = "${env:SQLFLOW_DWH}",
            CompareMode = BaselineCompareMode.Data,
            LinkedServer = "old-dwh-prod",
            BaselineDatabase = "dw-dwh-prod",
            Schema = "arc",
            ObjectName = "Ferde_Passeringer",
            BaselineObjectName = "Ferde_Passeringer_Old",
            KeyExpressions = ["Dato", "CAST(Klokkeslett AS time)"],
        };

        var restored = ComputeTaskPayload.FromJson(payload.ToJson());
        var request = restored.ToComparisonRequest(Allowed);

        Assert.Equal(BaselineCompareMode.Data, request.Mode);
        Assert.Equal("Ferde_Passeringer_Old", request.EffectiveBaselineObject);
        Assert.Equal(2, request.KeyExpressions.Count);
    }

    [Fact]
    public void Payload_ValidatesTheMaintenanceActionAtTheQueueBoundary()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.DwhMaintenance,
            SourceRef = "${env:SQLFLOW_DWH}",
            MaintenanceAction = "rebuildEverything",
        };

        Assert.Throws<SqlFlowException>(payload.Validate);
    }

    [Fact]
    public void Payload_RequiresAMaintenanceAction()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.DwhMaintenance,
            SourceRef = "${env:SQLFLOW_DWH}",
        };

        var ex = Assert.Throws<SqlFlowException>(payload.Validate);
        Assert.Contains("maintenanceAction", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_RequiresACompareMode()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.CompareBaseline,
            SourceRef = "${env:SQLFLOW_DWH}",
            LinkedServer = "OLDPROD",
            BaselineDatabase = "dw-dwh-prod",
            Schema = "arc",
        };

        var ex = Assert.Throws<SqlFlowException>(payload.Validate);
        Assert.Contains("compareMode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataOpsOperations_AreGateable_AndTheOthersAreNot()
    {
        Assert.True(ComputeOperations.IsDataOps(ComputeOperations.DwhMaintenance));
        Assert.True(ComputeOperations.IsDataOps(ComputeOperations.CompareBaseline));
        Assert.False(ComputeOperations.IsDataOps(ComputeOperations.ListObjects));
        Assert.False(ComputeOperations.IsDataOps(ComputeOperations.MissingIndexes));
    }

    [Fact]
    public void EveryWarehouseHealthOperation_IsAlsoAMaintenanceAction_SoThereIsOneCodePath()
    {
        foreach (var operation in ComputeOperations.All.Where(ComputeOperations.IsWarehouseHealth))
        {
            var descriptor = MaintenanceActions.Find(operation);
            Assert.NotNull(descriptor);
            Assert.True(descriptor.IsWarehouseHealthProbe);
        }
    }
}
