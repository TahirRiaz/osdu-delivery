using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// The dictionary documents (<c>documentType: dictionary</c>) cache flows hold, as the platform sees them: the CLI validates
/// one under its own type, and the proposal preflight checks one before it is pushed, instead of reporting it as a flow
/// document with no <c>flowType</c>.
/// </summary>
public sealed class DictionaryDocumentKind : ICompanionDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public DictionaryDocumentKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string DocumentType => DictionaryDefinition.DocumentTypeName;

    public string Description => "a lookup table a cache flow holds in the partition's cache";

    public string ParseCompanion(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var dictionary = _loader.ParseDictionary(yaml, source);
        return $"{dictionary.Name} ({dictionary.Entries.Count} {(dictionary.Entries.Count == 1 ? "entry" : "entries")}, key {dictionary.Key})";
    }
}
