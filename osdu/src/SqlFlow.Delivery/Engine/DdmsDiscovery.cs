using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Reads the DDMSs a flow names by their Register service registration and fills in what the flow does not declare
/// (osdu/specs/core/INTEGRATION.md section 2.7; openapi register v1, <c>GET /ddms/{id}</c>). A registration lists the
/// DDMS's interfaces, each an entity type with an OpenAPI 3 document that names exactly one server and one retrieval
/// operation (a <c>GET</c> carrying <c>x-ddms-retrieve-entity</c>). The server is where the DDMS is; the retrieval
/// path names the collection the entity type is served under. A registered entity type is free text (the contract's
/// example is <c>wellbore</c>), so a collection the DDMS's shape knows by its path takes the shape's entity type, and
/// with it whether the collection keeps bulk data and what its columns are checked against. The registration is read
/// once per protocol, when the flow's protocol is built, so a run checks its route against what the DDMS registered.
/// </summary>
public static partial class DdmsDiscovery
{
    /// <summary>Where the Register service reads a registration, under the flow's endpoint (openapi register v1, getDMS).</summary>
    public const string DefaultRegisterPath = "/api/register/v1/ddms/{id}";

    /// <summary>The extension that marks an interface's retrieval operation (register's HowToBecomeADDMS and RegisteredInterface).</summary>
    public const string RetrieveExtension = "x-ddms-retrieve-entity";

    /// <summary>Whether <paramref name="flow"/> names a DDMS whose registration has not been read.</summary>
    public static bool Needed(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Target.Ddms.Any(d => d.AwaitsDiscovery);
    }

    /// <summary>
    /// <paramref name="flow"/> with every DDMS it names by registration read from the Register service; the flow itself
    /// when it names none. Throws a <see cref="DeliveryException"/> when a registration is missing or does not describe a
    /// DDMS of the declared shape.
    /// </summary>
    public static async Task<FlowDefinition> ResolveAsync(FlowDefinition flow, OsduHttpClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(client);
        if (!Needed(flow))
        {
            return flow;
        }

        var services = new List<DdmsService>(flow.Target.Ddms.Count);
        foreach (var service in flow.Target.Ddms.Where(s => !s.AwaitsDiscovery))
        {
            services.Add(service);
        }

        foreach (var service in flow.Target.Ddms.Where(s => s.AwaitsDiscovery))
        {
            var registration = await ReadAsync(flow, client, service, ct).ConfigureAwait(false);
            var discovered = Describe(flow, service, registration);
            foreach (var collection in discovered.Collections)
            {
                if (services.FirstOrDefault(s => s.Collections.Any(c => Overlap(c, collection))) is { } other)
                {
                    throw new DeliveryException(
                        $"{KeyPaths.Where(flow)}: the registration '{service.Registration}' of target.ddms.{service.Name} serves {collection.EntityType}, "
                        + $"which target.ddms.{other.Name} serves as well. A record goes to one DDMS, so list the collections of one of them, leaving that entity type out.");
                }
            }

            services.Add(discovered);
        }

        // The order the flow declared them in is the order a record's entity type is looked up in.
        var ordered = flow.Target.Ddms.Select(declared => services.First(s => string.Equals(s.Name, declared.Name, StringComparison.Ordinal))).ToList();
        return flow with { Target = flow.Target with { Ddms = ordered } };
    }

    /// <summary>
    /// The DDMS <paramref name="declared"/> describes once its registration is read: the root and collections it
    /// declares, and the ones its registration names where it declares none.
    /// </summary>
    public static DdmsService Describe(FlowDefinition flow, DdmsService declared, JsonElement registration)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(declared);
        var where = $"{KeyPaths.Where(flow)}: the registration '{declared.Registration}' of target.ddms.{declared.Name}";
        if (registration.ValueKind != JsonValueKind.Object
            || !registration.TryGetProperty("interfaces", out var interfaces)
            || interfaces.ValueKind != JsonValueKind.Array
            || interfaces.GetArrayLength() == 0)
        {
            throw new DeliveryException($"{where} lists no interface, so it says nothing about what the DDMS serves or where it is.");
        }

        var roots = new HashSet<string>(StringComparer.Ordinal);
        var collections = new List<DdmsCollectionEntry>();
        foreach (var registered in interfaces.EnumerateArray())
        {
            var entityType = registered.ValueKind == JsonValueKind.Object && registered.TryGetProperty("entityType", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()!.Trim()
                : throw new DeliveryException($"{where} has an interface without an entityType.");
            var at = $"{where}, interface {entityType},";
            if (!registered.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            {
                throw new DeliveryException($"{at} carries no OpenAPI document.");
            }

            roots.Add(ServerRoot(schema, at));
            var segment = RetrievalSegment(schema, declared.Shape, at);
            var known = DdmsCatalog.KnownCollection(declared.Shape, segment);
            collections.Add(known ?? new DdmsCollectionEntry(entityType, segment, HasBulkWrite(schema, segment)));
        }

        var root = declared.Root;
        if (root is null)
        {
            if (roots.Count != 1)
            {
                throw new DeliveryException(
                    $"{where} names {roots.Count.ToString(CultureInfo.InvariantCulture)} servers ({string.Join(", ", roots)}), so where the DDMS is is ambiguous. "
                    + $"Declare it with target.ddms.{declared.Name}.root.");
            }

            root = roots.Single();
        }

        return declared with
        {
            Root = root,
            Collections = declared.Collections.Count > 0 ? declared.Collections : collections,
            Discovered = true,
        };
    }

    private static async Task<JsonElement> ReadAsync(FlowDefinition flow, OsduHttpClient client, DdmsService service, CancellationToken ct)
    {
        var url = client.Url(flow.Target.ProtocolOptions.RegisterPath ?? DefaultRegisterPath, service.Registration);
        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            throw new DeliveryException(
                $"{KeyPaths.Where(flow)}: the Register service ({url.AbsolutePath}) holds no DDMS registered as '{service.Registration}' in partition "
                + $"{client.Header(FlowMapper.PartitionHeader) ?? "(none)"}, which target.ddms.{service.Name}.register names.");
        }

        return OsduHttpClient.ParseJson(result, url);
    }

    /// <summary>
    /// Where the one server an interface's document names is: its URL with the variables' defaults put in, without a
    /// trailing '/'. A relative URL is a path under the flow's endpoint; an absolute one is used as it stands.
    /// </summary>
    private static string ServerRoot(JsonElement schema, string at)
    {
        if (!schema.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array || servers.GetArrayLength() != 1)
        {
            throw new DeliveryException($"{at} does not name exactly one server, as the Register service requires of a registered document.");
        }

        var server = servers[0];
        if (server.ValueKind != JsonValueKind.Object || !server.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String)
        {
            throw new DeliveryException($"{at} names a server without a url.");
        }

        var url = urlValue.GetString()!.Trim();
        foreach (Match variable in ServerVariable().Matches(url))
        {
            var name = variable.Groups["name"].Value;
            var value = server.TryGetProperty("variables", out var variables)
                && variables.ValueKind == JsonValueKind.Object
                && variables.TryGetProperty(name, out var declaredVariable)
                && declaredVariable.ValueKind == JsonValueKind.Object
                && declaredVariable.TryGetProperty("default", out var fallback)
                && fallback.ValueKind == JsonValueKind.String
                    ? fallback.GetString()!
                    : throw new DeliveryException($"{at} names the server {url}, whose variable {name} has no default.");
            url = url.Replace(variable.Value, value, StringComparison.Ordinal);
        }

        // Only a URL with a scheme is absolute: on Linux a bare path parses as an absolute file URI.
        if (url.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.Scheme is "http" or "https"
                ? url.TrimEnd('/')
                : throw new DeliveryException($"{at} names the server {url}, which is not an http(s) URL.");
        }

        var path = "/" + url.TrimStart('/');
        return path.Any(c => char.IsWhiteSpace(c) || c is '?' or '#')
            ? throw new DeliveryException($"{at} names the server {url}, which is not a URL or a path.")
            : path.TrimEnd('/');
    }

    /// <summary>The collection an interface's one retrieval operation reads from, which is where its records are served.</summary>
    private static string RetrievalSegment(JsonElement schema, DdmsShape shape, string at)
    {
        var retrievals = new List<string>();
        if (schema.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var path in paths.EnumerateObject())
            {
                if (path.Value.ValueKind == JsonValueKind.Object
                    && path.Value.TryGetProperty("get", out var get)
                    && get.ValueKind == JsonValueKind.Object
                    && get.TryGetProperty(RetrieveExtension, out _))
                {
                    retrievals.Add(path.Name);
                }
            }
        }

        if (retrievals.Count != 1)
        {
            throw new DeliveryException(
                $"{at} has {retrievals.Count.ToString(CultureInfo.InvariantCulture)} GET operations marked {RetrieveExtension}, and the Register service resolves a record through exactly one.");
        }

        var retrieval = retrievals[0];
        return shape switch
        {
            DdmsShape.WellboreDdmsV3 when WellboreDdmsV3Retrieval().Match(retrieval) is { Success: true } match => match.Groups["segment"].Value,
            DdmsShape.WellboreDdmsV3 => throw new DeliveryException(
                $"{at} retrieves records from {retrieval}, which is not a collection of a {shape} DDMS ({DdmsCatalog.WellboreDdmsV3Prefix}<collection>/{{id}})."),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "not a DDMS shape"),
        };
    }

    /// <summary>Whether a registered document writes bulk data for the records of <paramref name="segment"/>.</summary>
    private static bool HasBulkWrite(JsonElement schema, string segment)
    {
        if (!schema.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var prefix = DdmsCatalog.WellboreDdmsV3Prefix + segment + "/{";
        return paths.EnumerateObject().Any(path =>
            path.Name.StartsWith(prefix, StringComparison.Ordinal)
            && path.Name.EndsWith("}/data", StringComparison.Ordinal)
            && path.Value.ValueKind == JsonValueKind.Object
            && path.Value.TryGetProperty("post", out _));
    }

    /// <summary>Whether two collections serve the same records: the same entity type, or the same type where one names it alone.</summary>
    private static bool Overlap(DdmsCollectionEntry a, DdmsCollectionEntry b)
    {
        static string TypeOf(string entityType)
        {
            var separator = entityType.IndexOf("--", StringComparison.Ordinal);
            return separator < 0 ? entityType : entityType[(separator + 2)..];
        }

        var grouped = a.EntityType.Contains("--", StringComparison.Ordinal) && b.EntityType.Contains("--", StringComparison.Ordinal);
        return grouped
            ? string.Equals(a.EntityType, b.EntityType, StringComparison.OrdinalIgnoreCase)
            : string.Equals(TypeOf(a.EntityType), TypeOf(b.EntityType), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\{(?<name>[^{}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ServerVariable();

    [GeneratedRegex(@"^/ddms/v3/(?<segment>[A-Za-z0-9._-]{1,100})/\{[^{}/]+\}\z", RegexOptions.CultureInvariant)]
    private static partial Regex WellboreDdmsV3Retrieval();
}
