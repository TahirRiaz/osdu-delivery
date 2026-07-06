using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Core.Lineage;

namespace SqlFlow.Lineage.Extraction;

/// <summary>
/// The operation-wise T-SQL lineage extractor: statement-driven destructuring of ScriptDom's typed AST,
/// modeled on the DeltaForge extractor. There is deliberately NO flat fragment visitor at the statement
/// level: a visitor firing on every table name cannot tell a MERGE target from its USING source, an UPDATE
/// alias target from a joined read, or a CTE from a real object. Context flows down instead: each statement
/// kind has an explicit handler that knows which positions write and which read, with CTE scoping, subquery
/// depth, USE-database tracking, and a recursion guard carried in <c>Scope</c>. Statement kinds
/// without a handler become warnings with their type name, never silent gaps. One extractor call per script,
/// no shared state: thread-safe by construction.
/// </summary>
public static class TSqlLineageExtractor
{
    /// <summary>The recursion ceiling (the DeltaForge guard): beyond this the SQL is adversarial, not real.</summary>
    public const int MaxDepth = 64;

    /// <summary>Extracts the dependencies of one T-SQL script (any number of batches/statements).
    /// <paramref name="context"/> names the script in warnings (a module name, a trace step).
    /// <paramref name="defaultDatabase"/> qualifies 1/2-part names when the script's home database is known
    /// (a harvested module's database); an explicit database part or USE statement always wins.</summary>
    public static ScriptDependencies Extract(string sql, string? context = null, string? defaultDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var dependencies = new ScriptDependencies();
        var label = string.IsNullOrWhiteSpace(context) ? "script" : context;

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        foreach (var error in errors)
        {
            dependencies.Warnings.Add($"{label}: parse error at line {error.Line}: {error.Message}");
        }

        if (fragment is not TSqlScript script)
        {
            if (errors.Count == 0)
            {
                dependencies.Warnings.Add($"{label}: the parser produced no script.");
            }

            return dependencies;
        }

        var walker = new Walker(dependencies, label, defaultDatabase);
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                walker.ExtractStatement(statement);
            }
        }

        return dependencies;
    }

    /// <summary>The per-script walk state: never shared, never static.</summary>
    private sealed class Walker
    {
        private readonly ScriptDependencies _deps;
        private readonly string _label;
        private readonly List<HashSet<string>> _cteScopes = [];
        private readonly HashSet<string> _tableVariables = new(StringComparer.OrdinalIgnoreCase);
        private string? _currentDatabase;
        private TableName? _triggerTarget;
        private int _depth;

        /// <summary>Pair identities already recorded, so the pair list stays deduplicated at the source.</summary>
        private readonly HashSet<(string Source, string Target)> _pairKeys = [];

        /// <summary>The reads of the statement currently being extracted, WITH their dependency kind: the
        /// self-pair exemption is computed per statement (the reference scopes it that way), so a subquery
        /// read elsewhere in the script cannot legitimize a direct self-feed here.</summary>
        private List<(TableName Table, DependencyKind Kind)>? _statementReads;

        public Walker(ScriptDependencies deps, string label, string? defaultDatabase)
        {
            _deps = deps;
            _label = label;
            _currentDatabase = defaultDatabase;
        }

        public void ExtractStatement(TSqlStatement statement)
        {
            if (!EnterDepth(statement))
            {
                return;
            }

            try
            {
                switch (statement)
                {
                    case SelectStatement select:
                        ExtractSelect(select);
                        break;
                    case InsertStatement insert:
                        ExtractInsert(insert);
                        break;
                    case UpdateStatement update:
                        ExtractUpdate(update);
                        break;
                    case DeleteStatement delete:
                        ExtractDelete(delete);
                        break;
                    case MergeStatement merge:
                        ExtractMerge(merge);
                        break;
                    case ViewStatementBody view:
                        // CREATE, ALTER, and CREATE OR ALTER VIEW all carry the same body shape.
                        ExtractViewBody(view, view.SchemaObjectName, view.SelectStatement,
                            view is AlterViewStatement ? TableOperation.Alter : TableOperation.Create);
                        break;
                    case CreateTableStatement createTable:
                        ExtractCreateTable(createTable);
                        break;
                    case TruncateTableStatement truncate:
                        Outbound(TableName.From(truncate.TableName, _currentDatabase), TableOperation.Truncate);
                        break;
                    case DropObjectsStatement drop:
                        foreach (var name in drop.Objects)
                        {
                            Outbound(TableName.From(name, _currentDatabase), TableOperation.Drop);
                        }

                        break;
                    case AlterTableSwitchStatement switchStatement:
                        ExtractPartitionSwitch(switchStatement);
                        break;
                    case AlterTableStatement alterTable:
                        Outbound(TableName.From(alterTable.SchemaObjectName, _currentDatabase), TableOperation.Alter);
                        break;
                    case DeclareCursorStatement cursor:
                        // The cursor's SELECT is a real read; open/fetch/close are lineage-neutral.
                        if (cursor.CursorDefinition?.Select is { } cursorSelect)
                        {
                            ExtractSelect(cursorSelect);
                        }

                        break;
                    case OpenCursorStatement or FetchCursorStatement or CloseCursorStatement or DeallocateCursorStatement:
                        break;
                    case ExecuteStatement execute:
                        ExtractExecute(execute);
                        break;
                    case ProcedureStatementBody procedure:
                        // CREATE/ALTER PROCEDURE: the module itself is created; its body's work is the
                        // module's lineage.
                        Outbound(TableName.From(procedure.ProcedureReference.Name, _currentDatabase),
                            statement is AlterProcedureStatement ? TableOperation.Alter : TableOperation.Create);
                        ExtractStatementList(procedure.StatementList);
                        break;
                    case FunctionStatementBody function:
                        ExtractFunction(function);
                        break;
                    case TriggerStatementBody trigger:
                        ExtractTrigger(trigger);
                        break;
                    case UseStatement use:
                        _currentDatabase = use.DatabaseName?.Value;
                        break;
                    case BeginEndBlockStatement block:
                        ExtractStatementList(block.StatementList);
                        break;
                    case IfStatement conditional:
                        WalkExpressionSubqueries(conditional.Predicate);
                        ExtractStatement(conditional.ThenStatement);
                        if (conditional.ElseStatement is not null)
                        {
                            ExtractStatement(conditional.ElseStatement);
                        }

                        break;
                    case WhileStatement loop:
                        WalkExpressionSubqueries(loop.Predicate);
                        ExtractStatement(loop.Statement);
                        break;
                    case TryCatchStatement tryCatch:
                        ExtractStatementList(tryCatch.TryStatements);
                        ExtractStatementList(tryCatch.CatchStatements);
                        break;
                    case DeclareTableVariableStatement declareTable:
                        _tableVariables.Add(declareTable.Body.VariableName.Value);
                        break;
                    case DeclareVariableStatement declare:
                        foreach (var element in declare.Declarations)
                        {
                            WalkExpressionSubqueries(element.Value);
                        }

                        break;
                    case SetVariableStatement setVariable:
                        WalkExpressionSubqueries(setVariable.Expression);
                        break;
                    case ReturnStatement ret:
                        WalkExpressionSubqueries(ret.Expression);
                        break;
                    case BulkInsertStatement bulk:
                        Outbound(TableName.From(bulk.To, _currentDatabase), TableOperation.BulkInsert);
                        break;
                    case PrintStatement or SetCommandStatement or PredicateSetStatement or BeginTransactionStatement
                        or CommitTransactionStatement or RollbackTransactionStatement or SaveTransactionStatement
                        or ThrowStatement or RaiseErrorStatement or WaitForStatement or BreakStatement
                        or ContinueStatement or GoToStatement or LabelStatement or CreateIndexStatement
                        or AlterIndexStatement or DropIndexStatement or CreateStatisticsStatement
                        or UpdateStatisticsStatement or GrantStatement or RevokeStatement or DenyStatement
                        or SetRowCountStatement or CreateSchemaStatement or ExecuteAsStatement or RevertStatement:
                        // Deliberately lineage-neutral: indexes, statistics, permissions, transactions, and
                        // session state move no data between objects.
                        break;
                    default:
                        _deps.Warnings.Add(
                            $"{_label}: unhandled statement '{statement.GetType().Name}' at line {statement.StartLine}; its lineage (if any) is not captured.");
                        break;
                }
            }
            finally
            {
                _depth--;
            }
        }

        private void ExtractStatementList(StatementList? list)
        {
            if (list is null)
            {
                return;
            }

            foreach (var statement in list.Statements)
            {
                ExtractStatement(statement);
            }
        }

        private void ExtractStatementList(IList<TSqlStatement> statements)
        {
            foreach (var statement in statements)
            {
                ExtractStatement(statement);
            }
        }

        // ---- DML -------------------------------------------------------------------------------------

        private void ExtractSelect(SelectStatement select)
        {
            WithStatementFrame(select.WithCtesAndXmlNamespaces, reads =>
            {
                WalkQuery(select.QueryExpression, subqueryDepth: 0);

                if (select.Into is not null)
                {
                    // SELECT INTO is T-SQL's CTAS: one CreateAs operation (the DeltaForge taxonomy), and
                    // script-internal staging for the data-flow pairing of everything after it.
                    var target = TableName.From(select.Into, _currentDatabase);
                    Outbound(target, TableOperation.CreateAs);
                    _deps.CtasCreated.Add(target.Key);
                    RecordMovement(target, reads);
                    RecordCreatedObject(target, LineageNodeKind.Table, select, columns: []);
                }
            });
        }

        /// <summary>CREATE TABLE, including the Synapse/Fabric CTAS form whose body is a real read.</summary>
        private void ExtractCreateTable(CreateTableStatement createTable)
        {
            var target = TableName.From(createTable.SchemaObjectName, _currentDatabase);
            if (createTable.SelectStatement is { } ctasBody)
            {
                WithStatementFrame(ctasBody.WithCtesAndXmlNamespaces, reads =>
                {
                    WalkQuery(ctasBody.QueryExpression, subqueryDepth: 0);
                    Outbound(target, TableOperation.CreateAs);
                    _deps.CtasCreated.Add(target.Key);
                    RecordMovement(target, reads);
                });
                RecordCreatedObject(target, LineageNodeKind.Table, createTable, columns: []);
                return;
            }

            Outbound(target, TableOperation.Create);
            RecordCreatedObject(target, LineageNodeKind.Table, createTable, ReadColumnDefinitions(createTable.Definition));
        }

        /// <summary>ALTER TABLE ... SWITCH [PARTITION n] TO target: a metadata-speed data movement, still a
        /// movement: rows leave the source and land in the target.</summary>
        private void ExtractPartitionSwitch(AlterTableSwitchStatement switchStatement)
        {
            var source = TableName.From(switchStatement.SchemaObjectName, _currentDatabase);
            var target = TableName.From(switchStatement.TargetTable, _currentDatabase);

            _deps.AddInbound(source, TableOperation.Read, DependencyKind.Direct);
            _statementReads?.Add((source, DependencyKind.Direct));
            Outbound(source, TableOperation.Delete);
            Outbound(target, TableOperation.Insert);

            if (!_deps.LocalDeps.TryGetValue(target.Key, out var sources))
            {
                _deps.LocalDeps[target.Key] = [source.Key];
            }
            else if (!sources.Contains(source.Key, StringComparer.Ordinal))
            {
                sources.Add(source.Key);
            }

            if (source.PartCount >= 3 && target.PartCount >= 3)
            {
                AddPair(source, target);
            }
        }

        private void ExtractInsert(InsertStatement insert)
        {
            WithStatementFrame(insert.WithCtesAndXmlNamespaces, reads =>
            {
                var specification = insert.InsertSpecification;
                var target = ResolveTarget(specification.Target, fromClause: null);

                switch (specification.InsertSource)
                {
                    case SelectInsertSource selectSource:
                        WalkQuery(selectSource.Select, subqueryDepth: 0);
                        break;
                    case ValuesInsertSource valuesSource:
                        foreach (var row in valuesSource.RowValues)
                        {
                            foreach (var value in row.ColumnValues)
                            {
                                WalkExpressionSubqueries(value);
                            }
                        }

                        break;
                    case ExecuteInsertSource executeSource:
                        // INSERT ... EXEC: the procedure's result set feeds the target; the procedure's own
                        // sources are its module's lineage, resolved when the module is harvested.
                        ExtractExecuteSpecification(executeSource.Execute);
                        break;
                    default:
                        _deps.Warnings.Add(
                            $"{_label}: unhandled INSERT source '{specification.InsertSource?.GetType().Name}' at line {insert.StartLine}.");
                        break;
                }

                if (target is not null)
                {
                    Outbound(target, TableOperation.Insert);
                    RecordMovement(target, reads);
                }

                ExtractOutputInto(specification, reads);
            });
        }

        private void ExtractUpdate(UpdateStatement update)
        {
            WithStatementFrame(update.WithCtesAndXmlNamespaces, reads =>
            {
                var specification = update.UpdateSpecification;

                // The FROM clause is read FIRST so an aliased target (UPDATE a ... FROM dbo.T AS a) can be
                // resolved against it, the T-SQL twin of DeltaForge's peek_resolve_update_alias.
                if (specification.FromClause is not null)
                {
                    foreach (var reference in specification.FromClause.TableReferences)
                    {
                        WalkTableReference(reference, subqueryDepth: 0);
                    }
                }

                foreach (var clause in specification.SetClauses)
                {
                    if (clause is AssignmentSetClause assignment)
                    {
                        WalkExpressionSubqueries(assignment.NewValue);
                    }
                }

                WalkExpressionSubqueries(specification.WhereClause?.SearchCondition);

                var target = ResolveTarget(specification.Target, specification.FromClause);
                if (target is not null)
                {
                    Outbound(target, TableOperation.Update);

                    // UPDATE is in-place: no data-flow pairs and no local-deps movement (the DeltaForge
                    // rule), EXCEPT the change-feed carve-out: an update fed by a change feed is a real
                    // propagation and must produce its edge.
                    RecordInPlaceMovement(target, reads);
                }

                ExtractOutputInto(specification, reads);
            });
        }

        private void ExtractDelete(DeleteStatement delete)
        {
            WithStatementFrame(delete.WithCtesAndXmlNamespaces, reads =>
            {
                var specification = delete.DeleteSpecification;

                if (specification.FromClause is not null)
                {
                    foreach (var reference in specification.FromClause.TableReferences)
                    {
                        WalkTableReference(reference, subqueryDepth: 0);
                    }
                }

                WalkExpressionSubqueries(specification.WhereClause?.SearchCondition);

                var target = ResolveTarget(specification.Target, specification.FromClause);
                if (target is not null)
                {
                    Outbound(target, TableOperation.Delete);

                    // DELETE is in-place: pairs only through the change-feed carve-out (a propagating
                    // delete reading an upstream feed is lineage; a plain delete is not).
                    RecordInPlaceMovement(target, reads);
                }

                ExtractOutputInto(specification, reads);
            });
        }

        private void ExtractMerge(MergeStatement merge)
        {
            WithStatementFrame(merge.WithCtesAndXmlNamespaces, reads =>
            {
                var specification = merge.MergeSpecification;

                WalkTableReference(specification.TableReference, subqueryDepth: 0);
                WalkExpressionSubqueries(specification.SearchCondition);

                foreach (var clause in specification.ActionClauses)
                {
                    switch (clause.Action)
                    {
                        case UpdateMergeAction updateAction:
                            foreach (var set in updateAction.SetClauses)
                            {
                                if (set is AssignmentSetClause assignment)
                                {
                                    WalkExpressionSubqueries(assignment.NewValue);
                                }
                            }

                            break;
                        case InsertMergeAction insertAction:
                            foreach (var value in insertAction.Source?.RowValues.SelectMany(r => r.ColumnValues) ?? [])
                            {
                                WalkExpressionSubqueries(value);
                            }

                            break;
                    }
                }

                var target = ResolveTarget(specification.Target, fromClause: null);
                if (target is not null)
                {
                    Outbound(target, TableOperation.Merge);
                    RecordMovement(target, reads);
                }

                ExtractOutputInto(specification, reads);
            });
        }

        /// <summary>OUTPUT ... INTO t: a second write target fed by the same statement.</summary>
        private void ExtractOutputInto(DataModificationSpecification specification, List<(TableName Table, DependencyKind Kind)> reads)
        {
            if (specification.OutputIntoClause?.IntoTable is not { } intoTable)
            {
                return;
            }

            var target = ResolveTarget(intoTable, fromClause: null);
            if (target is not null)
            {
                Outbound(target, TableOperation.Insert);
                RecordMovement(target, reads);
            }
        }

        // ---- Modules ----------------------------------------------------------------------------------

        private void ExtractViewBody(
            TSqlFragment statement, SchemaObjectName name, SelectStatement body, TableOperation operation)
        {
            var view = TableName.From(name, _currentDatabase);
            Outbound(view, operation);

            WithStatementFrame(body.WithCtesAndXmlNamespaces, reads =>
            {
                WalkQuery(body.QueryExpression, subqueryDepth: 0);
                RecordMovement(view, reads);
            });

            // The whole CREATE/ALTER VIEW statement is the view's generating script; an ALTER only re-defines
            // the body, so treat both as the current definition.
            RecordCreatedObject(view, LineageNodeKind.View, statement, columns: []);
        }

        private void ExtractFunction(FunctionStatementBody function)
        {
            Outbound(TableName.From(function.Name, _currentDatabase),
                function is AlterFunctionStatement ? TableOperation.Alter : TableOperation.Create);

            if (function.ReturnType is SelectFunctionReturnType inline)
            {
                // An inline TVF is a parameterized view: its body is its lineage.
                WithStatementFrame(inline.SelectStatement.WithCtesAndXmlNamespaces,
                    _ => WalkQuery(inline.SelectStatement.QueryExpression, subqueryDepth: 0));
            }

            ExtractStatementList(function.StatementList);
        }

        private void ExtractTrigger(TriggerStatementBody trigger)
        {
            Outbound(TableName.From(trigger.Name, _currentDatabase),
                trigger is AlterTriggerStatement ? TableOperation.Alter : TableOperation.Create);

            // Inside a DML trigger's body, the inserted/deleted pseudo-tables ARE the parent object, the
            // T-SQL change-feed surface. DDL and LOGON triggers have no parent table (TriggerObject.Name is
            // null there): their bodies still extract, just without a change-feed target.
            var previousTarget = _triggerTarget;
            _triggerTarget = trigger.TriggerObject?.Name is { } parent ? TableName.From(parent, _currentDatabase) : null;
            try
            {
                ExtractStatementList(trigger.StatementList);
            }
            finally
            {
                _triggerTarget = previousTarget;
            }
        }

        private void ExtractExecute(ExecuteStatement execute)
            => ExtractExecuteSpecification(execute.ExecuteSpecification);

        private void ExtractExecuteSpecification(ExecuteSpecification? specification)
        {
            switch (specification?.ExecutableEntity)
            {
                case ExecutableProcedureReference procedure
                    when procedure.ProcedureReference?.ProcedureReference?.Name is { } name:
                {
                    var procedureName = TableName.From(name, _currentDatabase);
                    if (string.Equals(procedureName.Name, "sp_executesql", StringComparison.OrdinalIgnoreCase))
                    {
                        // Dynamic SQL is statically undecidable; flagged, never guessed (the DeltaForge rule).
                        _deps.Warnings.Add($"{_label}: dynamic SQL via sp_executesql; its lineage is not statically derivable.");
                        return;
                    }

                    _deps.AddInbound(procedureName, TableOperation.Execute, DependencyKind.Direct);
                    break;
                }

                case ExecutableProcedureReference:
                    _deps.Warnings.Add($"{_label}: EXEC of a procedure held in a variable; its lineage is not statically derivable.");
                    break;
                case ExecutableStringList:
                    _deps.Warnings.Add($"{_label}: dynamic SQL via EXEC(...); its lineage is not statically derivable.");
                    break;
                default:
                    _deps.Warnings.Add($"{_label}: unhandled EXECUTE form '{specification?.ExecutableEntity?.GetType().Name}'.");
                    break;
            }
        }

        // ---- Query and table-reference descent ---------------------------------------------------------

        private void WalkQuery(QueryExpression? query, int subqueryDepth)
        {
            if (query is null || !EnterDepth(query))
            {
                return;
            }

            try
            {
                switch (query)
                {
                    case QuerySpecification specification:
                        if (specification.FromClause is not null)
                        {
                            foreach (var reference in specification.FromClause.TableReferences)
                            {
                                WalkTableReference(reference, subqueryDepth);
                            }
                        }

                        foreach (var element in specification.SelectElements)
                        {
                            switch (element)
                            {
                                case SelectScalarExpression scalar:
                                    WalkExpressionSubqueries(scalar.Expression);
                                    break;
                                case SelectSetVariable assignment:
                                    // SELECT @x = (subquery) ... assigns while reading.
                                    WalkExpressionSubqueries(assignment.Expression);
                                    break;
                            }
                        }

                        WalkExpressionSubqueries(specification.WhereClause?.SearchCondition);
                        WalkExpressionSubqueries(specification.HavingClause?.SearchCondition);
                        WalkExpressionSubqueries(specification.TopRowFilter?.Expression);
                        if (specification.OrderByClause is { } orderBy)
                        {
                            foreach (var ordering in orderBy.OrderByElements)
                            {
                                WalkExpressionSubqueries(ordering.Expression);
                            }
                        }

                        WalkExpressionSubqueries(specification.OffsetClause?.OffsetExpression);
                        WalkExpressionSubqueries(specification.OffsetClause?.FetchExpression);
                        break;
                    case BinaryQueryExpression binary:
                        WalkQuery(binary.FirstQueryExpression, subqueryDepth);
                        WalkQuery(binary.SecondQueryExpression, subqueryDepth);
                        break;
                    case QueryParenthesisExpression parenthesis:
                        WalkQuery(parenthesis.QueryExpression, subqueryDepth);
                        break;
                    default:
                        _deps.Warnings.Add($"{_label}: unhandled query expression '{query.GetType().Name}'.");
                        break;
                }
            }
            finally
            {
                _depth--;
            }
        }

        private void WalkTableReference(TableReference reference, int subqueryDepth)
        {
            if (!EnterDepth(reference))
            {
                return;
            }

            try
            {
                switch (reference)
                {
                    case NamedTableReference named:
                        RecordNamedReference(named, subqueryDepth);
                        break;
                    case QualifiedJoin join:
                        WalkTableReference(join.FirstTableReference, subqueryDepth);
                        WalkTableReference(join.SecondTableReference, subqueryDepth);
                        WalkExpressionSubqueries(join.SearchCondition);
                        break;
                    case UnqualifiedJoin join:
                        WalkTableReference(join.FirstTableReference, subqueryDepth);
                        WalkTableReference(join.SecondTableReference, subqueryDepth);
                        break;
                    case JoinParenthesisTableReference parenthesized:
                        WalkTableReference(parenthesized.Join, subqueryDepth);
                        break;
                    case QueryDerivedTable derived:
                        WalkQuery(derived.QueryExpression, subqueryDepth + 1);
                        break;
                    case PivotedTableReference pivoted:
                        WalkTableReference(pivoted.TableReference, subqueryDepth);
                        break;
                    case UnpivotedTableReference unpivoted:
                        WalkTableReference(unpivoted.TableReference, subqueryDepth);
                        break;
                    case SchemaObjectFunctionTableReference function:
                    {
                        // A TVF is read AND required to exist; its body expands when its module is harvested.
                        var name = TableName.From(function.SchemaObject, _currentDatabase);
                        Inbound(name, TableOperation.Read, subqueryDepth);
                        _deps.AddInbound(name, TableOperation.Execute,
                            subqueryDepth > 0 ? DependencyKind.Subquery : DependencyKind.Direct);
                        foreach (var parameter in function.Parameters)
                        {
                            WalkExpressionSubqueries(parameter);
                        }

                        break;
                    }

                    case ChangeTableChangesTableReference changes:
                        _deps.AddInbound(TableName.From(changes.Target, _currentDatabase), TableOperation.Read, DependencyKind.ChangeTracking);
                        break;
                    case ChangeTableVersionTableReference version:
                        _deps.AddInbound(TableName.From(version.Target, _currentDatabase), TableOperation.Read, DependencyKind.ChangeTracking);
                        break;
                    case VariableTableReference:
                    case InlineDerivedTable:
                        // Table variables and VALUES(...) carry no external lineage.
                        break;
                    case OpenJsonTableReference openJson:
                        // OPENJSON shreds a value, not a table; subqueries inside its argument still read.
                        WalkExpressionSubqueries(openJson.Variable);
                        break;
                    case GlobalFunctionTableReference globalFunction:
                        // STRING_SPLIT, GENERATE_SERIES and friends: value-shredding, argument subqueries walk.
                        foreach (var parameter in globalFunction.Parameters)
                        {
                            WalkExpressionSubqueries(parameter);
                        }

                        break;
                    case OpenQueryTableReference openQuery:
                        _deps.Warnings.Add(
                            $"{_label}: OPENQUERY against linked server '{openQuery.LinkedServer?.Value}'; the remote statement's lineage is not statically derivable.");
                        break;
                    case OpenRowsetTableReference:
                        _deps.Warnings.Add($"{_label}: OPENROWSET reference; its lineage is not statically derivable.");
                        break;
                    default:
                        _deps.Warnings.Add($"{_label}: unhandled table reference '{reference.GetType().Name}'.");
                        break;
                }
            }
            finally
            {
                _depth--;
            }
        }

        private void RecordNamedReference(NamedTableReference named, int subqueryDepth)
        {
            var baseName = named.SchemaObject.BaseIdentifier?.Value;
            if (string.IsNullOrEmpty(baseName))
            {
                return;
            }

            // CTE names shadow real objects for 1-part references in their scope.
            if (named.SchemaObject.Identifiers.Count == 1 && IsCteInScope(baseName))
            {
                return;
            }

            // Trigger pseudo-tables read the trigger's parent through the change feed.
            if (_triggerTarget is not null
                && named.SchemaObject.Identifiers.Count == 1
                && (baseName.Equals("inserted", StringComparison.OrdinalIgnoreCase)
                    || baseName.Equals("deleted", StringComparison.OrdinalIgnoreCase)))
            {
                _deps.AddInbound(_triggerTarget, TableOperation.Read, DependencyKind.ChangeTracking);
                _statementReads?.Add((_triggerTarget, DependencyKind.ChangeTracking));
                return;
            }

            if (_tableVariables.Contains(baseName))
            {
                return;
            }

            Inbound(TableName.From(named.SchemaObject, _currentDatabase), TableOperation.Read, subqueryDepth);
        }

        /// <summary>Collects the subqueries of an expression subtree and walks each through the operation-wise
        /// query walker at increased depth. A scoped visitor is contextually sound HERE and only here: within
        /// an expression, every table reference is by definition inside a subquery in a read context, so no
        /// operation context can be lost; hand-writing the two hundred scalar/boolean node kinds would add
        /// risk, not accuracy.</summary>
        private void WalkExpressionSubqueries(TSqlFragment? fragment)
        {
            if (fragment is null)
            {
                return;
            }

            var collector = new SubqueryCollector();
            fragment.Accept(collector);

            foreach (var subquery in collector.TopLevel())
            {
                WalkQuery(subquery, subqueryDepth: 1);
            }
        }

        // ---- Scope helpers ------------------------------------------------------------------------------

        /// <summary>Runs one statement inside its own read frame (for data-movement pairing) and CTE scope.</summary>
        private void WithStatementFrame(WithCtesAndXmlNamespaces? ctes, Action<List<(TableName Table, DependencyKind Kind)>> body)
        {
            var previousReads = _statementReads;
            var reads = new List<(TableName, DependencyKind)>();
            _statementReads = reads;

            var scope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _cteScopes.Add(scope);
            try
            {
                if (ctes is not null)
                {
                    // All CTE names register BEFORE any body walks: T-SQL allows forward and recursive
                    // references among them, and registration first is what makes self-references shadow.
                    foreach (var cte in ctes.CommonTableExpressions)
                    {
                        scope.Add(cte.ExpressionName.Value);
                    }

                    foreach (var cte in ctes.CommonTableExpressions)
                    {
                        var before = reads.Count;
                        WalkQuery(cte.QueryExpression, subqueryDepth: 0);

                        // The CTE's own sources land in local deps so phase two can dissolve it; a
                        // source-less CTE (VALUES) gets NO entry, the reference rule that keeps terminal
                        // resolution honest.
                        var sources = reads.Skip(before).Select(r => r.Item1.Key).Distinct().ToList();
                        if (sources.Count > 0)
                        {
                            _deps.LocalDeps[cte.ExpressionName.Value.ToLowerInvariant()] = sources;
                        }
                    }
                }

                body(reads);
            }
            finally
            {
                _cteScopes.RemoveAt(_cteScopes.Count - 1);
                _statementReads = previousReads;
            }
        }

        /// <summary>Resolves a DML target: a CTE name shadows first (writing through a CTE is statically
        /// unresolvable), then a FROM-clause alias dissolves to its real table, a derived-table alias is
        /// warned about (a phantom write would corrupt the plan), and anything else unresolvable is warned,
        /// never guessed.</summary>
        private TableName? ResolveTarget(TableReference target, FromClause? fromClause)
        {
            switch (target)
            {
                case NamedTableReference named:
                {
                    var name = named.SchemaObject;

                    // The CTE shadow wins over everything: UPDATE cte ... FROM cte must not bind the FROM
                    // entry as a real table.
                    if (name.Identifiers.Count == 1 && IsCteInScope(name.BaseIdentifier.Value))
                    {
                        _deps.Warnings.Add(
                            $"{_label}: DML through CTE '{name.BaseIdentifier.Value}'; the written base table is not statically derivable.");
                        return null;
                    }

                    if (name.Identifiers.Count == 1 && _tableVariables.Contains(name.BaseIdentifier.Value))
                    {
                        return null;
                    }

                    if (name.Identifiers.Count == 1 && fromClause is not null)
                    {
                        var binding = FindTargetBinding(fromClause, name.BaseIdentifier.Value);
                        if (binding.IsDerived)
                        {
                            _deps.Warnings.Add(
                                $"{_label}: DML target '{name.BaseIdentifier.Value}' aliases a derived table; the written base table is not statically derivable.");
                            return null;
                        }

                        if (binding.Table is { } aliased)
                        {
                            return aliased;
                        }
                    }

                    return TableName.From(name, _currentDatabase);
                }

                case VariableTableReference:
                    return null;
                default:
                    _deps.Warnings.Add($"{_label}: unhandled DML target '{target.GetType().Name}'.");
                    return null;
            }
        }

        private (TableName? Table, bool IsDerived) FindTargetBinding(FromClause fromClause, string alias)
        {
            foreach (var reference in fromClause.TableReferences)
            {
                var binding = FindTargetBinding(reference, alias);
                if (binding.Table is not null || binding.IsDerived)
                {
                    return binding;
                }
            }

            return (null, false);
        }

        private (TableName? Table, bool IsDerived) FindTargetBinding(TableReference reference, string alias) => reference switch
        {
            NamedTableReference named when string.Equals(named.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase)
                => (TableName.From(named.SchemaObject, _currentDatabase), false),
            // An unaliased 1-part FROM entry whose name IS the target name also binds (UPDATE T ... FROM T).
            NamedTableReference named when named.Alias is null
                                           && string.Equals(named.SchemaObject.BaseIdentifier.Value, alias, StringComparison.OrdinalIgnoreCase)
                => (TableName.From(named.SchemaObject, _currentDatabase), false),
            QueryDerivedTable derived when string.Equals(derived.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase)
                => (null, true),
            QualifiedJoin join => Coalesce(FindTargetBinding(join.FirstTableReference, alias), () => FindTargetBinding(join.SecondTableReference, alias)),
            UnqualifiedJoin join => Coalesce(FindTargetBinding(join.FirstTableReference, alias), () => FindTargetBinding(join.SecondTableReference, alias)),
            JoinParenthesisTableReference parenthesized => FindTargetBinding(parenthesized.Join, alias),
            _ => (null, false),
        };

        private static (TableName? Table, bool IsDerived) Coalesce(
            (TableName? Table, bool IsDerived) first, Func<(TableName? Table, bool IsDerived)> second)
            => first.Table is not null || first.IsDerived ? first : second();

        private void Inbound(TableName table, TableOperation operation, int subqueryDepth)
        {
            var kind = subqueryDepth > 0 ? DependencyKind.Subquery : DependencyKind.Direct;
            _deps.AddInbound(table, operation, kind);
            _statementReads?.Add((table, kind));
        }

        private void Outbound(TableName table, TableOperation operation)
        {
            _deps.AddOutbound(table, operation);
            if (operation is TableOperation.Create or TableOperation.CreateAs)
            {
                _deps.ScriptCreated.Add(table.Key);
            }

            if (operation is TableOperation.Drop)
            {
                _deps.ScriptDropped.Add(table.Key);

                // Create BEFORE drop is the engine's run-scoped staging signature; a drop followed by a
                // (re)create is a rebuild and keeps its lifecycle relations.
                if (_deps.ScriptCreated.Contains(table.Key))
                {
                    _deps.CreatedThenDropped.Add(table.Key);
                }
            }
        }

        /// <summary>
        /// Pairs an insert-like statement's reads with its write target (the DeltaForge
        /// extract_data_flow_edges rules): both sides need full three-part identity (relaxed when the
        /// statement reads a change feed), reads of EARLIER CTAS targets are script plumbing and pair
        /// nothing, self-pairs survive only when the statement's own read of the target was subquery or
        /// change-feed kind. The local-deps entry (every read, no part filter) is what phase two dissolves
        /// through; a read-less statement records no entry (the reference rule).
        /// </summary>
        private void RecordMovement(TableName target, List<(TableName Table, DependencyKind Kind)> reads)
        {
            var sources = reads.Select(r => r.Table.Key).Distinct().ToList();
            if (_deps.LocalDeps.TryGetValue(target.Key, out var existing))
            {
                existing.AddRange(sources.Where(s => !existing.Contains(s, StringComparer.Ordinal)));
            }
            else if (sources.Count > 0)
            {
                _deps.LocalDeps[target.Key] = sources;
            }

            var statementChangeFeed = reads.Where(r => r.Kind == DependencyKind.ChangeTracking)
                .Select(r => r.Table.Key).ToHashSet(StringComparer.Ordinal);
            var statementExempt = reads.Where(r => r.Kind is DependencyKind.Subquery or DependencyKind.ChangeTracking)
                .Select(r => r.Table.Key).ToHashSet(StringComparer.Ordinal);
            var hasChangeFeed = statementChangeFeed.Count > 0;

            foreach (var (read, _) in reads.DistinctBy(r => r.Table.Key))
            {
                if (_deps.CtasCreated.Contains(read.Key) && read.Key != target.Key)
                {
                    continue;
                }

                if (read.Key == target.Key && !statementExempt.Contains(read.Key))
                {
                    continue;
                }

                var sourceRelaxed = statementChangeFeed.Contains(read.Key);
                if (!sourceRelaxed && read.PartCount < 3)
                {
                    continue;
                }

                if (target.PartCount < 3 && !hasChangeFeed)
                {
                    continue;
                }

                AddPair(read, target);
            }
        }

        /// <summary>The in-place carve-out, exactly the reference: UPDATE/DELETE produce pairs only when the
        /// statement reads a change feed (a propagating delete is lineage; a plain delete is not), and then
        /// EVERY qualified read of the statement pairs, with the part rules relaxed. The movement still
        /// records local deps so phase two dissolves the chain.</summary>
        private void RecordInPlaceMovement(TableName target, List<(TableName Table, DependencyKind Kind)> reads)
        {
            var sources = reads.Select(r => r.Table.Key).Distinct().ToList();
            if (sources.Count > 0 && !_deps.LocalDeps.ContainsKey(target.Key))
            {
                _deps.LocalDeps[target.Key] = sources;
            }

            var statementChangeFeed = reads.Where(r => r.Kind == DependencyKind.ChangeTracking)
                .Select(r => r.Table.Key).ToHashSet(StringComparer.Ordinal);
            if (statementChangeFeed.Count == 0)
            {
                return;
            }

            var statementExempt = reads.Where(r => r.Kind is DependencyKind.Subquery or DependencyKind.ChangeTracking)
                .Select(r => r.Table.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var (read, _) in reads.DistinctBy(r => r.Table.Key))
            {
                if (read.Key == target.Key && !statementExempt.Contains(read.Key))
                {
                    continue;
                }

                if (!statementChangeFeed.Contains(read.Key) && read.PartCount < 3)
                {
                    continue;
                }

                AddPair(read, target);
            }
        }

        private void AddPair(TableName source, TableName target)
        {
            if (_pairKeys.Add((source.Key, target.Key)))
            {
                _deps.DataFlowPairs.Add(new DataFlowPair { Source = source, Target = target });
            }
        }

        // ---- Created-object capture -------------------------------------------------------------------

        /// <summary>Records an object this script created with its verbatim DDL and (for a plain table) its
        /// columns, so the catalog can attach the generating script and an offline column dictionary. A
        /// temp object is script-local and never captured; a later CREATE for the same key supersedes an
        /// earlier one (a rebuild).</summary>
        private void RecordCreatedObject(TableName table, LineageNodeKind kind, TSqlFragment ddlFragment, IReadOnlyList<LineageColumn> columns)
        {
            if (table.IsTemp)
            {
                return;
            }

            var ddl = FragmentText(ddlFragment);
            if (string.IsNullOrWhiteSpace(ddl))
            {
                return;
            }

            _deps.CreatedObjects[table.Key] = new CreatedObject
            {
                Table = table,
                Kind = kind,
                Ddl = ddl,
                Columns = columns,
            };
        }

        /// <summary>Reads the column definitions of a CREATE TABLE body: the name, the type rendered verbatim
        /// from the source, and nullability (an explicit NOT NULL constraint makes it non-nullable; SQL Server
        /// columns default to nullable otherwise).</summary>
        private static IReadOnlyList<LineageColumn> ReadColumnDefinitions(TableDefinition? definition)
        {
            if (definition is null || definition.ColumnDefinitions.Count == 0)
            {
                return [];
            }

            var columns = new List<LineageColumn>(definition.ColumnDefinitions.Count);
            var ordinal = 1;
            foreach (var column in definition.ColumnDefinitions)
            {
                var name = column.ColumnIdentifier?.Value;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var nullable = true;
                foreach (var constraint in column.Constraints)
                {
                    if (constraint is NullableConstraintDefinition nullableConstraint)
                    {
                        nullable = nullableConstraint.Nullable;
                    }
                }

                columns.Add(new LineageColumn
                {
                    Ordinal = ordinal++,
                    Name = name,
                    DataType = FragmentText(column.DataType),
                    Nullable = nullable,
                });
            }

            return columns;
        }

        /// <summary>The verbatim source text of a fragment, reassembled from its token-stream span. Null when
        /// the fragment carries no token span (a synthesized node).</summary>
        private static string? FragmentText(TSqlFragment? fragment)
        {
            if (fragment?.ScriptTokenStream is not { } tokens
                || fragment.FirstTokenIndex < 0
                || fragment.LastTokenIndex < fragment.FirstTokenIndex
                || fragment.LastTokenIndex >= tokens.Count)
            {
                return null;
            }

            var builder = new StringBuilder();
            for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            {
                builder.Append(tokens[i].Text);
            }

            return builder.ToString().Trim();
        }

        private bool IsCteInScope(string name)
        {
            for (var i = _cteScopes.Count - 1; i >= 0; i--)
            {
                if (_cteScopes[i].Contains(name))
                {
                    return true;
                }
            }

            return false;
        }

        private bool EnterDepth(TSqlFragment fragment)
        {
            _depth++;
            if (_depth <= MaxDepth)
            {
                return true;
            }

            _deps.Warnings.Add($"{_label}: nesting beyond {MaxDepth} levels at line {fragment.StartLine}; deeper lineage is not captured.");
            _depth--;
            return false;
        }
    }

    /// <summary>Collects the scalar subqueries of one expression subtree; nested subqueries are filtered to
    /// the top-most by token span, because each collected query is walked recursively anyway.</summary>
    private sealed class SubqueryCollector : TSqlFragmentVisitor
    {
        private readonly List<ScalarSubquery> _found = [];

        public override void Visit(ScalarSubquery node) => _found.Add(node);

        public IReadOnlyList<QueryExpression> TopLevel()
        {
            var top = new List<QueryExpression>();
            foreach (var candidate in _found)
            {
                var nested = _found.Any(other => !ReferenceEquals(other, candidate)
                    && other.FirstTokenIndex <= candidate.FirstTokenIndex
                    && other.LastTokenIndex >= candidate.LastTokenIndex);
                if (!nested && candidate.QueryExpression is not null)
                {
                    top.Add(candidate.QueryExpression);
                }
            }

            return top;
        }
    }
}
