using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a mapping's <c>$assert</c> is read (osdu/docs/reference/flow/mapping-assertions.md): the assertion flows' conditions,
/// the stage, the action, the values and the where conditions, and each place it is refused, with what to write instead.
/// </summary>
public sealed class MappingAssertionLoaderTests
{
    private static MappingDefinition Mapping(string data) => TestSchema.Mapping(data);

    private static string Refused(string data) => Assert.Throws<FlowValidationException>(() => Mapping(data)).Message;

    private static MappingEntry Entry(MappingDefinition mapping, string target)
        => mapping.Entries.SelectMany(e => e.Fillers).Single(e => e.Target.Text == target);

    [Fact]
    public void An_assertion_judges_the_record_and_holds_unless_it_says_otherwise()
    {
        var mapping = Mapping("""
            Count:
              $from: count
              $assert:
                - between: [0, 250]
            """);

        var assertion = Assert.Single(Entry(mapping, "osdu.data.Count").Assertions);
        Assert.Equal(AssertionStage.Record, assertion.Stage);
        Assert.Equal(AssertionAction.Hold, assertion.OnFail);
        Assert.Equal(ValueOperator.Between, assertion.Condition.Operator);
        Assert.Equal("between 0 and 250", assertion.Label);
        Assert.False(assertion.Named);
        Assert.False(assertion.AnyValue);
        Assert.Equal("record.data.Count.$assert[0]", assertion.Location);
        Assert.Single(mapping.Assertions());
    }

    [Fact]
    public void An_assertion_takes_its_name_stage_action_and_conditions_as_written()
    {
        var mapping = Mapping("""
            Weight:
              $from: weight
              $required: false
              $assert:
                - name: weight-placeholder
                  description: The source writes -999 for an unknown weight.
                  stage: incoming
                  notIn: [-999, -999.25]
                  onFail: omit
                  where:
                    - column: weight_unit
                      equals: KG
                      ignoreCase: true
                - atMost: 10000
                  tolerance: 0.5
                  onFail: report
            """);

        var assertions = Entry(mapping, "osdu.data.Weight").Assertions;
        Assert.Equal(2, assertions.Count);
        var incoming = assertions[0];
        Assert.Equal("weight-placeholder", incoming.Label);
        Assert.True(incoming.Named);
        Assert.Equal("The source writes -999 for an unknown weight.", incoming.Description);
        Assert.Equal(AssertionStage.Incoming, incoming.Stage);
        Assert.Equal(AssertionAction.Omit, incoming.OnFail);
        Assert.Equal(["-999", "-999.25"], incoming.Condition.Operands.Select(o => o.Text));
        var filter = Assert.Single(incoming.Where);
        Assert.Null(filter.Field);
        Assert.Equal(new DatasetColumn(null, "weight_unit"), filter.Column);
        Assert.True(filter.Condition.IgnoreCase);

        Assert.Equal(AssertionAction.Report, assertions[1].OnFail);
        Assert.Equal(0.5, assertions[1].Condition.Tolerance);

        // The column the incoming condition reads is one the node reads, so the preflight checks it against the ingestion tables.
        Assert.Contains(new DatasetColumn(null, "weight_unit"), Entry(mapping, "osdu.data.Weight").Columns);
    }

    [Fact]
    public void Each_condition_of_the_assertion_flows_is_read_the_same_way()
    {
        var mapping = Mapping("""
            Symbol:
              $from: symbol
              $assert:
                - equals: GR
                - notEquals: CALI
                - in: [GR, SP]
                - notIn: [NONE]
                - matches: '^[A-Z]+$'
                - notMatches: '\s'
                - startsWith: G
                - endsWith: R
                - contains: R
                - notContains: X
                - exists: true
                - empty: false
                - type: string
                - length: { atLeast: 1, atMost: 16 }
                - greaterThan: A
                - lessThan: Z
                - atLeast: AA
                - atMost: ZZ
            """);

        var operators = Entry(mapping, "osdu.data.Symbol").Assertions.Select(a => a.Condition.Operator).ToList();
        Assert.Equal(
            [
                ValueOperator.EqualTo, ValueOperator.NotEqualTo, ValueOperator.In, ValueOperator.NotIn, ValueOperator.Matches, ValueOperator.NotMatches,
                ValueOperator.StartsWith, ValueOperator.EndsWith, ValueOperator.Contains, ValueOperator.NotContains, ValueOperator.Exists,
                ValueOperator.Empty, ValueOperator.Type, ValueOperator.Length, ValueOperator.GreaterThan, ValueOperator.LessThan,
                ValueOperator.AtLeast, ValueOperator.AtMost,
            ],
            operators);
    }

    [Fact]
    public void A_record_condition_reads_a_field_and_an_item_assertion_reads_the_items_row()
    {
        var mapping = Mapping("""
            Curves:
              $forEach: curves
              $required: false
              $assert:
                - length: { atLeast: 2 }
                  onFail: report
              $item:
                CurveID:
                  $from: curve_id
                  $assert:
                    - matches: '^[A-Z0-9_]+$'
                      where:
                        - field: data.Curves.TopDepth
                          greaterThan: 0
                    - stage: incoming
                      notEquals: DEPT
                      where:
                        - column: curve_unit
                          exists: true
                        - column: $dataset.name
                          startsWith: W
            """);

        var array = Entry(mapping, "osdu.data.Curves").Assertions;
        Assert.Equal(ValueOperator.Length, Assert.Single(array).Condition.Operator);

        var item = Entry(mapping, "osdu.data.Curves[].CurveID").Assertions;
        Assert.Equal("data.Curves.TopDepth", Assert.Single(item[0].Where).Field);
        Assert.Equal(new DatasetColumn("curves", "curve_unit"), item[1].Where[0].Column);
        Assert.Equal(new DatasetColumn(null, "name"), item[1].Where[1].Column);
        Assert.Equal("matches \"^[A-Z0-9_]+$\" where data.Curves.TopDepth greaterThan 0", item[0].Label);
    }

    [Fact]
    public void A_coalesce_and_a_lookup_node_assert_on_the_value_the_record_carries()
    {
        var mapping = Mapping("""
            Unit:
              $coalesce:
                - $cache: UnitOfMeasure.id
                  $findBy: Code = unit
                - $from: unit_id
              $assert:
                - startsWith: 'dev:'
            """);

        var unit = Entry(mapping, "osdu.data.Unit");
        Assert.Single(unit.Assertions);
        Assert.All(unit.Alternatives, alternative => Assert.Empty(alternative.Assertions));
    }

    [Theory]
    [InlineData("""
        Symbol:
          $value: GR
          $assert:
            - equals: GR
        """, "a literal $value gives every record the same one")]
    [InlineData("""
        Symbol:
          $from: symbol
          $assert: { equals: GR }
        """, "$assert is a list of assertions, such as $assert: [ { between: [0, 250] } ]")]
    [InlineData("""
        Symbol:
          $from: symbol
          $assert: []
        """, "$assert lists no assertion")]
    [InlineData("""
        Symbol:
          $from: symbol
          $assert: [GR]
        """, "record.data.Symbol.$assert[0] is an assertion: one condition and its settings")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - betwen: [0, 1]
        """, "'betwen' is not a word of an assertion. Did you mean 'between'?")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - between: [0, 1]
              $stage: incoming
        """, "the words of an assertion carry no '$'")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - between: [0, 1]
              stage: before
        """, "stage is 'before'; write record or incoming")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - between: [0, 1]
              onFail: warn
        """, "onFail is 'warn'; write hold, report or omit")]
    [InlineData("""
        Unit:
          $cache: UnitOfMeasure.id
          $findBy: Code = unit
          $assert:
            - stage: incoming
              exists: true
        """, "this node's value comes from the partition's cache")]
    [InlineData("""
        Curves:
          $forEach: curves
          $assert:
            - stage: incoming
              exists: true
          $item:
            CurveID: { $from: curve_id }
        """, "a $forEach node writes an array of the rows of curves")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - stage: incoming
              values: any
              atLeast: 0
        """, "stage: incoming judges the one value the row gives the node; remove values")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
              onFail: omit
        """, "onFail: omit leaves the value out of the record, and the node is required")]
    [InlineData("""
        Aliases:
          $from: alias
          $required: false
          $assert:
            - equals: A
              values: any
              onFail: omit
        """, "values: any judges the values together")]
    [InlineData("""
        Unit:
          $from: unit
          $assert:
            - resolves: reference-data--UnitOfMeasure
        """, "asks whether a reference resolves, which a mapping does not assert")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
              atMost: 10
        """, "names atLeast and atMost; a condition has one operator. Write between for a range")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - onFail: report
        """, "names no condition: one of equals, notEquals")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - between: [10, 0]
        """, "runs from 10 down to 0; write the lower bound first")]
    [InlineData("""
        Symbol:
          $from: symbol
          $assert:
            - matches: '(['
        """, "is not a regular expression")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
              where:
                - column: depth_unit
                  equals: M
        """, "reads a column of the row, and the assertion judges the value the record carries (stage: record)")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - stage: incoming
              atLeast: 0
              where:
                - field: data.Name
                  equals: M
        """, "reads a field of the record, and the assertion judges the value the row gives (stage: incoming)")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
              where: { field: data.Name, equals: M }
        """, ".where is a list of conditions")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
              where:
                - field: Name
                  equals: M
        """, "starts at 'Name', which is not a property of an OSDU record")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
              where:
                - field: data.Name
                  equals: M
                  values: any
        """, "takes field, one operator, ignoreCase and tolerance, not 'values'")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
            - atLeast: 0
        """, "record.data.Weight.$assert[1] asserts what record.data.Weight.$assert[0] asserts")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
              name: positive
            - atMost: 100
              name: positive
        """, "is named 'positive', as record.data.Weight.$assert[0] is")]
    [InlineData("""
        Unit:
          $coalesce:
            - $from: unit
              $assert:
                - exists: true
            - $from: unit_id
        """, "$assert decides for the whole $coalesce node; write it beside $coalesce")]
    [InlineData("""
        Aliases:
          - first
          - $from: alias
            $assert:
              - equals: A
        """, "a list of values takes no assertions")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - atLeast: 0
              ignoreCase: true
        """, "ignoreCase applies to text comparisons, not to atLeast")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - exists: yes
        """, ".exists is true or false")]
    [InlineData("""
        Weight:
          $from: weight
          $assert:
            - stage: incoming
              atLeast: 0
              where:
                - column: curves.top
                  exists: true
        """, "is not a column")]
    public void A_mapping_refuses_an_assertion_that_cannot_mean_what_it_says(string data, string expected)
        => Assert.Contains(expected, Refused(data));

    [Fact]
    public void A_node_states_at_most_twenty_assertions()
    {
        var assertions = string.Concat(Enumerable.Range(0, 21).Select(i => $"\n    - atLeast: {i}"));
        Assert.Contains("lists 21 assertions, and a node states at most 20", Refused("Weight:\n  $from: weight\n  $assert:" + assertions));
    }

    [Fact]
    public void A_name_is_at_most_two_hundred_characters()
        => Assert.Contains("1 to 200 characters", Refused($"""
            Weight:
              $from: weight
              $assert:
                - atLeast: 0
                  name: {new string('n', 201)}
            """));

    [Fact]
    public void Adding_an_assertion_moves_the_mappings_fingerprint()
    {
        var plain = TestSchema.Mapping("Count: { $from: count }");
        var asserted = TestSchema.Mapping("""
            Count:
              $from: count
              $assert:
                - atLeast: 0
            """);

        Assert.NotEqual(plain.Fingerprint, asserted.Fingerprint);
    }
}
