using System.Text;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine;

/// <summary>What a fixtures update did to one mapping file: where it is, what happened to each fixture, whether it was written.</summary>
/// <param name="Mapping">The mapping, as <c>Name@version</c>.</param>
/// <param name="Path">The mapping file.</param>
/// <param name="Rewrite">The document with its expected records written again, and each fixture's outcome.</param>
/// <param name="Written">True when the file on disk now holds <see cref="FixtureRewrite.Text"/>.</param>
public sealed record FixtureUpdate(string Mapping, string Path, FixtureRewrite Rewrite, bool Written);

/// <summary>
/// <c>sqlflow fixtures update</c>: renders a flow's mapping's fixtures under its pinned template and cache, exactly as the
/// preflight gate renders them, and writes each into its <c>expected</c> block. Everything but the fixtures is checked
/// first, so a mapping the gate would refuse for another reason is refused here too, and nothing is written for it.
/// The file is written whole, through a temporary file beside it, and read back before it replaces the original, so an
/// interrupted or broken update never leaves a half-written mapping.
/// </summary>
public static class FixtureUpdates
{
    /// <param name="engine">What the flow's mapping resolves against: its documents, templates and cache.</param>
    /// <param name="flow">The flow (or one interface of a source) whose mapping is updated.</param>
    /// <param name="write">False reports what would change and writes nothing.</param>
    /// <param name="ct">Cancels the resolution.</param>
    public static async Task<FixtureUpdate> UpdateAsync(EngineContext engine, FlowDefinition flow, bool write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(flow);

        // A fixture declares what every search it asks is answered with, so no platform search is needed or made.
        var mappings = new MappingCatalog(DeliveryLayout.Resolve(flow).MappingsDirectory, engine.Documents);
        var resolved = await new RenderResolver(mappings, engine.Cache, engine.Templates, engine.Secrets).ResolveAsync(flow, checkFixtures: false, ct).ConfigureAwait(false);
        var mapping = resolved.Mapping;
        var path = mapping.SourcePath
            ?? throw new FlowValidationException($"{KeyPaths.Where(flow)}: mapping {mapping.Reference} was not read from a file, so there is no file to write its fixtures into.");

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var yaml = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bom ? bytes[3..] : bytes);
        var rewrite = FixtureRewriter.Rewrite(yaml, mapping, Preflight.RenderFixtures(mapping, resolved.Renderer), path);
        if (!write || !rewrite.Changed)
        {
            return new FixtureUpdate(mapping.Reference, path, rewrite, Written: false);
        }

        // What is written has to read back as the same mapping with the same fixtures, or nothing is written.
        var reread = engine.Documents.ParseMapping(rewrite.Text, path);
        if (reread.Fixtures.Count != mapping.Fixtures.Count)
        {
            throw new DeliveryException($"{path}: the updated document reads {reread.Fixtures.Count} fixture(s) where it held {mapping.Fixtures.Count}; nothing was written.");
        }

        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var encoded = new UTF8Encoding(bom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(rewrite.Text)).ToArray();
            await File.WriteAllBytesAsync(temporary, encoded, ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return new FixtureUpdate(mapping.Reference, path, rewrite, Written: true);
    }
}
