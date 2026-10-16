using System.Collections;
using System.Reflection;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Lays the settings an interface declares over the ones its source declares for every interface, key by key: a value the
/// interface sets wins, a value it leaves out is the source's, a text map merges with the interface's entries winning, and
/// a nested block overlays the same way. A list is a value: the interface's list replaces the source's. The result is
/// always a copy, so what one interface's view changes never reaches the source's blocks or another interface. Only the
/// YAML shapes whose every setting is optional are overlaid, so "not set" is always told apart from a set value.
/// </summary>
internal static class YamlOverlay
{
    private static readonly MethodInfo ApplyMethod = typeof(YamlOverlay).GetMethod(nameof(Apply))!;

    public static T? Apply<T>(T? shared, T? over)
        where T : class, new()
    {
        if (shared is null && over is null)
        {
            return null;
        }

        var merged = new T();
        foreach (var property in Settings(typeof(T)))
        {
            property.SetValue(
                merged,
                Combine(property.PropertyType, shared is null ? null : property.GetValue(shared), over is null ? null : property.GetValue(over)));
        }

        return merged;
    }

    private static object? Combine(Type type, object? shared, object? over)
    {
        if (shared is null && over is null)
        {
            return null;
        }

        if (type == typeof(Dictionary<string, string>))
        {
            var merged = new Dictionary<string, string>();
            foreach (var source in new[] { shared, over })
            {
                foreach (var (key, value) in (Dictionary<string, string>?)source ?? [])
                {
                    merged[key] = value;
                }
            }

            return merged;
        }

        if (IsBlock(type))
        {
            return ApplyMethod.MakeGenericMethod(type).Invoke(null, [shared, over]);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            // A list is replaced, never merged, and never shared between the views that read it.
            return Activator.CreateInstance(type, (IEnumerable)(over ?? shared)!);
        }

        return over ?? shared;
    }

    /// <summary>A nested YAML block of this assembly, overlaid key by key rather than replaced.</summary>
    private static bool IsBlock(Type type)
        => type.IsClass && type.Assembly == typeof(YamlOverlay).Assembly && type.Name.EndsWith("Yaml", StringComparison.Ordinal)
           && type.GetConstructor(Type.EmptyTypes) is not null;

    private static IEnumerable<PropertyInfo> Settings(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetSetMethod() is null || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            // A setting that cannot be absent (a plain bool or number) cannot tell "left out" from its default, and would
            // silently reset the source's value: such a shape is not one to overlay.
            if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
            {
                throw new InvalidOperationException($"{type.Name}.{property.Name} is not optional, so {type.Name} cannot be overlaid.");
            }

            if (property.PropertyType != typeof(string) && !property.PropertyType.IsValueType && !IsBlock(property.PropertyType)
                && property.PropertyType != typeof(Dictionary<string, string>)
                && !(property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>)))
            {
                throw new InvalidOperationException($"{type.Name}.{property.Name} is a {property.PropertyType.Name}, which the overlay does not know how to copy.");
            }

            yield return property;
        }
    }
}
