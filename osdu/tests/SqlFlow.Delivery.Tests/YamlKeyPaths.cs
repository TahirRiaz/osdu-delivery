using System.Collections;
using System.Reflection;
using YamlDotNet.Serialization;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Every key path a YAML model accepts, read off the model the strict loader deserializes into, in the census's own
/// path form: <c>source.connection</c>, <c>mappings[].target</c>, <c>parameters.&lt;name&gt;.default</c>. A property of
/// type <see cref="object"/> is the author's own shape (a static value, a schedule the platform parses), so nothing below
/// it is listed; its path is marked in <see cref="FreeForm"/>.
/// </summary>
internal sealed class YamlKeyPaths
{
    private YamlKeyPaths(IReadOnlyDictionary<string, string> paths, IReadOnlySet<string> freeForm)
    {
        Paths = paths;
        FreeForm = freeForm;
    }

    /// <summary>Each path, with the CLR type its value is read as (<c>string</c>, <c>bool</c>, <c>object</c>, <c>list</c>, <c>map</c>).</summary>
    public IReadOnlyDictionary<string, string> Paths { get; }

    /// <summary>The paths whose value the model reads as a plain object: any shape is accepted below them.</summary>
    public IReadOnlySet<string> FreeForm { get; }

    public static YamlKeyPaths Of(Type model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var freeForm = new SortedSet<string>(StringComparer.Ordinal);
        Walk(model, string.Empty, paths, freeForm, []);
        return new YamlKeyPaths(paths, freeForm);
    }

    private static void Walk(Type type, string prefix, SortedDictionary<string, string> paths, SortedSet<string> freeForm, HashSet<Type> visiting)
    {
        if (!visiting.Add(type))
        {
            throw new InvalidOperationException($"{type.Name} contains itself at '{prefix}', which a YAML model never does.");
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<YamlIgnoreAttribute>() is not null || property.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            var alias = property.GetCustomAttribute<YamlMemberAttribute>()?.Alias;
            var name = alias ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            Value(property.PropertyType, prefix.Length == 0 ? name : prefix + "." + name, paths, freeForm, visiting);
        }

        visiting.Remove(type);
    }

    private static void Value(Type type, string path, SortedDictionary<string, string> paths, SortedSet<string> freeForm, HashSet<Type> visiting)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(object))
        {
            paths[path] = "object";
            freeForm.Add(path);
            return;
        }

        if (IsScalar(underlying))
        {
            paths[path] = underlying.IsEnum ? "enum" : underlying == typeof(bool) ? "bool" : IsNumber(underlying) ? "number" : "string";
            return;
        }

        if (MapValue(underlying) is { } mapValue)
        {
            paths[path] = "map";
            Value(mapValue, path + ".<name>", paths, freeForm, visiting);
            return;
        }

        if (ListItem(underlying) is { } item)
        {
            paths[path] = "list";
            Value(item, path + "[]", paths, freeForm, visiting);
            return;
        }

        paths[path] = "object";
        Walk(underlying, path, paths, freeForm, visiting);
    }

    private static bool IsScalar(Type type)
        => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan);

    private static bool IsNumber(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal) || type == typeof(float) || type == typeof(short);

    private static Type? MapValue(Type type)
    {
        var map = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>) && i.GetGenericArguments()[0] == typeof(string));
        return map?.GetGenericArguments()[1];
    }

    private static Type? ListItem(Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return null;
        }

        var list = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));
        return list?.GetGenericArguments()[0];
    }
}
