using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;

namespace SqlFlow.Execution;

/// <summary>Loads any flow document for execution, applying the secret-hygiene check that both <c>validate</c>
/// and <c>run</c> rely on. This is the one load path the CLI's verbs and the shared <see cref="DocumentExecutor"/>
/// go through, so a document loads under exactly the same rules wherever it runs.</summary>
public static class DocumentLoader
{
    /// <summary>Loads any flow document. Every load also runs the secret-hygiene check over the document's
    /// credential references, so an embedded credential warns on validate AND run, before it reaches a commit.
    /// The warning is surfaced through <paramref name="onSecretWarning"/>; a null sink loads silently. The value
    /// itself is never echoed.</summary>
    public static FlowDocument Load(YamlDocumentLoader documents, string file, Action<string>? onSecretWarning = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var document = documents.LoadFile(file);
        if (onSecretWarning is not null)
        {
            foreach (var (alias, reference) in document.CredentialReferences)
            {
                if (SecretHygiene.LooksLikeEmbeddedSecret(reference))
                {
                    onSecretWarning(SecretHygiene.Warning(alias, file));
                }
            }
        }

        return document;
    }
}
