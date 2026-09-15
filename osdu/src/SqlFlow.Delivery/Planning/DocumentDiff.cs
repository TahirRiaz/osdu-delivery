using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Planning;

/// <summary>
/// A path-level diff of two documents (design.md section 10.5: plan shows the document diff between contexts).
/// Reports added, removed and changed leaf paths in canonical order.
/// </summary>
public static class DocumentDiff
{
    public static string Compute(JsonNode? expected, JsonNode? actual)
    {
        var left = Flatten(CanonicalJson.Normalize(expected));
        var right = Flatten(CanonicalJson.Normalize(actual));
        var sb = new StringBuilder();
        foreach (var path in left.Keys.Union(right.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var hasLeft = left.TryGetValue(path, out var l);
            var hasRight = right.TryGetValue(path, out var r);
            if (hasLeft && hasRight)
            {
                if (!string.Equals(l, r, StringComparison.Ordinal))
                {
                    sb.Append("~ ").Append(path).Append(": ").Append(l).Append(" -> ").Append(r).AppendLine();
                }
            }
            else if (hasLeft)
            {
                sb.Append("- ").Append(path).Append(": ").Append(l).AppendLine();
            }
            else
            {
                sb.Append("+ ").Append(path).Append(": ").Append(r).AppendLine();
            }
        }

        return sb.Length == 0 ? "(no differences)" : sb.ToString().TrimEnd();
    }

    public static int CountChanges(JsonNode? expected, JsonNode? actual)
    {
        var left = Flatten(CanonicalJson.Normalize(expected));
        var right = Flatten(CanonicalJson.Normalize(actual));
        var changes = 0;
        foreach (var path in left.Keys.Union(right.Keys))
        {
            if (!left.TryGetValue(path, out var l) || !right.TryGetValue(path, out var r) || !string.Equals(l, r, StringComparison.Ordinal))
            {
                changes++;
            }
        }

        return changes;
    }

    private static Dictionary<string, string> Flatten(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(node, string.Empty, result);
        return result;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string> into)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count > 0:
                foreach (var kv in obj)
                {
                    Walk(kv.Value, path.Length == 0 ? kv.Key : path + "." + kv.Key, into);
                }

                break;
            case JsonArray arr when arr.Count > 0:
                for (var i = 0; i < arr.Count; i++)
                {
                    Walk(arr[i], $"{path}[{i}]", into);
                }

                break;
            default:
                into[path.Length == 0 ? "$" : path] = node is null ? "null" : CanonicalJson.ToString(node);
                break;
        }
    }
}
