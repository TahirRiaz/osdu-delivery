using SqlFlow.Core;
using SqlFlow.Yaml;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Tests;

/// <summary>
/// A test-only document kind (<c>flowType: test</c>) so the platform suites can drive the loader, the estate
/// scan, the catalog sync, the proposal preflight and the run queue without any real engine. The body is the
/// smallest document that exercises every header the platform reads: a name, an optional batch, a source and an
/// optional target reference, a credentials map (each value is meant to be a <c>${...}</c> reference) and an
/// explicit <c>repoTree</c> switch for the snapshot-executability decision.
/// </summary>
public sealed class TestFlowKind : IFlowDocumentKind
{
    public const string Type = "test";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private sealed class Body
    {
        public string? Name { get; set; }

        public string? Batch { get; set; }

        public string? Source { get; set; }

        public string? Target { get; set; }

        public Dictionary<string, string>? Credentials { get; set; }

        public bool RepoTree { get; set; }
    }

    public string FlowType => Type;

    public string Description => "a test-only flow that executes nothing";

    /// <summary>A loader that knows only this kind: what every platform test parses with.</summary>
    public static YamlDocumentLoader Loader() => new([new TestFlowKind()]);

    public FlowDocument Parse(string yaml, string source, FlowDocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(envelope);
        Body? body;
        try
        {
            body = Deserializer.Deserialize<Body>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        body ??= new Body();
        var name = YamlDocumentParts.RequireFlowName(body.Name, "test flow", source);
        if (string.IsNullOrWhiteSpace(body.Source))
        {
            throw new FlowValidationException($"{source}: test flow '{name}' requires a 'source'.");
        }

        return new TestFlowDocument
        {
            FlowName = name,
            BatchLabel = YamlDocumentParts.NullIfBlank(body.Batch),
            Source = body.Source.Trim(),
            Target = YamlDocumentParts.NullIfBlank(body.Target),
            Credentials = body.Credentials ?? new Dictionary<string, string>(),
            RepoTree = body.RepoTree,
            Schedule = envelope.Schedule,
            Mode = envelope.Mode,
            Lifecycle = envelope.Lifecycle,
        };
    }
}

/// <summary>The document <see cref="TestFlowKind"/> produces.</summary>
public sealed record TestFlowDocument : FlowDocument
{
    public required string FlowName { get; init; }

    public override string Name => FlowName;

    public override string Kind => TestFlowKind.Type;

    public string? BatchLabel { get; init; }

    public override string? Batch => BatchLabel;

    public required string Source { get; init; }

    public string? Target { get; init; }

    public IReadOnlyDictionary<string, string> Credentials { get; init; } = new Dictionary<string, string>();

    public bool RepoTree { get; init; }

    public override string? SourceReference => Source;

    public override string? TargetReference => Target;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Credentials;

    public override bool RequiresRepoTree => RepoTree;
}
