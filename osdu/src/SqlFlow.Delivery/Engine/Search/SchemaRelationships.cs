using System.Text.RegularExpressions;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>A place in a kind's records whose value names another record, as the kind's schema declares it.</summary>
/// <param name="At">
/// The property path every value of the place shares, as a check names it: <c>data.WellboreID</c>, a list's items by
/// <c>[]</c> (<c>data.VerticalMeasurements[].VerticalMeasurementUnitOfMeasureID</c>), and <c>*</c> for any property of an
/// object whose properties the schema does not name.
/// </param>
/// <param name="Names">The entity types a value there names (<c>master-data--Wellbore</c>), or a group alone (<c>dataset</c>) for any record of it.</param>
/// <param name="ByPattern">
/// True when the schema declares no <c>x-osdu-relationship</c> there and the types are read from the pattern an id there
/// must match (<c>^[\w\-\.]+:master-data\-\-Wellbore:[\w\-\.\:\%]+[0-9]*$</c>), as a partition's own schema may write it.
/// </param>
public sealed record SchemaRelationship(string At, IReadOnlyList<string> Names, bool ByPattern = false);

/// <summary>The places a schema's records name other records, and whether the walk stopped at its bound before it saw them all.</summary>
public sealed record SchemaRelationshipRead(IReadOnlyList<SchemaRelationship> Places, bool Cut);

/// <summary>
/// Every place a kind's records name another record, read from the rules its schema compiles to (<see cref="SchemaRules"/>),
/// so a reference is found where a check would judge it: through every <c>$ref</c> to another schema the bundle holds,
/// every <c>allOf</c> branch, every form of a <c>oneOf</c> or <c>anyOf</c> (the items of <c>meta</c> and
/// <c>GeoContexts</c> are a choice of forms), every list's items, and the schema an object gives every property it does
/// not name.
/// </summary>
/// <remarks>
/// <para>
/// A place is one the schema marks with <c>x-osdu-relationship</c>. Where it marks none, a pattern an id there must match
/// that names an entity type (<c>...:master-data\-\-Wellbore:...</c>, or a choice of them) makes it one, flagged as read
/// from the pattern: OSDU's own schemas write both, and a partition's own schema may write the pattern alone. The record's
/// own <c>id</c> is never one, though its pattern names the record's own type.
/// </para>
/// <para>
/// A schema that refers back to itself is walked once along each path: a node is not entered again below itself. The walk
/// is bounded, <see cref="MaxDepth"/> steps deep and <see cref="MaxVisits"/> nodes in all, and says when it stopped at a
/// bound. Places are listed in the order the schema declares them, parents before their children, each once, with the
/// entity types every form and branch names there.
/// </para>
/// </remarks>
public static partial class SchemaRelationships
{
    /// <summary>The deepest a walk goes, in properties, items and forms.</summary>
    public const int MaxDepth = 48;

    /// <summary>The most nodes one walk enters.</summary>
    public const int MaxVisits = 200_000;

    /// <summary>The record's own id, whose pattern names the record's own type: never a reference to another record.</summary>
    private const string OwnId = "id";

    /// <summary>The places <paramref name="rules"/> declares a value naming another record.</summary>
    public static SchemaRelationshipRead Of(SchemaRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var walk = new Walk();
        walk.Enter(rules.Root, string.Empty, 0, named: false);
        return new SchemaRelationshipRead(
            walk.Order.Select(at => new SchemaRelationship(at, walk.Found[at].Names, !walk.Found[at].Declared)).ToList(),
            walk.Cut);
    }

    /// <summary>
    /// The entity types an id pattern names, as OSDU writes the pattern of a reference
    /// (<c>^[\w\-\.]+:master-data\-\-Wellbore:[\w\-\.\:\%]+[0-9]*$</c>, or a choice of types in parentheses); empty for a
    /// pattern naming none.
    /// </summary>
    public static IReadOnlyList<string> NamedByPattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var plain = pattern.Replace(@"\-", "-", StringComparison.Ordinal).Replace(@"\.", ".", StringComparison.Ordinal);
        return IdPart().Matches(plain).Select(m => m.Groups["type"].Value).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>An entity type between the parts of an id pattern: after a colon, a parenthesis or a bar, before a colon, a parenthesis or a bar.</summary>
    [GeneratedRegex(@"(?<=[:(|])(?<type>[a-z][a-z0-9]*(?:-[a-z0-9]+)*--[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)(?=[:)|])", RegexOptions.CultureInvariant)]
    private static partial Regex IdPart();

    private sealed class Walk
    {
        private readonly HashSet<RuleNode> _ancestors = new(ReferenceEqualityComparer.Instance);
        private int _visits;

        public List<string> Order { get; } = [];

        public Dictionary<string, (List<string> Names, bool Declared)> Found { get; } = new(StringComparer.Ordinal);

        public bool Cut { get; private set; }

        /// <param name="node">The rules at the place.</param>
        /// <param name="at">The place.</param>
        /// <param name="depth">How many steps the walk took to it.</param>
        /// <param name="named">Whether the list holding these items is itself marked as naming records, so its items' pattern adds nothing.</param>
        public void Enter(RuleNode node, string at, int depth, bool named)
        {
            if (depth > MaxDepth || ++_visits > MaxVisits)
            {
                Cut = true;
                return;
            }

            if (!_ancestors.Add(node))
            {
                return;
            }

            try
            {
                if (at.Length > 0)
                {
                    if (node.Relationships.Count > 0)
                    {
                        Add(at, node.Relationships, declared: true);
                    }
                    else if (!named && at != OwnId && node.Patterns.SelectMany(p => NamedByPattern(p.Text)).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } byPattern)
                    {
                        Add(at, byPattern, declared: false);
                    }
                }

                foreach (var (name, child) in node.Properties)
                {
                    Enter(child, at.Length == 0 ? name : $"{at}.{name}", depth + 1, named: false);
                }

                if (node.Additional is { } additional)
                {
                    Enter(additional, at.Length == 0 ? "*" : $"{at}.*", depth + 1, named: false);
                }

                if (node.Items is { } items)
                {
                    Enter(items, at + "[]", depth + 1, named: node.Relationships.Count > 0);
                }

                foreach (var forms in node.Choices)
                {
                    foreach (var form in forms)
                    {
                        Enter(form, at, depth + 1, named);
                    }
                }
            }
            finally
            {
                _ancestors.Remove(node);
            }
        }

        private void Add(string at, IReadOnlyList<string> names, bool declared)
        {
            if (!Found.TryGetValue(at, out var held))
            {
                held = ([], declared);
                Order.Add(at);
            }

            foreach (var name in names)
            {
                if (!held.Names.Contains(name, StringComparer.Ordinal))
                {
                    held.Names.Add(name);
                }
            }

            // A place one form marks with x-osdu-relationship is declared, whatever another form's pattern says.
            Found[at] = (held.Names, held.Declared || declared);
        }
    }
}
