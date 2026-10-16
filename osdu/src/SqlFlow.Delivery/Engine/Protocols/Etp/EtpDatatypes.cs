// Written from the pinned ETP 1.2 protocol, osdu/specs/reservoir-ddms/etp-1.2.avpr: one C# record per Avro record,
// its properties in the schema's field order, which is the order the wire carries them in. EtpSchemaTests round-trips
// every one of them against a codec driven by that same file, so this file and the schema cannot drift apart.

using System.Collections.ObjectModel;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>The ETP 1.2 <c>Datatypes.MessageHeader</c> record.</summary>
public sealed record MessageHeader
{
    public required int Protocol { get; init; }

    public required int MessageType { get; init; }

    public required long CorrelationId { get; init; }

    public required long MessageId { get; init; }

    public required int MessageFlags { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt(Protocol);
        writer.WriteInt(MessageType);
        writer.WriteLong(CorrelationId);
        writer.WriteLong(MessageId);
        writer.WriteInt(MessageFlags);
    }

    public static MessageHeader Read(ref EtpReader reader)
    {
        var protocol = reader.ReadInt();
        var messageType = reader.ReadInt();
        var correlationId = reader.ReadLong();
        var messageId = reader.ReadLong();
        var messageFlags = reader.ReadInt();

        return new MessageHeader
        {
            Protocol = protocol,
            MessageType = messageType,
            CorrelationId = correlationId,
            MessageId = messageId,
            MessageFlags = messageFlags,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.AnyArrayType</c> enumeration.</summary>
public enum AnyArrayType
{
    ArrayOfBoolean = 0,
    ArrayOfInt = 1,
    ArrayOfLong = 2,
    ArrayOfFloat = 3,
    ArrayOfDouble = 4,
    ArrayOfString = 5,
    Bytes = 6,
}

/// <summary>The ETP 1.2 <c>Datatypes.AnyLogicalArrayType</c> enumeration.</summary>
public enum AnyLogicalArrayType
{
    ArrayOfBoolean = 0,
    ArrayOfInt8 = 1,
    ArrayOfUInt8 = 2,
    ArrayOfInt16LE = 3,
    ArrayOfInt32LE = 4,
    ArrayOfInt64LE = 5,
    ArrayOfUInt16LE = 6,
    ArrayOfUInt32LE = 7,
    ArrayOfUInt64LE = 8,
    ArrayOfFloat32LE = 9,
    ArrayOfDouble64LE = 10,
    ArrayOfInt16BE = 11,
    ArrayOfInt32BE = 12,
    ArrayOfInt64BE = 13,
    ArrayOfUInt16BE = 14,
    ArrayOfUInt32BE = 15,
    ArrayOfUInt64BE = 16,
    ArrayOfFloat32BE = 17,
    ArrayOfDouble64BE = 18,
    ArrayOfString = 19,
    ArrayOfCustom = 20,
}

/// <summary>The ETP 1.2 <c>Datatypes.AnySparseArray</c> record.</summary>
public sealed record AnySparseArray
{
    public required IReadOnlyList<AnySubarray> Slices { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Slices.Count);
        foreach (var slicesItem in Slices)
        {
            slicesItem.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static AnySparseArray Read(ref EtpReader reader)
    {
        var slices = new List<AnySubarray>();
        for (var slicesCount = reader.ReadBlockCount(); slicesCount != 0; slicesCount = reader.ReadBlockCount())
        {
            slices.EnsureCapacity(slices.Count + slicesCount);
            for (var slicesIndex = 0; slicesIndex < slicesCount; slicesIndex++)
            {
                slices.Add(AnySubarray.Read(ref reader));
            }
        }

        return new AnySparseArray
        {
            Slices = slices,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.AnySubarray</c> record.</summary>
public sealed record AnySubarray
{
    public required long Start { get; init; }

    public required AnyArray Slice { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLong(Start);
        Slice.Write(writer);
    }

    public static AnySubarray Read(ref EtpReader reader)
    {
        var start = reader.ReadLong();
        var slice = AnyArray.Read(ref reader);

        return new AnySubarray
        {
            Start = start,
            Slice = slice,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfBoolean</c> record.</summary>
public sealed record ArrayOfBoolean
{
    public required ReadOnlyMemory<bool> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBooleanArray(Values);
    }

    public static ArrayOfBoolean Read(ref EtpReader reader)
    {
        var values = reader.ReadBooleanArray();

        return new ArrayOfBoolean
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfBytes</c> record.</summary>
public sealed record ArrayOfBytes
{
    public required IReadOnlyList<ReadOnlyMemory<byte>> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Values.Count);
        foreach (var valuesItem in Values)
        {
            writer.WriteBytes(valuesItem.Span);
        }
        writer.WriteBlockEnd();
    }

    public static ArrayOfBytes Read(ref EtpReader reader)
    {
        var values = new List<ReadOnlyMemory<byte>>();
        for (var valuesCount = reader.ReadBlockCount(); valuesCount != 0; valuesCount = reader.ReadBlockCount())
        {
            values.EnsureCapacity(values.Count + valuesCount);
            for (var valuesIndex = 0; valuesIndex < valuesCount; valuesIndex++)
            {
                values.Add(reader.ReadBytes());
            }
        }

        return new ArrayOfBytes
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfDouble</c> record.</summary>
public sealed record ArrayOfDouble
{
    public required ReadOnlyMemory<double> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteDoubleArray(Values);
    }

    public static ArrayOfDouble Read(ref EtpReader reader)
    {
        var values = reader.ReadDoubleArray();

        return new ArrayOfDouble
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfFloat</c> record.</summary>
public sealed record ArrayOfFloat
{
    public required ReadOnlyMemory<float> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteFloatArray(Values);
    }

    public static ArrayOfFloat Read(ref EtpReader reader)
    {
        var values = reader.ReadFloatArray();

        return new ArrayOfFloat
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfInt</c> record.</summary>
public sealed record ArrayOfInt
{
    public required ReadOnlyMemory<int> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteIntArray(Values);
    }

    public static ArrayOfInt Read(ref EtpReader reader)
    {
        var values = reader.ReadIntArray();

        return new ArrayOfInt
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfLong</c> record.</summary>
public sealed record ArrayOfLong
{
    public required ReadOnlyMemory<long> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLongArray(Values);
    }

    public static ArrayOfLong Read(ref EtpReader reader)
    {
        var values = reader.ReadLongArray();

        return new ArrayOfLong
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfNullableBoolean</c> record.</summary>
public sealed record ArrayOfNullableBoolean
{
    public required IReadOnlyList<bool?> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Values.Count);
        foreach (var valuesItem in Values)
        {
            if (valuesItem is { } valuesValue1)
            {
                writer.WriteUnion(1);
                writer.WriteBoolean(valuesValue1);
            }
            else
            {
                writer.WriteUnion(0);
            }
        }
        writer.WriteBlockEnd();
    }

    public static ArrayOfNullableBoolean Read(ref EtpReader reader)
    {
        var values = new List<bool?>();
        for (var valuesCount = reader.ReadBlockCount(); valuesCount != 0; valuesCount = reader.ReadBlockCount())
        {
            values.EnsureCapacity(values.Count + valuesCount);
            for (var valuesIndex = 0; valuesIndex < valuesCount; valuesIndex++)
            {
                values.Add(reader.ReadUnion(2) == 1 ? reader.ReadBoolean() : (bool?)null);
            }
        }

        return new ArrayOfNullableBoolean
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfNullableInt</c> record.</summary>
public sealed record ArrayOfNullableInt
{
    public required IReadOnlyList<int?> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Values.Count);
        foreach (var valuesItem in Values)
        {
            if (valuesItem is { } valuesValue1)
            {
                writer.WriteUnion(1);
                writer.WriteInt(valuesValue1);
            }
            else
            {
                writer.WriteUnion(0);
            }
        }
        writer.WriteBlockEnd();
    }

    public static ArrayOfNullableInt Read(ref EtpReader reader)
    {
        var values = new List<int?>();
        for (var valuesCount = reader.ReadBlockCount(); valuesCount != 0; valuesCount = reader.ReadBlockCount())
        {
            values.EnsureCapacity(values.Count + valuesCount);
            for (var valuesIndex = 0; valuesIndex < valuesCount; valuesIndex++)
            {
                values.Add(reader.ReadUnion(2) == 1 ? reader.ReadInt() : (int?)null);
            }
        }

        return new ArrayOfNullableInt
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfNullableLong</c> record.</summary>
public sealed record ArrayOfNullableLong
{
    public required IReadOnlyList<long?> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Values.Count);
        foreach (var valuesItem in Values)
        {
            if (valuesItem is { } valuesValue1)
            {
                writer.WriteUnion(1);
                writer.WriteLong(valuesValue1);
            }
            else
            {
                writer.WriteUnion(0);
            }
        }
        writer.WriteBlockEnd();
    }

    public static ArrayOfNullableLong Read(ref EtpReader reader)
    {
        var values = new List<long?>();
        for (var valuesCount = reader.ReadBlockCount(); valuesCount != 0; valuesCount = reader.ReadBlockCount())
        {
            values.EnsureCapacity(values.Count + valuesCount);
            for (var valuesIndex = 0; valuesIndex < valuesCount; valuesIndex++)
            {
                values.Add(reader.ReadUnion(2) == 1 ? reader.ReadLong() : (long?)null);
            }
        }

        return new ArrayOfNullableLong
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ArrayOfString</c> record.</summary>
public sealed record ArrayOfString
{
    public required IReadOnlyList<string> Values { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Values.Count);
        foreach (var valuesItem in Values)
        {
            writer.WriteString(valuesItem);
        }
        writer.WriteBlockEnd();
    }

    public static ArrayOfString Read(ref EtpReader reader)
    {
        var values = new List<string>();
        for (var valuesCount = reader.ReadBlockCount(); valuesCount != 0; valuesCount = reader.ReadBlockCount())
        {
            values.EnsureCapacity(values.Count + valuesCount);
            for (var valuesIndex = 0; valuesIndex < valuesCount; valuesIndex++)
            {
                values.Add(reader.ReadString());
            }
        }

        return new ArrayOfString
        {
            Values = values,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.DataArrayTypes.DataArray</c> record.</summary>
public sealed record DataArray
{
    public required ReadOnlyMemory<long> Dimensions { get; init; }

    public required AnyArray Data { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLongArray(Dimensions);
        Data.Write(writer);
    }

    public static DataArray Read(ref EtpReader reader)
    {
        var dimensions = reader.ReadLongArray();
        var data = AnyArray.Read(ref reader);

        return new DataArray
        {
            Dimensions = dimensions,
            Data = data,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.DataArrayTypes.DataArrayIdentifier</c> record.</summary>
public sealed record DataArrayIdentifier
{
    public required string Uri { get; init; }

    public required string PathInResource { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Uri);
        writer.WriteString(PathInResource);
    }

    public static DataArrayIdentifier Read(ref EtpReader reader)
    {
        var uri = reader.ReadString();
        var pathInResource = reader.ReadString();

        return new DataArrayIdentifier
        {
            Uri = uri,
            PathInResource = pathInResource,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.DataArrayTypes.DataArrayMetadata</c> record.</summary>
public sealed record DataArrayMetadata
{
    public required ReadOnlyMemory<long> Dimensions { get; init; }

    public ReadOnlyMemory<long> PreferredSubarrayDimensions { get; init; } = ReadOnlyMemory<long>.Empty;

    public required AnyArrayType TransportArrayType { get; init; }

    public required AnyLogicalArrayType LogicalArrayType { get; init; }

    public required long StoreLastWrite { get; init; }

    public required long StoreCreated { get; init; }

    public IReadOnlyDictionary<string, DataValue> CustomData { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLongArray(Dimensions);
        writer.WriteLongArray(PreferredSubarrayDimensions);
        writer.WriteEnum((int)TransportArrayType);
        writer.WriteEnum((int)LogicalArrayType);
        writer.WriteLong(StoreLastWrite);
        writer.WriteLong(StoreCreated);

        writer.WriteBlockHeader(CustomData.Count);
        foreach (var (customDataKey, customDataValue) in CustomData)
        {
            writer.WriteString(customDataKey);
            customDataValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static DataArrayMetadata Read(ref EtpReader reader)
    {
        var dimensions = reader.ReadLongArray();
        var preferredSubarrayDimensions = reader.ReadLongArray();
        var transportArrayType = (AnyArrayType)reader.ReadEnum(7);
        var logicalArrayType = (AnyLogicalArrayType)reader.ReadEnum(21);
        var storeLastWrite = reader.ReadLong();
        var storeCreated = reader.ReadLong();

        var customData = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var customDataCount = reader.ReadBlockCount(); customDataCount != 0; customDataCount = reader.ReadBlockCount())
        {
            for (var customDataIndex = 0; customDataIndex < customDataCount; customDataIndex++)
            {
                var customDataKey = reader.ReadString();
                customData[customDataKey] = DataValue.Read(ref reader);
            }
        }

        return new DataArrayMetadata
        {
            Dimensions = dimensions,
            PreferredSubarrayDimensions = preferredSubarrayDimensions,
            TransportArrayType = transportArrayType,
            LogicalArrayType = logicalArrayType,
            StoreLastWrite = storeLastWrite,
            StoreCreated = storeCreated,
            CustomData = customData,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.DataArrayTypes.GetDataSubarraysType</c> record.</summary>
public sealed record GetDataSubarraysType
{
    public required DataArrayIdentifier Uid { get; init; }

    public ReadOnlyMemory<long> Starts { get; init; } = ReadOnlyMemory<long>.Empty;

    public ReadOnlyMemory<long> Counts { get; init; } = ReadOnlyMemory<long>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Uid.Write(writer);
        writer.WriteLongArray(Starts);
        writer.WriteLongArray(Counts);
    }

    public static GetDataSubarraysType Read(ref EtpReader reader)
    {
        var uid = DataArrayIdentifier.Read(ref reader);
        var starts = reader.ReadLongArray();
        var counts = reader.ReadLongArray();

        return new GetDataSubarraysType
        {
            Uid = uid,
            Starts = starts,
            Counts = counts,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.DataArrayTypes.PutDataArraysType</c> record.</summary>
public sealed record PutDataArraysType
{
    public required DataArrayIdentifier Uid { get; init; }

    public required DataArray Array { get; init; }

    public IReadOnlyDictionary<string, DataValue> CustomData { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Uid.Write(writer);
        Array.Write(writer);

        writer.WriteBlockHeader(CustomData.Count);
        foreach (var (customDataKey, customDataValue) in CustomData)
        {
            writer.WriteString(customDataKey);
            customDataValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataArraysType Read(ref EtpReader reader)
    {
        var uid = DataArrayIdentifier.Read(ref reader);
        var array = DataArray.Read(ref reader);

        var customData = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var customDataCount = reader.ReadBlockCount(); customDataCount != 0; customDataCount = reader.ReadBlockCount())
        {
            for (var customDataIndex = 0; customDataIndex < customDataCount; customDataIndex++)
            {
                var customDataKey = reader.ReadString();
                customData[customDataKey] = DataValue.Read(ref reader);
            }
        }

        return new PutDataArraysType
        {
            Uid = uid,
            Array = array,
            CustomData = customData,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.DataArrayTypes.PutDataSubarraysType</c> record.</summary>
public sealed record PutDataSubarraysType
{
    public required DataArrayIdentifier Uid { get; init; }

    public required AnyArray Data { get; init; }

    public required ReadOnlyMemory<long> Starts { get; init; }

    public required ReadOnlyMemory<long> Counts { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Uid.Write(writer);
        Data.Write(writer);
        writer.WriteLongArray(Starts);
        writer.WriteLongArray(Counts);
    }

    public static PutDataSubarraysType Read(ref EtpReader reader)
    {
        var uid = DataArrayIdentifier.Read(ref reader);
        var data = AnyArray.Read(ref reader);
        var starts = reader.ReadLongArray();
        var counts = reader.ReadLongArray();

        return new PutDataSubarraysType
        {
            Uid = uid,
            Data = data,
            Starts = starts,
            Counts = counts,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.DataArrayTypes.PutUninitializedDataArrayType</c> record.</summary>
public sealed record PutUninitializedDataArrayType
{
    public required DataArrayIdentifier Uid { get; init; }

    public required DataArrayMetadata Metadata { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Uid.Write(writer);
        Metadata.Write(writer);
    }

    public static PutUninitializedDataArrayType Read(ref EtpReader reader)
    {
        var uid = DataArrayIdentifier.Read(ref reader);
        var metadata = DataArrayMetadata.Read(ref reader);

        return new PutUninitializedDataArrayType
        {
            Uid = uid,
            Metadata = metadata,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.ErrorInfo</c> record.</summary>
public sealed record ErrorInfo
{
    public required string Message { get; init; }

    public required int Code { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Message);
        writer.WriteInt(Code);
    }

    public static ErrorInfo Read(ref EtpReader reader)
    {
        var message = reader.ReadString();
        var code = reader.ReadInt();

        return new ErrorInfo
        {
            Message = message,
            Code = code,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.ActiveStatusKind</c> enumeration.</summary>
public enum ActiveStatusKind
{
    Active = 0,
    Inactive = 1,
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.ContextInfo</c> record.</summary>
public sealed record ContextInfo
{
    public required string Uri { get; init; }

    public required int Depth { get; init; }

    public IReadOnlyList<string> DataObjectTypes { get; init; } = [];

    public required RelationshipKind NavigableEdges { get; init; }

    public bool IncludeSecondaryTargets { get; init; }

    public bool IncludeSecondarySources { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Uri);
        writer.WriteInt(Depth);

        writer.WriteBlockHeader(DataObjectTypes.Count);
        foreach (var dataObjectTypesItem in DataObjectTypes)
        {
            writer.WriteString(dataObjectTypesItem);
        }
        writer.WriteBlockEnd();

        writer.WriteEnum((int)NavigableEdges);
        writer.WriteBoolean(IncludeSecondaryTargets);
        writer.WriteBoolean(IncludeSecondarySources);
    }

    public static ContextInfo Read(ref EtpReader reader)
    {
        var uri = reader.ReadString();
        var depth = reader.ReadInt();

        var dataObjectTypes = new List<string>();
        for (var dataObjectTypesCount = reader.ReadBlockCount(); dataObjectTypesCount != 0; dataObjectTypesCount = reader.ReadBlockCount())
        {
            dataObjectTypes.EnsureCapacity(dataObjectTypes.Count + dataObjectTypesCount);
            for (var dataObjectTypesIndex = 0; dataObjectTypesIndex < dataObjectTypesCount; dataObjectTypesIndex++)
            {
                dataObjectTypes.Add(reader.ReadString());
            }
        }

        var navigableEdges = (RelationshipKind)reader.ReadEnum(3);
        var includeSecondaryTargets = reader.ReadBoolean();
        var includeSecondarySources = reader.ReadBoolean();

        return new ContextInfo
        {
            Uri = uri,
            Depth = depth,
            DataObjectTypes = dataObjectTypes,
            NavigableEdges = navigableEdges,
            IncludeSecondaryTargets = includeSecondaryTargets,
            IncludeSecondarySources = includeSecondarySources,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.ContextScopeKind</c> enumeration.</summary>
public enum ContextScopeKind
{
    Self = 0,
    Sources = 1,
    Targets = 2,
    SourcesOrSelf = 3,
    TargetsOrSelf = 4,
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.DataObject</c> record.</summary>
public sealed record DataObject
{
    public required Resource Resource { get; init; }

    public string Format { get; init; } = "xml";

    public Guid? BlobId { get; init; }

    public ReadOnlyMemory<byte> Data { get; init; } = ReadOnlyMemory<byte>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Resource.Write(writer);
        writer.WriteString(Format);

        if (BlobId is { } blobIdValue)
        {
            writer.WriteUnion(1);
            writer.WriteUuid(blobIdValue);
        }
        else
        {
            writer.WriteUnion(0);
        }

        writer.WriteBytes(Data.Span);
    }

    public static DataObject Read(ref EtpReader reader)
    {
        var resource = Resource.Read(ref reader);
        var format = reader.ReadString();
        Guid? blobId = reader.ReadUnion(2) == 1 ? reader.ReadUuid() : (Guid?)null;
        var data = reader.ReadBytes();

        return new DataObject
        {
            Resource = resource,
            Format = format,
            BlobId = blobId,
            Data = data,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.Dataspace</c> record.</summary>
public sealed record Dataspace
{
    public required string Uri { get; init; }

    public string Path { get; init; } = string.Empty;

    public required long StoreLastWrite { get; init; }

    public required long StoreCreated { get; init; }

    public IReadOnlyDictionary<string, DataValue> CustomData { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Uri);
        writer.WriteString(Path);
        writer.WriteLong(StoreLastWrite);
        writer.WriteLong(StoreCreated);

        writer.WriteBlockHeader(CustomData.Count);
        foreach (var (customDataKey, customDataValue) in CustomData)
        {
            writer.WriteString(customDataKey);
            customDataValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static Dataspace Read(ref EtpReader reader)
    {
        var uri = reader.ReadString();
        var path = reader.ReadString();
        var storeLastWrite = reader.ReadLong();
        var storeCreated = reader.ReadLong();

        var customData = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var customDataCount = reader.ReadBlockCount(); customDataCount != 0; customDataCount = reader.ReadBlockCount())
        {
            for (var customDataIndex = 0; customDataIndex < customDataCount; customDataIndex++)
            {
                var customDataKey = reader.ReadString();
                customData[customDataKey] = DataValue.Read(ref reader);
            }
        }

        return new Dataspace
        {
            Uri = uri,
            Path = path,
            StoreLastWrite = storeLastWrite,
            StoreCreated = storeCreated,
            CustomData = customData,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.Edge</c> record.</summary>
public sealed record Edge
{
    public required string SourceUri { get; init; }

    public required string TargetUri { get; init; }

    public required RelationshipKind RelationshipKind { get; init; }

    public IReadOnlyDictionary<string, DataValue> CustomData { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(SourceUri);
        writer.WriteString(TargetUri);
        writer.WriteEnum((int)RelationshipKind);

        writer.WriteBlockHeader(CustomData.Count);
        foreach (var (customDataKey, customDataValue) in CustomData)
        {
            writer.WriteString(customDataKey);
            customDataValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static Edge Read(ref EtpReader reader)
    {
        var sourceUri = reader.ReadString();
        var targetUri = reader.ReadString();
        var relationshipKind = (RelationshipKind)reader.ReadEnum(3);

        var customData = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var customDataCount = reader.ReadBlockCount(); customDataCount != 0; customDataCount = reader.ReadBlockCount())
        {
            for (var customDataIndex = 0; customDataIndex < customDataCount; customDataIndex++)
            {
                var customDataKey = reader.ReadString();
                customData[customDataKey] = DataValue.Read(ref reader);
            }
        }

        return new Edge
        {
            SourceUri = sourceUri,
            TargetUri = targetUri,
            RelationshipKind = relationshipKind,
            CustomData = customData,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.PutResponse</c> record.</summary>
public sealed record PutResponse
{
    public IReadOnlyList<string> CreatedContainedObjectUris { get; init; } = [];

    public IReadOnlyList<string> DeletedContainedObjectUris { get; init; } = [];

    public IReadOnlyList<string> JoinedContainedObjectUris { get; init; } = [];

    public IReadOnlyList<string> UnjoinedContainedObjectUris { get; init; } = [];

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(CreatedContainedObjectUris.Count);
        foreach (var createdContainedObjectUrisItem in CreatedContainedObjectUris)
        {
            writer.WriteString(createdContainedObjectUrisItem);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(DeletedContainedObjectUris.Count);
        foreach (var deletedContainedObjectUrisItem in DeletedContainedObjectUris)
        {
            writer.WriteString(deletedContainedObjectUrisItem);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(JoinedContainedObjectUris.Count);
        foreach (var joinedContainedObjectUrisItem in JoinedContainedObjectUris)
        {
            writer.WriteString(joinedContainedObjectUrisItem);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(UnjoinedContainedObjectUris.Count);
        foreach (var unjoinedContainedObjectUrisItem in UnjoinedContainedObjectUris)
        {
            writer.WriteString(unjoinedContainedObjectUrisItem);
        }
        writer.WriteBlockEnd();
    }

    public static PutResponse Read(ref EtpReader reader)
    {
        var createdContainedObjectUris = new List<string>();
        for (var createdContainedObjectUrisCount = reader.ReadBlockCount(); createdContainedObjectUrisCount != 0; createdContainedObjectUrisCount = reader.ReadBlockCount())
        {
            createdContainedObjectUris.EnsureCapacity(createdContainedObjectUris.Count + createdContainedObjectUrisCount);
            for (var createdContainedObjectUrisIndex = 0; createdContainedObjectUrisIndex < createdContainedObjectUrisCount; createdContainedObjectUrisIndex++)
            {
                createdContainedObjectUris.Add(reader.ReadString());
            }
        }

        var deletedContainedObjectUris = new List<string>();
        for (var deletedContainedObjectUrisCount = reader.ReadBlockCount(); deletedContainedObjectUrisCount != 0; deletedContainedObjectUrisCount = reader.ReadBlockCount())
        {
            deletedContainedObjectUris.EnsureCapacity(deletedContainedObjectUris.Count + deletedContainedObjectUrisCount);
            for (var deletedContainedObjectUrisIndex = 0; deletedContainedObjectUrisIndex < deletedContainedObjectUrisCount; deletedContainedObjectUrisIndex++)
            {
                deletedContainedObjectUris.Add(reader.ReadString());
            }
        }

        var joinedContainedObjectUris = new List<string>();
        for (var joinedContainedObjectUrisCount = reader.ReadBlockCount(); joinedContainedObjectUrisCount != 0; joinedContainedObjectUrisCount = reader.ReadBlockCount())
        {
            joinedContainedObjectUris.EnsureCapacity(joinedContainedObjectUris.Count + joinedContainedObjectUrisCount);
            for (var joinedContainedObjectUrisIndex = 0; joinedContainedObjectUrisIndex < joinedContainedObjectUrisCount; joinedContainedObjectUrisIndex++)
            {
                joinedContainedObjectUris.Add(reader.ReadString());
            }
        }

        var unjoinedContainedObjectUris = new List<string>();
        for (var unjoinedContainedObjectUrisCount = reader.ReadBlockCount(); unjoinedContainedObjectUrisCount != 0; unjoinedContainedObjectUrisCount = reader.ReadBlockCount())
        {
            unjoinedContainedObjectUris.EnsureCapacity(unjoinedContainedObjectUris.Count + unjoinedContainedObjectUrisCount);
            for (var unjoinedContainedObjectUrisIndex = 0; unjoinedContainedObjectUrisIndex < unjoinedContainedObjectUrisCount; unjoinedContainedObjectUrisIndex++)
            {
                unjoinedContainedObjectUris.Add(reader.ReadString());
            }
        }

        return new PutResponse
        {
            CreatedContainedObjectUris = createdContainedObjectUris,
            DeletedContainedObjectUris = deletedContainedObjectUris,
            JoinedContainedObjectUris = joinedContainedObjectUris,
            UnjoinedContainedObjectUris = unjoinedContainedObjectUris,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.RelationshipKind</c> enumeration.</summary>
public enum RelationshipKind
{
    Primary = 0,
    Secondary = 1,
    Both = 2,
}

/// <summary>The ETP 1.2 <c>Datatypes.Object.Resource</c> record.</summary>
public sealed record Resource
{
    public required string Uri { get; init; }

    public IReadOnlyList<string> AlternateUris { get; init; } = [];

    public required string Name { get; init; }

    public int? SourceCount { get; init; }

    public int? TargetCount { get; init; }

    public required long LastChanged { get; init; }

    public required long StoreLastWrite { get; init; }

    public required long StoreCreated { get; init; }

    public required ActiveStatusKind ActiveStatus { get; init; }

    public IReadOnlyDictionary<string, DataValue> CustomData { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Uri);

        writer.WriteBlockHeader(AlternateUris.Count);
        foreach (var alternateUrisItem in AlternateUris)
        {
            writer.WriteString(alternateUrisItem);
        }
        writer.WriteBlockEnd();

        writer.WriteString(Name);

        if (SourceCount is { } sourceCountValue)
        {
            writer.WriteUnion(1);
            writer.WriteInt(sourceCountValue);
        }
        else
        {
            writer.WriteUnion(0);
        }

        if (TargetCount is { } targetCountValue)
        {
            writer.WriteUnion(1);
            writer.WriteInt(targetCountValue);
        }
        else
        {
            writer.WriteUnion(0);
        }

        writer.WriteLong(LastChanged);
        writer.WriteLong(StoreLastWrite);
        writer.WriteLong(StoreCreated);
        writer.WriteEnum((int)ActiveStatus);

        writer.WriteBlockHeader(CustomData.Count);
        foreach (var (customDataKey, customDataValue) in CustomData)
        {
            writer.WriteString(customDataKey);
            customDataValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static Resource Read(ref EtpReader reader)
    {
        var uri = reader.ReadString();

        var alternateUris = new List<string>();
        for (var alternateUrisCount = reader.ReadBlockCount(); alternateUrisCount != 0; alternateUrisCount = reader.ReadBlockCount())
        {
            alternateUris.EnsureCapacity(alternateUris.Count + alternateUrisCount);
            for (var alternateUrisIndex = 0; alternateUrisIndex < alternateUrisCount; alternateUrisIndex++)
            {
                alternateUris.Add(reader.ReadString());
            }
        }

        var name = reader.ReadString();
        int? sourceCount = reader.ReadUnion(2) == 1 ? reader.ReadInt() : (int?)null;
        int? targetCount = reader.ReadUnion(2) == 1 ? reader.ReadInt() : (int?)null;
        var lastChanged = reader.ReadLong();
        var storeLastWrite = reader.ReadLong();
        var storeCreated = reader.ReadLong();
        var activeStatus = (ActiveStatusKind)reader.ReadEnum(2);

        var customData = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var customDataCount = reader.ReadBlockCount(); customDataCount != 0; customDataCount = reader.ReadBlockCount())
        {
            for (var customDataIndex = 0; customDataIndex < customDataCount; customDataIndex++)
            {
                var customDataKey = reader.ReadString();
                customData[customDataKey] = DataValue.Read(ref reader);
            }
        }

        return new Resource
        {
            Uri = uri,
            AlternateUris = alternateUris,
            Name = name,
            SourceCount = sourceCount,
            TargetCount = targetCount,
            LastChanged = lastChanged,
            StoreLastWrite = storeLastWrite,
            StoreCreated = storeCreated,
            ActiveStatus = activeStatus,
            CustomData = customData,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.SupportedDataObject</c> record.</summary>
public sealed record SupportedDataObject
{
    public required string QualifiedType { get; init; }

    public IReadOnlyDictionary<string, DataValue> DataObjectCapabilities { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(QualifiedType);

        writer.WriteBlockHeader(DataObjectCapabilities.Count);
        foreach (var (dataObjectCapabilitiesKey, dataObjectCapabilitiesValue) in DataObjectCapabilities)
        {
            writer.WriteString(dataObjectCapabilitiesKey);
            dataObjectCapabilitiesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static SupportedDataObject Read(ref EtpReader reader)
    {
        var qualifiedType = reader.ReadString();

        var dataObjectCapabilities = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var dataObjectCapabilitiesCount = reader.ReadBlockCount(); dataObjectCapabilitiesCount != 0; dataObjectCapabilitiesCount = reader.ReadBlockCount())
        {
            for (var dataObjectCapabilitiesIndex = 0; dataObjectCapabilitiesIndex < dataObjectCapabilitiesCount; dataObjectCapabilitiesIndex++)
            {
                var dataObjectCapabilitiesKey = reader.ReadString();
                dataObjectCapabilities[dataObjectCapabilitiesKey] = DataValue.Read(ref reader);
            }
        }

        return new SupportedDataObject
        {
            QualifiedType = qualifiedType,
            DataObjectCapabilities = dataObjectCapabilities,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.SupportedProtocol</c> record.</summary>
public sealed record SupportedProtocol
{
    public required int Protocol { get; init; }

    public required ProtocolVersion ProtocolVersion { get; init; }

    public required string Role { get; init; }

    public IReadOnlyDictionary<string, DataValue> ProtocolCapabilities { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt(Protocol);
        ProtocolVersion.Write(writer);
        writer.WriteString(Role);

        writer.WriteBlockHeader(ProtocolCapabilities.Count);
        foreach (var (protocolCapabilitiesKey, protocolCapabilitiesValue) in ProtocolCapabilities)
        {
            writer.WriteString(protocolCapabilitiesKey);
            protocolCapabilitiesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static SupportedProtocol Read(ref EtpReader reader)
    {
        var protocol = reader.ReadInt();
        var protocolVersion = ProtocolVersion.Read(ref reader);
        var role = reader.ReadString();

        var protocolCapabilities = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var protocolCapabilitiesCount = reader.ReadBlockCount(); protocolCapabilitiesCount != 0; protocolCapabilitiesCount = reader.ReadBlockCount())
        {
            for (var protocolCapabilitiesIndex = 0; protocolCapabilitiesIndex < protocolCapabilitiesCount; protocolCapabilitiesIndex++)
            {
                var protocolCapabilitiesKey = reader.ReadString();
                protocolCapabilities[protocolCapabilitiesKey] = DataValue.Read(ref reader);
            }
        }

        return new SupportedProtocol
        {
            Protocol = protocol,
            ProtocolVersion = protocolVersion,
            Role = role,
            ProtocolCapabilities = protocolCapabilities,
        };
    }
}

/// <summary>The ETP 1.2 <c>Datatypes.Version</c> record.</summary>
public sealed record ProtocolVersion
{
    public int Major { get; init; }

    public int Minor { get; init; }

    public int Revision { get; init; }

    public int Patch { get; init; }

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt(Major);
        writer.WriteInt(Minor);
        writer.WriteInt(Revision);
        writer.WriteInt(Patch);
    }

    public static ProtocolVersion Read(ref EtpReader reader)
    {
        var major = reader.ReadInt();
        var minor = reader.ReadInt();
        var revision = reader.ReadInt();
        var patch = reader.ReadInt();

        return new ProtocolVersion
        {
            Major = major,
            Minor = minor,
            Revision = revision,
            Patch = patch,
        };
    }
}
