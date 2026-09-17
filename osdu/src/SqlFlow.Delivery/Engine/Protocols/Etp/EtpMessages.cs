// Written from the pinned ETP 1.2 protocol, osdu/specs/reservoir-ddms/etp-1.2.avpr: one C# record per message of the
// protocols this route speaks (Core 0, Discovery 3, Store 4, DataArray 9, Transaction 18, Dataspace 24 and
// DataspaceOSDU 2424), its properties in the schema's field order. EtpSchemaTests round-trips every one of them
// against a codec driven by that same file, so this file and the schema cannot drift apart.

using System.Collections.ObjectModel;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The ETP 1.2 <c>Core.RequestSession</c> message (protocol 0, message type 1).
/// </summary>
public sealed record RequestSession : IEtpMessage
{
    public required string ApplicationName { get; init; }

    public required string ApplicationVersion { get; init; }

    public required Guid ClientInstanceId { get; init; }

    public required IReadOnlyList<SupportedProtocol> RequestedProtocols { get; init; }

    public required IReadOnlyList<SupportedDataObject> SupportedDataObjects { get; init; }

    public IReadOnlyList<string> SupportedCompression { get; init; } = [];

    public IReadOnlyList<string> SupportedFormats { get; init; } = ["xml"];

    public required long CurrentDateTime { get; init; }

    public required long EarliestRetainedChangeTime { get; init; }

    public bool ServerAuthorizationRequired { get; init; }

    public IReadOnlyDictionary<string, DataValue> EndpointCapabilities { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public int Protocol => 0;

    public int MessageType => 1;

    public string MessageName => "Core.RequestSession";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(ApplicationName);
        writer.WriteString(ApplicationVersion);
        writer.WriteUuid(ClientInstanceId);

        writer.WriteBlockHeader(RequestedProtocols.Count);
        foreach (var requestedProtocolsItem in RequestedProtocols)
        {
            requestedProtocolsItem.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(SupportedDataObjects.Count);
        foreach (var supportedDataObjectsItem in SupportedDataObjects)
        {
            supportedDataObjectsItem.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(SupportedCompression.Count);
        foreach (var supportedCompressionItem in SupportedCompression)
        {
            writer.WriteString(supportedCompressionItem);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(SupportedFormats.Count);
        foreach (var supportedFormatsItem in SupportedFormats)
        {
            writer.WriteString(supportedFormatsItem);
        }
        writer.WriteBlockEnd();

        writer.WriteLong(CurrentDateTime);
        writer.WriteLong(EarliestRetainedChangeTime);
        writer.WriteBoolean(ServerAuthorizationRequired);

        writer.WriteBlockHeader(EndpointCapabilities.Count);
        foreach (var (endpointCapabilitiesKey, endpointCapabilitiesValue) in EndpointCapabilities)
        {
            writer.WriteString(endpointCapabilitiesKey);
            endpointCapabilitiesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static RequestSession Read(ref EtpReader reader)
    {
        var applicationName = reader.ReadString();
        var applicationVersion = reader.ReadString();
        var clientInstanceId = reader.ReadUuid();

        var requestedProtocols = new List<SupportedProtocol>();
        for (var requestedProtocolsCount = reader.ReadBlockCount(); requestedProtocolsCount != 0; requestedProtocolsCount = reader.ReadBlockCount())
        {
            requestedProtocols.EnsureCapacity(requestedProtocols.Count + requestedProtocolsCount);
            for (var requestedProtocolsIndex = 0; requestedProtocolsIndex < requestedProtocolsCount; requestedProtocolsIndex++)
            {
                requestedProtocols.Add(SupportedProtocol.Read(ref reader));
            }
        }

        var supportedDataObjects = new List<SupportedDataObject>();
        for (var supportedDataObjectsCount = reader.ReadBlockCount(); supportedDataObjectsCount != 0; supportedDataObjectsCount = reader.ReadBlockCount())
        {
            supportedDataObjects.EnsureCapacity(supportedDataObjects.Count + supportedDataObjectsCount);
            for (var supportedDataObjectsIndex = 0; supportedDataObjectsIndex < supportedDataObjectsCount; supportedDataObjectsIndex++)
            {
                supportedDataObjects.Add(SupportedDataObject.Read(ref reader));
            }
        }

        var supportedCompression = new List<string>();
        for (var supportedCompressionCount = reader.ReadBlockCount(); supportedCompressionCount != 0; supportedCompressionCount = reader.ReadBlockCount())
        {
            supportedCompression.EnsureCapacity(supportedCompression.Count + supportedCompressionCount);
            for (var supportedCompressionIndex = 0; supportedCompressionIndex < supportedCompressionCount; supportedCompressionIndex++)
            {
                supportedCompression.Add(reader.ReadString());
            }
        }

        var supportedFormats = new List<string>();
        for (var supportedFormatsCount = reader.ReadBlockCount(); supportedFormatsCount != 0; supportedFormatsCount = reader.ReadBlockCount())
        {
            supportedFormats.EnsureCapacity(supportedFormats.Count + supportedFormatsCount);
            for (var supportedFormatsIndex = 0; supportedFormatsIndex < supportedFormatsCount; supportedFormatsIndex++)
            {
                supportedFormats.Add(reader.ReadString());
            }
        }

        var currentDateTime = reader.ReadLong();
        var earliestRetainedChangeTime = reader.ReadLong();
        var serverAuthorizationRequired = reader.ReadBoolean();

        var endpointCapabilities = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var endpointCapabilitiesCount = reader.ReadBlockCount(); endpointCapabilitiesCount != 0; endpointCapabilitiesCount = reader.ReadBlockCount())
        {
            for (var endpointCapabilitiesIndex = 0; endpointCapabilitiesIndex < endpointCapabilitiesCount; endpointCapabilitiesIndex++)
            {
                var endpointCapabilitiesKey = reader.ReadString();
                endpointCapabilities[endpointCapabilitiesKey] = DataValue.Read(ref reader);
            }
        }

        return new RequestSession
        {
            ApplicationName = applicationName,
            ApplicationVersion = applicationVersion,
            ClientInstanceId = clientInstanceId,
            RequestedProtocols = requestedProtocols,
            SupportedDataObjects = supportedDataObjects,
            SupportedCompression = supportedCompression,
            SupportedFormats = supportedFormats,
            CurrentDateTime = currentDateTime,
            EarliestRetainedChangeTime = earliestRetainedChangeTime,
            ServerAuthorizationRequired = serverAuthorizationRequired,
            EndpointCapabilities = endpointCapabilities,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.OpenSession</c> message (protocol 0, message type 2).
/// </summary>
public sealed record OpenSession : IEtpMessage
{
    public required string ApplicationName { get; init; }

    public required string ApplicationVersion { get; init; }

    public required Guid ServerInstanceId { get; init; }

    public required IReadOnlyList<SupportedProtocol> SupportedProtocols { get; init; }

    public required IReadOnlyList<SupportedDataObject> SupportedDataObjects { get; init; }

    public string SupportedCompression { get; init; } = string.Empty;

    public IReadOnlyList<string> SupportedFormats { get; init; } = ["xml"];

    public required long CurrentDateTime { get; init; }

    public required long EarliestRetainedChangeTime { get; init; }

    public required Guid SessionId { get; init; }

    public IReadOnlyDictionary<string, DataValue> EndpointCapabilities { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public int Protocol => 0;

    public int MessageType => 2;

    public string MessageName => "Core.OpenSession";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(ApplicationName);
        writer.WriteString(ApplicationVersion);
        writer.WriteUuid(ServerInstanceId);

        writer.WriteBlockHeader(SupportedProtocols.Count);
        foreach (var supportedProtocolsItem in SupportedProtocols)
        {
            supportedProtocolsItem.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(SupportedDataObjects.Count);
        foreach (var supportedDataObjectsItem in SupportedDataObjects)
        {
            supportedDataObjectsItem.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteString(SupportedCompression);

        writer.WriteBlockHeader(SupportedFormats.Count);
        foreach (var supportedFormatsItem in SupportedFormats)
        {
            writer.WriteString(supportedFormatsItem);
        }
        writer.WriteBlockEnd();

        writer.WriteLong(CurrentDateTime);
        writer.WriteLong(EarliestRetainedChangeTime);
        writer.WriteUuid(SessionId);

        writer.WriteBlockHeader(EndpointCapabilities.Count);
        foreach (var (endpointCapabilitiesKey, endpointCapabilitiesValue) in EndpointCapabilities)
        {
            writer.WriteString(endpointCapabilitiesKey);
            endpointCapabilitiesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static OpenSession Read(ref EtpReader reader)
    {
        var applicationName = reader.ReadString();
        var applicationVersion = reader.ReadString();
        var serverInstanceId = reader.ReadUuid();

        var supportedProtocols = new List<SupportedProtocol>();
        for (var supportedProtocolsCount = reader.ReadBlockCount(); supportedProtocolsCount != 0; supportedProtocolsCount = reader.ReadBlockCount())
        {
            supportedProtocols.EnsureCapacity(supportedProtocols.Count + supportedProtocolsCount);
            for (var supportedProtocolsIndex = 0; supportedProtocolsIndex < supportedProtocolsCount; supportedProtocolsIndex++)
            {
                supportedProtocols.Add(SupportedProtocol.Read(ref reader));
            }
        }

        var supportedDataObjects = new List<SupportedDataObject>();
        for (var supportedDataObjectsCount = reader.ReadBlockCount(); supportedDataObjectsCount != 0; supportedDataObjectsCount = reader.ReadBlockCount())
        {
            supportedDataObjects.EnsureCapacity(supportedDataObjects.Count + supportedDataObjectsCount);
            for (var supportedDataObjectsIndex = 0; supportedDataObjectsIndex < supportedDataObjectsCount; supportedDataObjectsIndex++)
            {
                supportedDataObjects.Add(SupportedDataObject.Read(ref reader));
            }
        }

        var supportedCompression = reader.ReadString();

        var supportedFormats = new List<string>();
        for (var supportedFormatsCount = reader.ReadBlockCount(); supportedFormatsCount != 0; supportedFormatsCount = reader.ReadBlockCount())
        {
            supportedFormats.EnsureCapacity(supportedFormats.Count + supportedFormatsCount);
            for (var supportedFormatsIndex = 0; supportedFormatsIndex < supportedFormatsCount; supportedFormatsIndex++)
            {
                supportedFormats.Add(reader.ReadString());
            }
        }

        var currentDateTime = reader.ReadLong();
        var earliestRetainedChangeTime = reader.ReadLong();
        var sessionId = reader.ReadUuid();

        var endpointCapabilities = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var endpointCapabilitiesCount = reader.ReadBlockCount(); endpointCapabilitiesCount != 0; endpointCapabilitiesCount = reader.ReadBlockCount())
        {
            for (var endpointCapabilitiesIndex = 0; endpointCapabilitiesIndex < endpointCapabilitiesCount; endpointCapabilitiesIndex++)
            {
                var endpointCapabilitiesKey = reader.ReadString();
                endpointCapabilities[endpointCapabilitiesKey] = DataValue.Read(ref reader);
            }
        }

        return new OpenSession
        {
            ApplicationName = applicationName,
            ApplicationVersion = applicationVersion,
            ServerInstanceId = serverInstanceId,
            SupportedProtocols = supportedProtocols,
            SupportedDataObjects = supportedDataObjects,
            SupportedCompression = supportedCompression,
            SupportedFormats = supportedFormats,
            CurrentDateTime = currentDateTime,
            EarliestRetainedChangeTime = earliestRetainedChangeTime,
            SessionId = sessionId,
            EndpointCapabilities = endpointCapabilities,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.CloseSession</c> message (protocol 0, message type 5).
/// </summary>
public sealed record CloseSession : IEtpMessage
{
    public string Reason { get; init; } = string.Empty;

    public int Protocol => 0;

    public int MessageType => 5;

    public string MessageName => "Core.CloseSession";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Reason);
    }

    public static CloseSession Read(ref EtpReader reader)
    {
        var reason = reader.ReadString();

        return new CloseSession
        {
            Reason = reason,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.Authorize</c> message (protocol 0, message type 6).
/// </summary>
public sealed record Authorize : IEtpMessage
{
    public required string Authorization { get; init; }

    public required IReadOnlyDictionary<string, string> SupplementalAuthorization { get; init; }

    public int Protocol => 0;

    public int MessageType => 6;

    public string MessageName => "Core.Authorize";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Authorization);

        writer.WriteBlockHeader(SupplementalAuthorization.Count);
        foreach (var (supplementalAuthorizationKey, supplementalAuthorizationValue) in SupplementalAuthorization)
        {
            writer.WriteString(supplementalAuthorizationKey);
            writer.WriteString(supplementalAuthorizationValue);
        }
        writer.WriteBlockEnd();
    }

    public static Authorize Read(ref EtpReader reader)
    {
        var authorization = reader.ReadString();

        var supplementalAuthorization = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var supplementalAuthorizationCount = reader.ReadBlockCount(); supplementalAuthorizationCount != 0; supplementalAuthorizationCount = reader.ReadBlockCount())
        {
            for (var supplementalAuthorizationIndex = 0; supplementalAuthorizationIndex < supplementalAuthorizationCount; supplementalAuthorizationIndex++)
            {
                var supplementalAuthorizationKey = reader.ReadString();
                supplementalAuthorization[supplementalAuthorizationKey] = reader.ReadString();
            }
        }

        return new Authorize
        {
            Authorization = authorization,
            SupplementalAuthorization = supplementalAuthorization,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.AuthorizeResponse</c> message (protocol 0, message type 7).
/// </summary>
public sealed record AuthorizeResponse : IEtpMessage
{
    public required bool Success { get; init; }

    public required IReadOnlyList<string> Challenges { get; init; }

    public int Protocol => 0;

    public int MessageType => 7;

    public string MessageName => "Core.AuthorizeResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBoolean(Success);

        writer.WriteBlockHeader(Challenges.Count);
        foreach (var challengesItem in Challenges)
        {
            writer.WriteString(challengesItem);
        }
        writer.WriteBlockEnd();
    }

    public static AuthorizeResponse Read(ref EtpReader reader)
    {
        var success = reader.ReadBoolean();

        var challenges = new List<string>();
        for (var challengesCount = reader.ReadBlockCount(); challengesCount != 0; challengesCount = reader.ReadBlockCount())
        {
            challenges.EnsureCapacity(challenges.Count + challengesCount);
            for (var challengesIndex = 0; challengesIndex < challengesCount; challengesIndex++)
            {
                challenges.Add(reader.ReadString());
            }
        }

        return new AuthorizeResponse
        {
            Success = success,
            Challenges = challenges,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.Ping</c> message (protocol 0, message type 8).
/// </summary>
public sealed record Ping : IEtpMessage
{
    public required long CurrentDateTime { get; init; }

    public int Protocol => 0;

    public int MessageType => 8;

    public string MessageName => "Core.Ping";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLong(CurrentDateTime);
    }

    public static Ping Read(ref EtpReader reader)
    {
        var currentDateTime = reader.ReadLong();

        return new Ping
        {
            CurrentDateTime = currentDateTime,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.Pong</c> message (protocol 0, message type 9).
/// </summary>
public sealed record Pong : IEtpMessage
{
    public required long CurrentDateTime { get; init; }

    public int Protocol => 0;

    public int MessageType => 9;

    public string MessageName => "Core.Pong";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLong(CurrentDateTime);
    }

    public static Pong Read(ref EtpReader reader)
    {
        var currentDateTime = reader.ReadLong();

        return new Pong
        {
            CurrentDateTime = currentDateTime,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.ProtocolException</c> message (protocol 0, message type 1000).
/// </summary>
public sealed record ProtocolFailure : IEtpMessage
{
    public ErrorInfo? Error { get; init; }

    public IReadOnlyDictionary<string, ErrorInfo> Errors { get; init; } = ReadOnlyDictionary<string, ErrorInfo>.Empty;

    public int Protocol => 0;

    public int MessageType => 1000;

    public string MessageName => "Core.ProtocolException";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Error is { } errorValue)
        {
            writer.WriteUnion(1);
            errorValue.Write(writer);
        }
        else
        {
            writer.WriteUnion(0);
        }

        writer.WriteBlockHeader(Errors.Count);
        foreach (var (errorsKey, errorsValue) in Errors)
        {
            writer.WriteString(errorsKey);
            errorsValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static ProtocolFailure Read(ref EtpReader reader)
    {
        ErrorInfo? error = reader.ReadUnion(2) == 1 ? ErrorInfo.Read(ref reader) : null;

        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        for (var errorsCount = reader.ReadBlockCount(); errorsCount != 0; errorsCount = reader.ReadBlockCount())
        {
            for (var errorsIndex = 0; errorsIndex < errorsCount; errorsIndex++)
            {
                var errorsKey = reader.ReadString();
                errors[errorsKey] = ErrorInfo.Read(ref reader);
            }
        }

        return new ProtocolFailure
        {
            Error = error,
            Errors = errors,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.Acknowledge</c> message (protocol 0, message type 1001).
/// </summary>
public sealed record Acknowledge : IEtpMessage
{
    public int Protocol => 0;

    public int MessageType => 1001;

    public string MessageName => "Core.Acknowledge";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
    }

    public static Acknowledge Read(ref EtpReader reader)
    {
        return new Acknowledge
        {
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.GetDataspaces</c> message (protocol 24, message type 1).
/// </summary>
public sealed record GetDataspaces : IEtpMessage
{
    public long? StoreLastWriteFilter { get; init; }

    public int Protocol => 24;

    public int MessageType => 1;

    public string MessageName => "Dataspace.GetDataspaces";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (StoreLastWriteFilter is { } storeLastWriteFilterValue)
        {
            writer.WriteUnion(1);
            writer.WriteLong(storeLastWriteFilterValue);
        }
        else
        {
            writer.WriteUnion(0);
        }
    }

    public static GetDataspaces Read(ref EtpReader reader)
    {
        long? storeLastWriteFilter = reader.ReadUnion(2) == 1 ? reader.ReadLong() : (long?)null;

        return new GetDataspaces
        {
            StoreLastWriteFilter = storeLastWriteFilter,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.GetDataspacesResponse</c> message (protocol 24, message type 2).
/// </summary>
public sealed record GetDataspacesResponse : IEtpMessage
{
    public IReadOnlyList<Dataspace> Dataspaces { get; init; } = [];

    public int Protocol => 24;

    public int MessageType => 2;

    public string MessageName => "Dataspace.GetDataspacesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Dataspaces.Count);
        foreach (var dataspacesItem in Dataspaces)
        {
            dataspacesItem.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataspacesResponse Read(ref EtpReader reader)
    {
        var dataspaces = new List<Dataspace>();
        for (var dataspacesCount = reader.ReadBlockCount(); dataspacesCount != 0; dataspacesCount = reader.ReadBlockCount())
        {
            dataspaces.EnsureCapacity(dataspaces.Count + dataspacesCount);
            for (var dataspacesIndex = 0; dataspacesIndex < dataspacesCount; dataspacesIndex++)
            {
                dataspaces.Add(Dataspace.Read(ref reader));
            }
        }

        return new GetDataspacesResponse
        {
            Dataspaces = dataspaces,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.PutDataspaces</c> message (protocol 24, message type 3).
/// </summary>
public sealed record PutDataspaces : IEtpMessage
{
    public required IReadOnlyDictionary<string, Dataspace> Dataspaces { get; init; }

    public int Protocol => 24;

    public int MessageType => 3;

    public string MessageName => "Dataspace.PutDataspaces";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Dataspaces.Count);
        foreach (var (dataspacesKey, dataspacesValue) in Dataspaces)
        {
            writer.WriteString(dataspacesKey);
            dataspacesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataspaces Read(ref EtpReader reader)
    {
        var dataspaces = new Dictionary<string, Dataspace>(StringComparer.Ordinal);
        for (var dataspacesCount = reader.ReadBlockCount(); dataspacesCount != 0; dataspacesCount = reader.ReadBlockCount())
        {
            for (var dataspacesIndex = 0; dataspacesIndex < dataspacesCount; dataspacesIndex++)
            {
                var dataspacesKey = reader.ReadString();
                dataspaces[dataspacesKey] = Dataspace.Read(ref reader);
            }
        }

        return new PutDataspaces
        {
            Dataspaces = dataspaces,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.PutDataspacesResponse</c> message (protocol 24, message type 6).
/// </summary>
public sealed record PutDataspacesResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 24;

    public int MessageType => 6;

    public string MessageName => "Dataspace.PutDataspacesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataspacesResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new PutDataspacesResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.DeleteDataspaces</c> message (protocol 24, message type 4).
/// </summary>
public sealed record DeleteDataspaces : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public int Protocol => 24;

    public int MessageType => 4;

    public string MessageName => "Dataspace.DeleteDataspaces";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Uris.Count);
        foreach (var (urisKey, urisValue) in Uris)
        {
            writer.WriteString(urisKey);
            writer.WriteString(urisValue);
        }
        writer.WriteBlockEnd();
    }

    public static DeleteDataspaces Read(ref EtpReader reader)
    {
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var urisCount = reader.ReadBlockCount(); urisCount != 0; urisCount = reader.ReadBlockCount())
        {
            for (var urisIndex = 0; urisIndex < urisCount; urisIndex++)
            {
                var urisKey = reader.ReadString();
                uris[urisKey] = reader.ReadString();
            }
        }

        return new DeleteDataspaces
        {
            Uris = uris,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.DeleteDataspacesResponse</c> message (protocol 24, message type 5).
/// </summary>
public sealed record DeleteDataspacesResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 24;

    public int MessageType => 5;

    public string MessageName => "Dataspace.DeleteDataspacesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static DeleteDataspacesResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new DeleteDataspacesResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataspaceOSDU.GetDataspaceInfo</c> message (protocol 2424, message type 1).
/// </summary>
public sealed record GetDataspaceInfo : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public int Protocol => 2424;

    public int MessageType => 1;

    public string MessageName => "DataspaceOSDU.GetDataspaceInfo";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Uris.Count);
        foreach (var (urisKey, urisValue) in Uris)
        {
            writer.WriteString(urisKey);
            writer.WriteString(urisValue);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataspaceInfo Read(ref EtpReader reader)
    {
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var urisCount = reader.ReadBlockCount(); urisCount != 0; urisCount = reader.ReadBlockCount())
        {
            for (var urisIndex = 0; urisIndex < urisCount; urisIndex++)
            {
                var urisKey = reader.ReadString();
                uris[urisKey] = reader.ReadString();
            }
        }

        return new GetDataspaceInfo
        {
            Uris = uris,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataspaceOSDU.GetDataspaceInfoResponse</c> message (protocol 2424, message type 2).
/// </summary>
public sealed record GetDataspaceInfoResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, Dataspace> Dataspaces { get; init; }

    public int Protocol => 2424;

    public int MessageType => 2;

    public string MessageName => "DataspaceOSDU.GetDataspaceInfoResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Dataspaces.Count);
        foreach (var (dataspacesKey, dataspacesValue) in Dataspaces)
        {
            writer.WriteString(dataspacesKey);
            dataspacesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataspaceInfoResponse Read(ref EtpReader reader)
    {
        var dataspaces = new Dictionary<string, Dataspace>(StringComparer.Ordinal);
        for (var dataspacesCount = reader.ReadBlockCount(); dataspacesCount != 0; dataspacesCount = reader.ReadBlockCount())
        {
            for (var dataspacesIndex = 0; dataspacesIndex < dataspacesCount; dataspacesIndex++)
            {
                var dataspacesKey = reader.ReadString();
                dataspaces[dataspacesKey] = Dataspace.Read(ref reader);
            }
        }

        return new GetDataspaceInfoResponse
        {
            Dataspaces = dataspaces,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataspaceOSDU.LockDataspaces</c> message (protocol 2424, message type 5).
/// </summary>
public sealed record LockDataspaces : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public required bool Lock { get; init; }

    public int Protocol => 2424;

    public int MessageType => 5;

    public string MessageName => "DataspaceOSDU.LockDataspaces";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Uris.Count);
        foreach (var (urisKey, urisValue) in Uris)
        {
            writer.WriteString(urisKey);
            writer.WriteString(urisValue);
        }
        writer.WriteBlockEnd();

        writer.WriteBoolean(Lock);
    }

    public static LockDataspaces Read(ref EtpReader reader)
    {
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var urisCount = reader.ReadBlockCount(); urisCount != 0; urisCount = reader.ReadBlockCount())
        {
            for (var urisIndex = 0; urisIndex < urisCount; urisIndex++)
            {
                var urisKey = reader.ReadString();
                uris[urisKey] = reader.ReadString();
            }
        }

        var @lock = reader.ReadBoolean();

        return new LockDataspaces
        {
            Uris = uris,
            Lock = @lock,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataspaceOSDU.LockDataspacesResponse</c> message (protocol 2424, message type 6).
/// </summary>
public sealed record LockDataspacesResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 2424;

    public int MessageType => 6;

    public string MessageName => "DataspaceOSDU.LockDataspacesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static LockDataspacesResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new LockDataspacesResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.GetDataObjects</c> message (protocol 4, message type 1).
/// </summary>
public sealed record GetDataObjects : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public string Format { get; init; } = "xml";

    public int Protocol => 4;

    public int MessageType => 1;

    public string MessageName => "Store.GetDataObjects";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Uris.Count);
        foreach (var (urisKey, urisValue) in Uris)
        {
            writer.WriteString(urisKey);
            writer.WriteString(urisValue);
        }
        writer.WriteBlockEnd();

        writer.WriteString(Format);
    }

    public static GetDataObjects Read(ref EtpReader reader)
    {
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var urisCount = reader.ReadBlockCount(); urisCount != 0; urisCount = reader.ReadBlockCount())
        {
            for (var urisIndex = 0; urisIndex < urisCount; urisIndex++)
            {
                var urisKey = reader.ReadString();
                uris[urisKey] = reader.ReadString();
            }
        }

        var format = reader.ReadString();

        return new GetDataObjects
        {
            Uris = uris,
            Format = format,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.GetDataObjectsResponse</c> message (protocol 4, message type 4).
/// </summary>
public sealed record GetDataObjectsResponse : IEtpMessage
{
    public IReadOnlyDictionary<string, DataObject> DataObjects { get; init; } = ReadOnlyDictionary<string, DataObject>.Empty;

    public int Protocol => 4;

    public int MessageType => 4;

    public string MessageName => "Store.GetDataObjectsResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataObjects.Count);
        foreach (var (dataObjectsKey, dataObjectsValue) in DataObjects)
        {
            writer.WriteString(dataObjectsKey);
            dataObjectsValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataObjectsResponse Read(ref EtpReader reader)
    {
        var dataObjects = new Dictionary<string, DataObject>(StringComparer.Ordinal);
        for (var dataObjectsCount = reader.ReadBlockCount(); dataObjectsCount != 0; dataObjectsCount = reader.ReadBlockCount())
        {
            for (var dataObjectsIndex = 0; dataObjectsIndex < dataObjectsCount; dataObjectsIndex++)
            {
                var dataObjectsKey = reader.ReadString();
                dataObjects[dataObjectsKey] = DataObject.Read(ref reader);
            }
        }

        return new GetDataObjectsResponse
        {
            DataObjects = dataObjects,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.PutDataObjects</c> message (protocol 4, message type 2).
/// </summary>
public sealed record PutDataObjects : IEtpMessage
{
    public required IReadOnlyDictionary<string, DataObject> DataObjects { get; init; }

    public bool PruneContainedObjects { get; init; }

    public int Protocol => 4;

    public int MessageType => 2;

    public string MessageName => "Store.PutDataObjects";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataObjects.Count);
        foreach (var (dataObjectsKey, dataObjectsValue) in DataObjects)
        {
            writer.WriteString(dataObjectsKey);
            dataObjectsValue.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteBoolean(PruneContainedObjects);
    }

    public static PutDataObjects Read(ref EtpReader reader)
    {
        var dataObjects = new Dictionary<string, DataObject>(StringComparer.Ordinal);
        for (var dataObjectsCount = reader.ReadBlockCount(); dataObjectsCount != 0; dataObjectsCount = reader.ReadBlockCount())
        {
            for (var dataObjectsIndex = 0; dataObjectsIndex < dataObjectsCount; dataObjectsIndex++)
            {
                var dataObjectsKey = reader.ReadString();
                dataObjects[dataObjectsKey] = DataObject.Read(ref reader);
            }
        }

        var pruneContainedObjects = reader.ReadBoolean();

        return new PutDataObjects
        {
            DataObjects = dataObjects,
            PruneContainedObjects = pruneContainedObjects,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.PutDataObjectsResponse</c> message (protocol 4, message type 9).
/// </summary>
public sealed record PutDataObjectsResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, PutResponse> Success { get; init; }

    public int Protocol => 4;

    public int MessageType => 9;

    public string MessageName => "Store.PutDataObjectsResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            successValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataObjectsResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, PutResponse>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = PutResponse.Read(ref reader);
            }
        }

        return new PutDataObjectsResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.DeleteDataObjects</c> message (protocol 4, message type 3).
/// </summary>
public sealed record DeleteDataObjects : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public bool PruneContainedObjects { get; init; }

    public int Protocol => 4;

    public int MessageType => 3;

    public string MessageName => "Store.DeleteDataObjects";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Uris.Count);
        foreach (var (urisKey, urisValue) in Uris)
        {
            writer.WriteString(urisKey);
            writer.WriteString(urisValue);
        }
        writer.WriteBlockEnd();

        writer.WriteBoolean(PruneContainedObjects);
    }

    public static DeleteDataObjects Read(ref EtpReader reader)
    {
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var urisCount = reader.ReadBlockCount(); urisCount != 0; urisCount = reader.ReadBlockCount())
        {
            for (var urisIndex = 0; urisIndex < urisCount; urisIndex++)
            {
                var urisKey = reader.ReadString();
                uris[urisKey] = reader.ReadString();
            }
        }

        var pruneContainedObjects = reader.ReadBoolean();

        return new DeleteDataObjects
        {
            Uris = uris,
            PruneContainedObjects = pruneContainedObjects,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.DeleteDataObjectsResponse</c> message (protocol 4, message type 10).
/// </summary>
public sealed record DeleteDataObjectsResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, ArrayOfString> DeletedUris { get; init; }

    public int Protocol => 4;

    public int MessageType => 10;

    public string MessageName => "Store.DeleteDataObjectsResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DeletedUris.Count);
        foreach (var (deletedUrisKey, deletedUrisValue) in DeletedUris)
        {
            writer.WriteString(deletedUrisKey);
            deletedUrisValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static DeleteDataObjectsResponse Read(ref EtpReader reader)
    {
        var deletedUris = new Dictionary<string, ArrayOfString>(StringComparer.Ordinal);
        for (var deletedUrisCount = reader.ReadBlockCount(); deletedUrisCount != 0; deletedUrisCount = reader.ReadBlockCount())
        {
            for (var deletedUrisIndex = 0; deletedUrisIndex < deletedUrisCount; deletedUrisIndex++)
            {
                var deletedUrisKey = reader.ReadString();
                deletedUris[deletedUrisKey] = ArrayOfString.Read(ref reader);
            }
        }

        return new DeleteDataObjectsResponse
        {
            DeletedUris = deletedUris,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.Chunk</c> message (protocol 4, message type 8).
/// </summary>
public sealed record Chunk : IEtpMessage
{
    public required Guid BlobId { get; init; }

    public required ReadOnlyMemory<byte> Data { get; init; }

    public required bool Final { get; init; }

    public int Protocol => 4;

    public int MessageType => 8;

    public string MessageName => "Store.Chunk";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(BlobId);
        writer.WriteBytes(Data.Span);
        writer.WriteBoolean(Final);
    }

    public static Chunk Read(ref EtpReader reader)
    {
        var blobId = reader.ReadUuid();
        var data = reader.ReadBytes();
        var final = reader.ReadBoolean();

        return new Chunk
        {
            BlobId = blobId,
            Data = data,
            Final = final,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataArrays</c> message (protocol 9, message type 2).
/// </summary>
public sealed record GetDataArrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, DataArrayIdentifier> DataArrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 2;

    public string MessageName => "DataArray.GetDataArrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataArrays Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, DataArrayIdentifier>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = DataArrayIdentifier.Read(ref reader);
            }
        }

        return new GetDataArrays
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataArraysResponse</c> message (protocol 9, message type 1).
/// </summary>
public sealed record GetDataArraysResponse : IEtpMessage
{
    public IReadOnlyDictionary<string, DataArray> DataArrays { get; init; } = ReadOnlyDictionary<string, DataArray>.Empty;

    public int Protocol => 9;

    public int MessageType => 1;

    public string MessageName => "DataArray.GetDataArraysResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataArraysResponse Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, DataArray>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = DataArray.Read(ref reader);
            }
        }

        return new GetDataArraysResponse
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataSubarrays</c> message (protocol 9, message type 3).
/// </summary>
public sealed record GetDataSubarrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, GetDataSubarraysType> DataSubarrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 3;

    public string MessageName => "DataArray.GetDataSubarrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataSubarrays.Count);
        foreach (var (dataSubarraysKey, dataSubarraysValue) in DataSubarrays)
        {
            writer.WriteString(dataSubarraysKey);
            dataSubarraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataSubarrays Read(ref EtpReader reader)
    {
        var dataSubarrays = new Dictionary<string, GetDataSubarraysType>(StringComparer.Ordinal);
        for (var dataSubarraysCount = reader.ReadBlockCount(); dataSubarraysCount != 0; dataSubarraysCount = reader.ReadBlockCount())
        {
            for (var dataSubarraysIndex = 0; dataSubarraysIndex < dataSubarraysCount; dataSubarraysIndex++)
            {
                var dataSubarraysKey = reader.ReadString();
                dataSubarrays[dataSubarraysKey] = GetDataSubarraysType.Read(ref reader);
            }
        }

        return new GetDataSubarrays
        {
            DataSubarrays = dataSubarrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataSubarraysResponse</c> message (protocol 9, message type 8).
/// </summary>
public sealed record GetDataSubarraysResponse : IEtpMessage
{
    public IReadOnlyDictionary<string, DataArray> DataSubarrays { get; init; } = ReadOnlyDictionary<string, DataArray>.Empty;

    public int Protocol => 9;

    public int MessageType => 8;

    public string MessageName => "DataArray.GetDataSubarraysResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataSubarrays.Count);
        foreach (var (dataSubarraysKey, dataSubarraysValue) in DataSubarrays)
        {
            writer.WriteString(dataSubarraysKey);
            dataSubarraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataSubarraysResponse Read(ref EtpReader reader)
    {
        var dataSubarrays = new Dictionary<string, DataArray>(StringComparer.Ordinal);
        for (var dataSubarraysCount = reader.ReadBlockCount(); dataSubarraysCount != 0; dataSubarraysCount = reader.ReadBlockCount())
        {
            for (var dataSubarraysIndex = 0; dataSubarraysIndex < dataSubarraysCount; dataSubarraysIndex++)
            {
                var dataSubarraysKey = reader.ReadString();
                dataSubarrays[dataSubarraysKey] = DataArray.Read(ref reader);
            }
        }

        return new GetDataSubarraysResponse
        {
            DataSubarrays = dataSubarrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutDataArrays</c> message (protocol 9, message type 4).
/// </summary>
public sealed record PutDataArrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, PutDataArraysType> DataArrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 4;

    public string MessageName => "DataArray.PutDataArrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataArrays Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, PutDataArraysType>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = PutDataArraysType.Read(ref reader);
            }
        }

        return new PutDataArrays
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutDataArraysResponse</c> message (protocol 9, message type 10).
/// </summary>
public sealed record PutDataArraysResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 9;

    public int MessageType => 10;

    public string MessageName => "DataArray.PutDataArraysResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataArraysResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new PutDataArraysResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutDataSubarrays</c> message (protocol 9, message type 5).
/// </summary>
public sealed record PutDataSubarrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, PutDataSubarraysType> DataSubarrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 5;

    public string MessageName => "DataArray.PutDataSubarrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataSubarrays.Count);
        foreach (var (dataSubarraysKey, dataSubarraysValue) in DataSubarrays)
        {
            writer.WriteString(dataSubarraysKey);
            dataSubarraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataSubarrays Read(ref EtpReader reader)
    {
        var dataSubarrays = new Dictionary<string, PutDataSubarraysType>(StringComparer.Ordinal);
        for (var dataSubarraysCount = reader.ReadBlockCount(); dataSubarraysCount != 0; dataSubarraysCount = reader.ReadBlockCount())
        {
            for (var dataSubarraysIndex = 0; dataSubarraysIndex < dataSubarraysCount; dataSubarraysIndex++)
            {
                var dataSubarraysKey = reader.ReadString();
                dataSubarrays[dataSubarraysKey] = PutDataSubarraysType.Read(ref reader);
            }
        }

        return new PutDataSubarrays
        {
            DataSubarrays = dataSubarrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutDataSubarraysResponse</c> message (protocol 9, message type 11).
/// </summary>
public sealed record PutDataSubarraysResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 9;

    public int MessageType => 11;

    public string MessageName => "DataArray.PutDataSubarraysResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataSubarraysResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new PutDataSubarraysResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutUninitializedDataArrays</c> message (protocol 9, message type 9).
/// </summary>
public sealed record PutUninitializedDataArrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, PutUninitializedDataArrayType> DataArrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 9;

    public string MessageName => "DataArray.PutUninitializedDataArrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutUninitializedDataArrays Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, PutUninitializedDataArrayType>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = PutUninitializedDataArrayType.Read(ref reader);
            }
        }

        return new PutUninitializedDataArrays
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutUninitializedDataArraysResponse</c> message (protocol 9, message type 12).
/// </summary>
public sealed record PutUninitializedDataArraysResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 9;

    public int MessageType => 12;

    public string MessageName => "DataArray.PutUninitializedDataArraysResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static PutUninitializedDataArraysResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new PutUninitializedDataArraysResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataArrayMetadata</c> message (protocol 9, message type 6).
/// </summary>
public sealed record GetDataArrayMetadata : IEtpMessage
{
    public required IReadOnlyDictionary<string, DataArrayIdentifier> DataArrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 6;

    public string MessageName => "DataArray.GetDataArrayMetadata";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataArrayMetadata Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, DataArrayIdentifier>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = DataArrayIdentifier.Read(ref reader);
            }
        }

        return new GetDataArrayMetadata
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataArrayMetadataResponse</c> message (protocol 9, message type 7).
/// </summary>
public sealed record GetDataArrayMetadataResponse : IEtpMessage
{
    public IReadOnlyDictionary<string, DataArrayMetadata> ArrayMetadata { get; init; } = ReadOnlyDictionary<string, DataArrayMetadata>.Empty;

    public int Protocol => 9;

    public int MessageType => 7;

    public string MessageName => "DataArray.GetDataArrayMetadataResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(ArrayMetadata.Count);
        foreach (var (arrayMetadataKey, arrayMetadataValue) in ArrayMetadata)
        {
            writer.WriteString(arrayMetadataKey);
            arrayMetadataValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataArrayMetadataResponse Read(ref EtpReader reader)
    {
        var arrayMetadata = new Dictionary<string, DataArrayMetadata>(StringComparer.Ordinal);
        for (var arrayMetadataCount = reader.ReadBlockCount(); arrayMetadataCount != 0; arrayMetadataCount = reader.ReadBlockCount())
        {
            for (var arrayMetadataIndex = 0; arrayMetadataIndex < arrayMetadataCount; arrayMetadataIndex++)
            {
                var arrayMetadataKey = reader.ReadString();
                arrayMetadata[arrayMetadataKey] = DataArrayMetadata.Read(ref reader);
            }
        }

        return new GetDataArrayMetadataResponse
        {
            ArrayMetadata = arrayMetadata,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.StartTransaction</c> message (protocol 18, message type 1).
/// </summary>
public sealed record StartTransaction : IEtpMessage
{
    public required bool ReadOnly { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<string> DataspaceUris { get; init; } = [""];

    public int Protocol => 18;

    public int MessageType => 1;

    public string MessageName => "Transaction.StartTransaction";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBoolean(ReadOnly);
        writer.WriteString(Message);

        writer.WriteBlockHeader(DataspaceUris.Count);
        foreach (var dataspaceUrisItem in DataspaceUris)
        {
            writer.WriteString(dataspaceUrisItem);
        }
        writer.WriteBlockEnd();
    }

    public static StartTransaction Read(ref EtpReader reader)
    {
        var readOnly = reader.ReadBoolean();
        var message = reader.ReadString();

        var dataspaceUris = new List<string>();
        for (var dataspaceUrisCount = reader.ReadBlockCount(); dataspaceUrisCount != 0; dataspaceUrisCount = reader.ReadBlockCount())
        {
            dataspaceUris.EnsureCapacity(dataspaceUris.Count + dataspaceUrisCount);
            for (var dataspaceUrisIndex = 0; dataspaceUrisIndex < dataspaceUrisCount; dataspaceUrisIndex++)
            {
                dataspaceUris.Add(reader.ReadString());
            }
        }

        return new StartTransaction
        {
            ReadOnly = readOnly,
            Message = message,
            DataspaceUris = dataspaceUris,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.StartTransactionResponse</c> message (protocol 18, message type 2).
/// </summary>
public sealed record StartTransactionResponse : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public bool Successful { get; init; } = true;

    public string FailureReason { get; init; } = string.Empty;

    public int Protocol => 18;

    public int MessageType => 2;

    public string MessageName => "Transaction.StartTransactionResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
        writer.WriteBoolean(Successful);
        writer.WriteString(FailureReason);
    }

    public static StartTransactionResponse Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();
        var successful = reader.ReadBoolean();
        var failureReason = reader.ReadString();

        return new StartTransactionResponse
        {
            TransactionUuid = transactionUuid,
            Successful = successful,
            FailureReason = failureReason,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.CommitTransaction</c> message (protocol 18, message type 3).
/// </summary>
public sealed record CommitTransaction : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public int Protocol => 18;

    public int MessageType => 3;

    public string MessageName => "Transaction.CommitTransaction";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
    }

    public static CommitTransaction Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();

        return new CommitTransaction
        {
            TransactionUuid = transactionUuid,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.CommitTransactionResponse</c> message (protocol 18, message type 5).
/// </summary>
public sealed record CommitTransactionResponse : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public bool Successful { get; init; } = true;

    public string FailureReason { get; init; } = string.Empty;

    public int Protocol => 18;

    public int MessageType => 5;

    public string MessageName => "Transaction.CommitTransactionResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
        writer.WriteBoolean(Successful);
        writer.WriteString(FailureReason);
    }

    public static CommitTransactionResponse Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();
        var successful = reader.ReadBoolean();
        var failureReason = reader.ReadString();

        return new CommitTransactionResponse
        {
            TransactionUuid = transactionUuid,
            Successful = successful,
            FailureReason = failureReason,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.RollbackTransaction</c> message (protocol 18, message type 4).
/// </summary>
public sealed record RollbackTransaction : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public int Protocol => 18;

    public int MessageType => 4;

    public string MessageName => "Transaction.RollbackTransaction";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
    }

    public static RollbackTransaction Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();

        return new RollbackTransaction
        {
            TransactionUuid = transactionUuid,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.RollbackTransactionResponse</c> message (protocol 18, message type 6).
/// </summary>
public sealed record RollbackTransactionResponse : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public bool Successful { get; init; } = true;

    public string FailureReason { get; init; } = string.Empty;

    public int Protocol => 18;

    public int MessageType => 6;

    public string MessageName => "Transaction.RollbackTransactionResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
        writer.WriteBoolean(Successful);
        writer.WriteString(FailureReason);
    }

    public static RollbackTransactionResponse Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();
        var successful = reader.ReadBoolean();
        var failureReason = reader.ReadString();

        return new RollbackTransactionResponse
        {
            TransactionUuid = transactionUuid,
            Successful = successful,
            FailureReason = failureReason,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Discovery.GetResources</c> message (protocol 3, message type 1).
/// </summary>
public sealed record GetResources : IEtpMessage
{
    public required ContextInfo Context { get; init; }

    public required ContextScopeKind Scope { get; init; }

    public bool CountObjects { get; init; }

    public long? StoreLastWriteFilter { get; init; }

    public ActiveStatusKind? ActiveStatusFilter { get; init; }

    public bool IncludeEdges { get; init; }

    public int Protocol => 3;

    public int MessageType => 1;

    public string MessageName => "Discovery.GetResources";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Context.Write(writer);
        writer.WriteEnum((int)Scope);
        writer.WriteBoolean(CountObjects);

        if (StoreLastWriteFilter is { } storeLastWriteFilterValue)
        {
            writer.WriteUnion(1);
            writer.WriteLong(storeLastWriteFilterValue);
        }
        else
        {
            writer.WriteUnion(0);
        }

        if (ActiveStatusFilter is { } activeStatusFilterValue)
        {
            writer.WriteUnion(1);
            writer.WriteEnum((int)activeStatusFilterValue);
        }
        else
        {
            writer.WriteUnion(0);
        }

        writer.WriteBoolean(IncludeEdges);
    }

    public static GetResources Read(ref EtpReader reader)
    {
        var context = ContextInfo.Read(ref reader);
        var scope = (ContextScopeKind)reader.ReadEnum(5);
        var countObjects = reader.ReadBoolean();
        long? storeLastWriteFilter = reader.ReadUnion(2) == 1 ? reader.ReadLong() : (long?)null;
        ActiveStatusKind? activeStatusFilter = reader.ReadUnion(2) == 1 ? (ActiveStatusKind)reader.ReadEnum(2) : (ActiveStatusKind?)null;
        var includeEdges = reader.ReadBoolean();

        return new GetResources
        {
            Context = context,
            Scope = scope,
            CountObjects = countObjects,
            StoreLastWriteFilter = storeLastWriteFilter,
            ActiveStatusFilter = activeStatusFilter,
            IncludeEdges = includeEdges,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Discovery.GetResourcesResponse</c> message (protocol 3, message type 4).
/// </summary>
public sealed record GetResourcesResponse : IEtpMessage
{
    public IReadOnlyList<Resource> Resources { get; init; } = [];

    public int Protocol => 3;

    public int MessageType => 4;

    public string MessageName => "Discovery.GetResourcesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Resources.Count);
        foreach (var resourcesItem in Resources)
        {
            resourcesItem.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetResourcesResponse Read(ref EtpReader reader)
    {
        var resources = new List<Resource>();
        for (var resourcesCount = reader.ReadBlockCount(); resourcesCount != 0; resourcesCount = reader.ReadBlockCount())
        {
            resources.EnsureCapacity(resources.Count + resourcesCount);
            for (var resourcesIndex = 0; resourcesIndex < resourcesCount; resourcesIndex++)
            {
                resources.Add(Resource.Read(ref reader));
            }
        }

        return new GetResourcesResponse
        {
            Resources = resources,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Discovery.GetResourcesEdgesResponse</c> message (protocol 3, message type 7).
/// </summary>
public sealed record GetResourcesEdgesResponse : IEtpMessage
{
    public required IReadOnlyList<Edge> Edges { get; init; }

    public int Protocol => 3;

    public int MessageType => 7;

    public string MessageName => "Discovery.GetResourcesEdgesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Edges.Count);
        foreach (var edgesItem in Edges)
        {
            edgesItem.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetResourcesEdgesResponse Read(ref EtpReader reader)
    {
        var edges = new List<Edge>();
        for (var edgesCount = reader.ReadBlockCount(); edgesCount != 0; edgesCount = reader.ReadBlockCount())
        {
            edges.EnsureCapacity(edges.Count + edgesCount);
            for (var edgesIndex = 0; edgesIndex < edgesCount; edgesIndex++)
            {
                edges.Add(Edge.Read(ref reader));
            }
        }

        return new GetResourcesEdgesResponse
        {
            Edges = edges,
        };
    }
}

/// <summary>
/// Every ETP message this client sends or understands, by the protocol and message type its header carries.
/// A message outside this set is not an error: an ETP server may send one, and the session ignores it.
/// </summary>
public static class EtpMessages
{
    /// <summary>Reads a message body, or null when the header names one this client does not implement.</summary>
    public static IEtpMessage? Read(int protocol, int messageType, ref EtpReader reader) => (protocol, messageType) switch
    {
        (0, 1) => RequestSession.Read(ref reader),
        (0, 2) => OpenSession.Read(ref reader),
        (0, 5) => CloseSession.Read(ref reader),
        (0, 6) => Authorize.Read(ref reader),
        (0, 7) => AuthorizeResponse.Read(ref reader),
        (0, 8) => Ping.Read(ref reader),
        (0, 9) => Pong.Read(ref reader),
        (0, 1000) => ProtocolFailure.Read(ref reader),
        (0, 1001) => Acknowledge.Read(ref reader),
        (24, 1) => GetDataspaces.Read(ref reader),
        (24, 2) => GetDataspacesResponse.Read(ref reader),
        (24, 3) => PutDataspaces.Read(ref reader),
        (24, 6) => PutDataspacesResponse.Read(ref reader),
        (24, 4) => DeleteDataspaces.Read(ref reader),
        (24, 5) => DeleteDataspacesResponse.Read(ref reader),
        (2424, 1) => GetDataspaceInfo.Read(ref reader),
        (2424, 2) => GetDataspaceInfoResponse.Read(ref reader),
        (2424, 5) => LockDataspaces.Read(ref reader),
        (2424, 6) => LockDataspacesResponse.Read(ref reader),
        (4, 1) => GetDataObjects.Read(ref reader),
        (4, 4) => GetDataObjectsResponse.Read(ref reader),
        (4, 2) => PutDataObjects.Read(ref reader),
        (4, 9) => PutDataObjectsResponse.Read(ref reader),
        (4, 3) => DeleteDataObjects.Read(ref reader),
        (4, 10) => DeleteDataObjectsResponse.Read(ref reader),
        (4, 8) => Chunk.Read(ref reader),
        (9, 2) => GetDataArrays.Read(ref reader),
        (9, 1) => GetDataArraysResponse.Read(ref reader),
        (9, 3) => GetDataSubarrays.Read(ref reader),
        (9, 8) => GetDataSubarraysResponse.Read(ref reader),
        (9, 4) => PutDataArrays.Read(ref reader),
        (9, 10) => PutDataArraysResponse.Read(ref reader),
        (9, 5) => PutDataSubarrays.Read(ref reader),
        (9, 11) => PutDataSubarraysResponse.Read(ref reader),
        (9, 9) => PutUninitializedDataArrays.Read(ref reader),
        (9, 12) => PutUninitializedDataArraysResponse.Read(ref reader),
        (9, 6) => GetDataArrayMetadata.Read(ref reader),
        (9, 7) => GetDataArrayMetadataResponse.Read(ref reader),
        (18, 1) => StartTransaction.Read(ref reader),
        (18, 2) => StartTransactionResponse.Read(ref reader),
        (18, 3) => CommitTransaction.Read(ref reader),
        (18, 5) => CommitTransactionResponse.Read(ref reader),
        (18, 4) => RollbackTransaction.Read(ref reader),
        (18, 6) => RollbackTransactionResponse.Read(ref reader),
        (3, 1) => GetResources.Read(ref reader),
        (3, 4) => GetResourcesResponse.Read(ref reader),
        (3, 7) => GetResourcesEdgesResponse.Read(ref reader),
        _ => null,
    };

    /// <summary>The name of a message, for logs and errors, whether or not this client implements it.</summary>
    public static string Name(int protocol, int messageType) => (protocol, messageType) switch
    {
        (0, 1) => "Core.RequestSession",
        (0, 2) => "Core.OpenSession",
        (0, 5) => "Core.CloseSession",
        (0, 6) => "Core.Authorize",
        (0, 7) => "Core.AuthorizeResponse",
        (0, 8) => "Core.Ping",
        (0, 9) => "Core.Pong",
        (0, 1000) => "Core.ProtocolException",
        (0, 1001) => "Core.Acknowledge",
        (24, 1) => "Dataspace.GetDataspaces",
        (24, 2) => "Dataspace.GetDataspacesResponse",
        (24, 3) => "Dataspace.PutDataspaces",
        (24, 6) => "Dataspace.PutDataspacesResponse",
        (24, 4) => "Dataspace.DeleteDataspaces",
        (24, 5) => "Dataspace.DeleteDataspacesResponse",
        (2424, 1) => "DataspaceOSDU.GetDataspaceInfo",
        (2424, 2) => "DataspaceOSDU.GetDataspaceInfoResponse",
        (2424, 5) => "DataspaceOSDU.LockDataspaces",
        (2424, 6) => "DataspaceOSDU.LockDataspacesResponse",
        (4, 1) => "Store.GetDataObjects",
        (4, 4) => "Store.GetDataObjectsResponse",
        (4, 2) => "Store.PutDataObjects",
        (4, 9) => "Store.PutDataObjectsResponse",
        (4, 3) => "Store.DeleteDataObjects",
        (4, 10) => "Store.DeleteDataObjectsResponse",
        (4, 8) => "Store.Chunk",
        (9, 2) => "DataArray.GetDataArrays",
        (9, 1) => "DataArray.GetDataArraysResponse",
        (9, 3) => "DataArray.GetDataSubarrays",
        (9, 8) => "DataArray.GetDataSubarraysResponse",
        (9, 4) => "DataArray.PutDataArrays",
        (9, 10) => "DataArray.PutDataArraysResponse",
        (9, 5) => "DataArray.PutDataSubarrays",
        (9, 11) => "DataArray.PutDataSubarraysResponse",
        (9, 9) => "DataArray.PutUninitializedDataArrays",
        (9, 12) => "DataArray.PutUninitializedDataArraysResponse",
        (9, 6) => "DataArray.GetDataArrayMetadata",
        (9, 7) => "DataArray.GetDataArrayMetadataResponse",
        (18, 1) => "Transaction.StartTransaction",
        (18, 2) => "Transaction.StartTransactionResponse",
        (18, 3) => "Transaction.CommitTransaction",
        (18, 5) => "Transaction.CommitTransactionResponse",
        (18, 4) => "Transaction.RollbackTransaction",
        (18, 6) => "Transaction.RollbackTransactionResponse",
        (3, 1) => "Discovery.GetResources",
        (3, 4) => "Discovery.GetResourcesResponse",
        (3, 7) => "Discovery.GetResourcesEdgesResponse",
        _ => $"protocol {protocol} message {messageType}",
    };
}
