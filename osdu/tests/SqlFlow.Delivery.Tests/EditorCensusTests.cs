using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The key census files the editor documents and checks the module's documents with (<c>osdu/docs/census</c>): every key
/// a loader accepts is in its census, and the census documents no key a loader would refuse. The flow kinds are compared
/// with the YAML models their strict loaders read; the mapping's modifiers and the dictionary document, which their
/// mappers parse by hand, with the names those mappers accept.
/// </summary>
public sealed class EditorCensusTests
{
    /// <summary>The census files, as the build copies them beside the tests.</summary>
    private static string CensusDirectory => Path.Combine(AppContext.BaseDirectory, "census");

    /// <summary>The platform envelope: the host reads it from every flow, and the editor documents it as SQLFlow does.</summary>
    private static readonly string[] Envelope = ["schedule", "mode", "lifecycle"];

    private static JsonObject Census(string file)
    {
        var path = Path.Combine(CensusDirectory, file);
        Assert.True(File.Exists(path), $"The census file {file} is missing from {CensusDirectory}.");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static List<JsonObject> Keys(JsonObject census) => census["keys"]!.AsArray().Select(k => k!.AsObject()).ToList();

    private static SortedSet<string> Paths(JsonObject census) => new(Keys(census).Select(k => k["path"]!.GetValue<string>()), StringComparer.Ordinal);

    [Theory]
    [InlineData("keys.delivery.json", "delivery")]
    [InlineData("keys.cache.json", "cache")]
    [InlineData("keys.retrieval.json", "retrieval")]
    [InlineData("keys.mapping.json", "mapping")]
    [InlineData("keys.dictionary.json", "dictionary")]
    public void A_census_file_names_its_kind_and_documents_every_key_it_lists(string file, string kind)
    {
        var census = Census(file);
        var named = census["flowType"]?.GetValue<string>() ?? census["documentType"]?.GetValue<string>();
        Assert.Equal(kind, named);
        Assert.False(census.ContainsKey("flowType") && census.ContainsKey("documentType"), $"{file} names both a flowType and a documentType.");
        Assert.True(census["strictKeys"]?.GetValue<bool>() ?? false, $"{file}: every loader of the module refuses an unknown key, so its census says so.");

        var keys = Keys(census);
        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, census["keyCount"]?.GetValue<int>());
        foreach (var key in keys)
        {
            var path = key["path"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(path), $"{file} has an entry without a path.");
            Assert.False(string.IsNullOrWhiteSpace(key["type"]?.GetValue<string>()), $"{file}: {path} has no type.");
            Assert.False(string.IsNullOrWhiteSpace(key["description"]?.GetValue<string>()), $"{file}: {path} has no description.");
        }

        Assert.Equal(keys.Count, Paths(census).Count);
        var text = File.ReadAllText(Path.Combine(CensusDirectory, file));
        // The project's writing style forbids the em dash in any text it ships, and an en dash standing in for one.
        Assert.DoesNotContain('—', text);
        Assert.DoesNotContain('–', text);
    }

    [Theory]
    [InlineData("keys.delivery.json", typeof(FlowYaml))]
    [InlineData("keys.cache.json", typeof(CacheYaml))]
    [InlineData("keys.retrieval.json", typeof(RetrievalYaml))]
    public void A_flow_kinds_census_documents_exactly_the_keys_its_loader_accepts(string file, Type model)
    {
        var accepted = YamlKeyPaths.Of(model);
        var census = Census(file);
        Assert.True(census["includeEnvelope"]?.GetValue<bool>() ?? false, $"{file}: the platform envelope belongs to every flow kind.");

        var expected = accepted.Paths.Keys.Where(p => !Envelope.Contains(p.Split('.')[0])).ToHashSet(StringComparer.Ordinal);
        var documented = Paths(census);
        var extras = documented.Where(p => !expected.Contains(p) && !UnderFreeForm(p, accepted)).ToList();
        Assert.True(extras.Count == 0, $"{file} documents keys the loader refuses: {string.Join(", ", extras)}");
        var missing = expected.Where(p => !documented.Contains(p)).ToList();
        Assert.True(missing.Count == 0, $"{file} leaves out keys the loader accepts: {string.Join(", ", missing)}");

        // What the author writes below a free-form value is either free, or documented key by key.
        foreach (var key in Keys(census).Where(k => k["freeForm"]?.GetValue<bool>() ?? false))
        {
            Assert.Contains(key["path"]!.GetValue<string>(), accepted.FreeForm);
        }
    }

    [Fact]
    public void The_mapping_census_documents_the_mapping_its_loader_reads_and_every_modifier_setting()
    {
        var accepted = YamlKeyPaths.Of(typeof(MappingYaml));
        var documented = Paths(Census("keys.mapping.json"));

        var missing = accepted.Paths.Keys.Where(p => !documented.Contains(p)).ToList();
        Assert.True(missing.Count == 0, $"keys.mapping.json leaves out keys the loader accepts: {string.Join(", ", missing)}");

        // Below a modifier, the census documents what MappingMapper parses by hand: a keyed modifier, its settings, and
        // the settings a replace takes beside its table.
        const string Modifier = "mappings[].modifiers[]";
        var keyed = MappingMapper.ModifierNames.Except(["trim", "upper", "lower"], StringComparer.Ordinal).ToList();
        var expectedBelow = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in keyed)
        {
            expectedBelow.Add($"{Modifier}.{name}");
        }

        foreach (var setting in MappingMapper.SplitSettings)
        {
            expectedBelow.Add($"{Modifier}.split.{setting}");
        }

        foreach (var setting in MappingMapper.NumberSettings)
        {
            expectedBelow.Add($"{Modifier}.number.{setting}");
        }

        foreach (var setting in MappingMapper.ReplaceSettings)
        {
            expectedBelow.Add($"{Modifier}.{setting}");
        }

        expectedBelow.Add($"{Modifier}.replace.<value>");
        var below = documented.Where(p => p.StartsWith(Modifier + ".", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedBelow, below.Order(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal));

        // Nothing else is documented that the loader would refuse.
        var extras = documented.Where(p => !accepted.Paths.ContainsKey(p) && !p.StartsWith(Modifier + ".", StringComparison.Ordinal)).ToList();
        Assert.True(extras.Count == 0, $"keys.mapping.json documents keys the loader refuses: {string.Join(", ", extras)}");

        // The named modifiers are the census's allowed values for a modifier written as a name.
        var modifierEntry = Keys(Census("keys.mapping.json")).Single(k => k["path"]!.GetValue<string>() == Modifier);
        Assert.Equal(MappingMapper.ModifierNames, modifierEntry["enumValues"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void The_dictionary_census_documents_the_keys_its_mapper_reads()
    {
        var documented = Paths(Census("keys.dictionary.json"));
        var topLevel = documented.Where(p => !p.Contains('.', StringComparison.Ordinal) && !p.Contains('[', StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(DictionaryMapper.Keys.ToHashSet(StringComparer.Ordinal), topLevel);
        Assert.Equal(
            ["documentType", "name", "description", "key", "fields", "fields[]", "entries", "entries.<key>", "entries.<key>.<field>"],
            Keys(Census("keys.dictionary.json")).Select(k => k["path"]!.GetValue<string>()));
    }

    /// <summary>Whether <paramref name="path"/> lies below a value the loader reads as a plain object of the author's own shape.</summary>
    private static bool UnderFreeForm(string path, YamlKeyPaths accepted)
        => accepted.FreeForm.Any(free => path.StartsWith(free + ".", StringComparison.Ordinal) || path.StartsWith(free + "[", StringComparison.Ordinal));
}
