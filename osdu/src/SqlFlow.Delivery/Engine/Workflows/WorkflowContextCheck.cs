using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Workflows;

/// <summary>
/// Checks an execution context against the payload contract of the workflow it is sent to (<see cref="WorkflowCatalog"/>):
/// before the flow runs, what its template can be seen to hold; before each trigger, the context as it is sent. A
/// context the workflow would fail on is never sent, and the record says why.
/// </summary>
public static class WorkflowContextCheck
{
    /// <summary>
    /// What a template shows before any record fills it: every key the contract requires is there, no credential is
    /// written into the document, and every value that is not a placeholder has the shape the workflow reads.
    /// </summary>
    public static IReadOnlyList<string> CheckTemplate(WorkflowContract contract, JsonObject template, bool addsPayload, string where)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(template);
        var problems = new List<string>();
        if (!contract.Deliverable)
        {
            problems.Add($"{where}: {contract.NotDeliverable} ({contract.Source}).");
            return problems;
        }

        foreach (var key in contract.Keys)
        {
            var node = Find(template, key.Path, out var present);
            if (!present)
            {
                var suppliedByRoute = addsPayload && key.Path.StartsWith("Payload/", StringComparison.Ordinal);
                if (key.Required && !suppliedByRoute)
                {
                    problems.Add($"{where}: the {contract.Name} contract requires '{Display(key.Path)}' ({key.Source}), and the context does not set it.");
                }

                continue;
            }

            var placeholders = node is JsonValue value && value.TryGetValue<string>(out var text) ? WorkflowTemplate.Parse(text) : [];
            if (key.Secret)
            {
                var single = node is JsonValue v && v.TryGetValue<string>(out var t) && placeholders.Count == 1 && placeholders[0].Length == t.Length;
                if (!single || placeholders[0].Name != "secret")
                {
                    problems.Add($"{where}: '{Display(key.Path)}' is a credential ({key.Source}); give it as {{secret:name}} with the reference under secrets, never as a value in the document.");
                }

                continue;
            }

            if (placeholders.Count == 0 && ShapeProblem(key, node) is { } shape)
            {
                problems.Add($"{where}: {shape}");
            }
        }

        foreach (var group in contract.AtLeastOne)
        {
            if (!group.Any(k => Find(template, k, out var present) is not null || present))
            {
                problems.Add($"{where}: the {contract.Name} contract reads at least one of {string.Join(", ", group)} ({contract.Source}), and the context sets none.");
            }
        }

        foreach (var group in contract.AllOrNone)
        {
            var set = group.Count(k => { Find(template, k, out var present); return present; });
            if (set != 0 && set != group.Count)
            {
                problems.Add($"{where}: the {contract.Name} contract takes all of {string.Join(", ", group)} or none of them ({contract.Source}); the context sets {set.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
            }
        }

        return problems;
    }

    /// <summary>
    /// What the context a trigger is about to send breaks of the contract; empty when it holds. Secrets are checked for
    /// presence only, from the redacted context, so no secret value ever reaches a message.
    /// </summary>
    public static IReadOnlyList<string> CheckContext(WorkflowContract contract, JsonObject context)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(context);
        var problems = new List<string>();
        foreach (var key in contract.Keys)
        {
            var node = Find(context, key.Path, out var present);
            if (!present || node is null)
            {
                if (key.Required)
                {
                    problems.Add($"'{Display(key.Path)}' is required by {contract.Name} ({key.Source}) and is not set");
                }

                continue;
            }

            if (key.Secret)
            {
                if (node is not JsonValue secret || !secret.TryGetValue<string>(out var redacted) || redacted.Length == 0)
                {
                    problems.Add($"'{Display(key.Path)}' is a credential and must be text ({key.Source})");
                }

                continue;
            }

            if (ShapeProblem(key, node) is { } shape)
            {
                problems.Add(shape);
            }
        }

        foreach (var group in contract.AtLeastOne)
        {
            if (!group.Any(k => Find(context, k, out var present) is not null && present))
            {
                problems.Add($"one of {string.Join(", ", group)} is required by {contract.Name} ({contract.Source}) and none is set");
            }
        }

        foreach (var group in contract.AllOrNone)
        {
            var set = group.Count(k => Find(context, k, out var present) is JsonValue v && present && !(v.TryGetValue<string>(out var s) && s.Length == 0));
            if (set != 0 && set != group.Count)
            {
                problems.Add($"{contract.Name} takes all of {string.Join(", ", group)} or none of them ({contract.Source})");
            }
        }

        return problems;
    }

    /// <summary>Why a value does not have the shape the contract gives its key, or null when it does.</summary>
    private static string? ShapeProblem(ContextKey key, JsonNode? node)
    {
        var name = Display(key.Path);
        var kind = node?.GetValueKind() ?? JsonValueKind.Null;
        var fits = key.Type switch
        {
            ContextValueType.Any => true,
            ContextValueType.Text => kind == JsonValueKind.String,
            ContextValueType.TextOrList => kind == JsonValueKind.String || (kind == JsonValueKind.Array && ((JsonArray)node!).All(i => i?.GetValueKind() == JsonValueKind.String)),
            ContextValueType.List => kind == JsonValueKind.Array,
            ContextValueType.Map => kind == JsonValueKind.Object,
            ContextValueType.MapOrList => kind == JsonValueKind.Object || (kind == JsonValueKind.Array && ((JsonArray)node!).All(i => i?.GetValueKind() == JsonValueKind.Object)),
            ContextValueType.Flag => kind is JsonValueKind.True or JsonValueKind.False,
            ContextValueType.Number => kind == JsonValueKind.Number,
            _ => false,
        };
        if (!fits)
        {
            return $"'{name}' must be {Describe(key.Type)} ({key.Source}), and it is {Describe(kind)}";
        }

        if (key.NotEmpty && ((kind == JsonValueKind.String && node!.GetValue<string>().Length == 0) || (kind == JsonValueKind.Array && ((JsonArray)node!).Count == 0)))
        {
            return $"'{name}' must not be empty ({key.Source})";
        }

        if (kind == JsonValueKind.String)
        {
            var text = node!.GetValue<string>();
            if (key.Allowed is { } allowed && !allowed.Contains(text, StringComparer.Ordinal))
            {
                return $"'{name}' is '{text}', and {key.Source} allows {string.Join(" or ", allowed)}";
            }

            if (key.Pattern is { } pattern && !Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                return $"'{name}' is '{text}', which does not match {pattern} ({key.Source})";
            }
        }

        return null;
    }

    /// <summary>The node at a <c>/</c>-separated key path, and whether the path is there at all (a key set to null is there).</summary>
    private static JsonNode? Find(JsonObject root, string path, out bool present)
    {
        JsonNode? current = root;
        present = false;
        foreach (var part in path.Split('/'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out var child))
            {
                return null;
            }

            current = child;
        }

        present = true;
        return current;
    }

    private static string Display(string path) => path.Replace('/', '.');

    private static string Describe(ContextValueType type) => type switch
    {
        ContextValueType.Text => "text",
        ContextValueType.TextOrList => "text or a list of text",
        ContextValueType.List => "a list",
        ContextValueType.Map => "an object",
        ContextValueType.MapOrList => "an object or a list of objects",
        ContextValueType.Flag => "true or false",
        ContextValueType.Number => "a number",
        _ => "a value",
    };

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "text",
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        JsonValueKind.True or JsonValueKind.False => "a flag",
        JsonValueKind.Number => "a number",
        _ => "null",
    };
}
