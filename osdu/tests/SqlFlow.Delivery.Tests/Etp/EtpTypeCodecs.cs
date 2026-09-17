// Written from the pinned ETP 1.2 protocol, osdu/specs/reservoir-ddms/etp-1.2.avpr: the C# record that answers for
// each schema name. It says only which type is which; the field order and the types of the fields are what
// EtpSchemaTests checks, against a codec driven by the schema itself.

using SqlFlow.Delivery.Engine.Protocols.Etp;

namespace SqlFlow.Delivery.Tests.Etp;

/// <summary>Every ETP type the module implements, by the name the pinned protocol gives it.</summary>
internal static class EtpTypeCodecs
{
    /// <summary>The schema names the module has a record for, in the order the protocol declares them.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "Energistics.Etp.v12.Datatypes.MessageHeader",
        "Energistics.Etp.v12.Datatypes.AnyArray",
        "Energistics.Etp.v12.Datatypes.AnyArrayType",
        "Energistics.Etp.v12.Datatypes.AnyLogicalArrayType",
        "Energistics.Etp.v12.Datatypes.AnySparseArray",
        "Energistics.Etp.v12.Datatypes.AnySubarray",
        "Energistics.Etp.v12.Datatypes.ArrayOfBoolean",
        "Energistics.Etp.v12.Datatypes.ArrayOfBytes",
        "Energistics.Etp.v12.Datatypes.ArrayOfDouble",
        "Energistics.Etp.v12.Datatypes.ArrayOfFloat",
        "Energistics.Etp.v12.Datatypes.ArrayOfInt",
        "Energistics.Etp.v12.Datatypes.ArrayOfLong",
        "Energistics.Etp.v12.Datatypes.ArrayOfNullableBoolean",
        "Energistics.Etp.v12.Datatypes.ArrayOfNullableInt",
        "Energistics.Etp.v12.Datatypes.ArrayOfNullableLong",
        "Energistics.Etp.v12.Datatypes.ArrayOfString",
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArray",
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArrayIdentifier",
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArrayMetadata",
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.GetDataSubarraysType",
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutDataArraysType",
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutDataSubarraysType",
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutUninitializedDataArrayType",
        "Energistics.Etp.v12.Datatypes.DataValue",
        "Energistics.Etp.v12.Datatypes.ErrorInfo",
        "Energistics.Etp.v12.Datatypes.Object.ActiveStatusKind",
        "Energistics.Etp.v12.Datatypes.Object.ContextInfo",
        "Energistics.Etp.v12.Datatypes.Object.ContextScopeKind",
        "Energistics.Etp.v12.Datatypes.Object.DataObject",
        "Energistics.Etp.v12.Datatypes.Object.Dataspace",
        "Energistics.Etp.v12.Datatypes.Object.Edge",
        "Energistics.Etp.v12.Datatypes.Object.PutResponse",
        "Energistics.Etp.v12.Datatypes.Object.RelationshipKind",
        "Energistics.Etp.v12.Datatypes.Object.Resource",
        "Energistics.Etp.v12.Datatypes.SupportedDataObject",
        "Energistics.Etp.v12.Datatypes.SupportedProtocol",
        "Energistics.Etp.v12.Datatypes.Version",
        "Energistics.Etp.v12.Protocol.Core.RequestSession",
        "Energistics.Etp.v12.Protocol.Core.OpenSession",
        "Energistics.Etp.v12.Protocol.Core.CloseSession",
        "Energistics.Etp.v12.Protocol.Core.Authorize",
        "Energistics.Etp.v12.Protocol.Core.AuthorizeResponse",
        "Energistics.Etp.v12.Protocol.Core.Ping",
        "Energistics.Etp.v12.Protocol.Core.Pong",
        "Energistics.Etp.v12.Protocol.Core.ProtocolException",
        "Energistics.Etp.v12.Protocol.Core.Acknowledge",
        "Energistics.Etp.v12.Protocol.Dataspace.GetDataspaces",
        "Energistics.Etp.v12.Protocol.Dataspace.GetDataspacesResponse",
        "Energistics.Etp.v12.Protocol.Dataspace.PutDataspaces",
        "Energistics.Etp.v12.Protocol.Dataspace.PutDataspacesResponse",
        "Energistics.Etp.v12.Protocol.Dataspace.DeleteDataspaces",
        "Energistics.Etp.v12.Protocol.Dataspace.DeleteDataspacesResponse",
        "Energistics.Etp.v12.Protocol.DataspaceOSDU.GetDataspaceInfo",
        "Energistics.Etp.v12.Protocol.DataspaceOSDU.GetDataspaceInfoResponse",
        "Energistics.Etp.v12.Protocol.DataspaceOSDU.LockDataspaces",
        "Energistics.Etp.v12.Protocol.DataspaceOSDU.LockDataspacesResponse",
        "Energistics.Etp.v12.Protocol.Store.GetDataObjects",
        "Energistics.Etp.v12.Protocol.Store.GetDataObjectsResponse",
        "Energistics.Etp.v12.Protocol.Store.PutDataObjects",
        "Energistics.Etp.v12.Protocol.Store.PutDataObjectsResponse",
        "Energistics.Etp.v12.Protocol.Store.DeleteDataObjects",
        "Energistics.Etp.v12.Protocol.Store.DeleteDataObjectsResponse",
        "Energistics.Etp.v12.Protocol.Store.Chunk",
        "Energistics.Etp.v12.Protocol.DataArray.GetDataArrays",
        "Energistics.Etp.v12.Protocol.DataArray.GetDataArraysResponse",
        "Energistics.Etp.v12.Protocol.DataArray.GetDataSubarrays",
        "Energistics.Etp.v12.Protocol.DataArray.GetDataSubarraysResponse",
        "Energistics.Etp.v12.Protocol.DataArray.PutDataArrays",
        "Energistics.Etp.v12.Protocol.DataArray.PutDataArraysResponse",
        "Energistics.Etp.v12.Protocol.DataArray.PutDataSubarrays",
        "Energistics.Etp.v12.Protocol.DataArray.PutDataSubarraysResponse",
        "Energistics.Etp.v12.Protocol.DataArray.PutUninitializedDataArrays",
        "Energistics.Etp.v12.Protocol.DataArray.PutUninitializedDataArraysResponse",
        "Energistics.Etp.v12.Protocol.DataArray.GetDataArrayMetadata",
        "Energistics.Etp.v12.Protocol.DataArray.GetDataArrayMetadataResponse",
        "Energistics.Etp.v12.Protocol.Transaction.StartTransaction",
        "Energistics.Etp.v12.Protocol.Transaction.StartTransactionResponse",
        "Energistics.Etp.v12.Protocol.Transaction.CommitTransaction",
        "Energistics.Etp.v12.Protocol.Transaction.CommitTransactionResponse",
        "Energistics.Etp.v12.Protocol.Transaction.RollbackTransaction",
        "Energistics.Etp.v12.Protocol.Transaction.RollbackTransactionResponse",
        "Energistics.Etp.v12.Protocol.Discovery.GetResources",
        "Energistics.Etp.v12.Protocol.Discovery.GetResourcesResponse",
        "Energistics.Etp.v12.Protocol.Discovery.GetResourcesEdgesResponse",
    ];

    /// <summary>Reads one value of the named type with the module's own record.</summary>
    public static object Read(string name, ref EtpReader reader) => name switch
    {
        "Energistics.Etp.v12.Datatypes.MessageHeader" => MessageHeader.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.AnyArray" => AnyArray.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.AnyArrayType" => (AnyArrayType)reader.ReadEnum(7),
        "Energistics.Etp.v12.Datatypes.AnyLogicalArrayType" => (AnyLogicalArrayType)reader.ReadEnum(21),
        "Energistics.Etp.v12.Datatypes.AnySparseArray" => AnySparseArray.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.AnySubarray" => AnySubarray.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfBoolean" => ArrayOfBoolean.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfBytes" => ArrayOfBytes.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfDouble" => ArrayOfDouble.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfFloat" => ArrayOfFloat.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfInt" => ArrayOfInt.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfLong" => ArrayOfLong.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfNullableBoolean" => ArrayOfNullableBoolean.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfNullableInt" => ArrayOfNullableInt.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfNullableLong" => ArrayOfNullableLong.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ArrayOfString" => ArrayOfString.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArray" => DataArray.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArrayIdentifier" => DataArrayIdentifier.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArrayMetadata" => DataArrayMetadata.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.GetDataSubarraysType" => GetDataSubarraysType.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutDataArraysType" => PutDataArraysType.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutDataSubarraysType" => PutDataSubarraysType.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutUninitializedDataArrayType" => PutUninitializedDataArrayType.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.DataValue" => DataValue.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.ErrorInfo" => ErrorInfo.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.Object.ActiveStatusKind" => (ActiveStatusKind)reader.ReadEnum(2),
        "Energistics.Etp.v12.Datatypes.Object.ContextInfo" => ContextInfo.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.Object.ContextScopeKind" => (ContextScopeKind)reader.ReadEnum(5),
        "Energistics.Etp.v12.Datatypes.Object.DataObject" => DataObject.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.Object.Dataspace" => Dataspace.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.Object.Edge" => Edge.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.Object.PutResponse" => PutResponse.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.Object.RelationshipKind" => (RelationshipKind)reader.ReadEnum(3),
        "Energistics.Etp.v12.Datatypes.Object.Resource" => Resource.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.SupportedDataObject" => SupportedDataObject.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.SupportedProtocol" => SupportedProtocol.Read(ref reader),
        "Energistics.Etp.v12.Datatypes.Version" => ProtocolVersion.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.RequestSession" => RequestSession.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.OpenSession" => OpenSession.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.CloseSession" => CloseSession.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.Authorize" => Authorize.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.AuthorizeResponse" => AuthorizeResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.Ping" => Ping.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.Pong" => Pong.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.ProtocolException" => ProtocolFailure.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Core.Acknowledge" => Acknowledge.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Dataspace.GetDataspaces" => GetDataspaces.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Dataspace.GetDataspacesResponse" => GetDataspacesResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Dataspace.PutDataspaces" => PutDataspaces.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Dataspace.PutDataspacesResponse" => PutDataspacesResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Dataspace.DeleteDataspaces" => DeleteDataspaces.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Dataspace.DeleteDataspacesResponse" => DeleteDataspacesResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataspaceOSDU.GetDataspaceInfo" => GetDataspaceInfo.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataspaceOSDU.GetDataspaceInfoResponse" => GetDataspaceInfoResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataspaceOSDU.LockDataspaces" => LockDataspaces.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataspaceOSDU.LockDataspacesResponse" => LockDataspacesResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Store.GetDataObjects" => GetDataObjects.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Store.GetDataObjectsResponse" => GetDataObjectsResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Store.PutDataObjects" => PutDataObjects.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Store.PutDataObjectsResponse" => PutDataObjectsResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Store.DeleteDataObjects" => DeleteDataObjects.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Store.DeleteDataObjectsResponse" => DeleteDataObjectsResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Store.Chunk" => Chunk.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.GetDataArrays" => GetDataArrays.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.GetDataArraysResponse" => GetDataArraysResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.GetDataSubarrays" => GetDataSubarrays.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.GetDataSubarraysResponse" => GetDataSubarraysResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.PutDataArrays" => PutDataArrays.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.PutDataArraysResponse" => PutDataArraysResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.PutDataSubarrays" => PutDataSubarrays.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.PutDataSubarraysResponse" => PutDataSubarraysResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.PutUninitializedDataArrays" => PutUninitializedDataArrays.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.PutUninitializedDataArraysResponse" => PutUninitializedDataArraysResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.GetDataArrayMetadata" => GetDataArrayMetadata.Read(ref reader),
        "Energistics.Etp.v12.Protocol.DataArray.GetDataArrayMetadataResponse" => GetDataArrayMetadataResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Transaction.StartTransaction" => StartTransaction.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Transaction.StartTransactionResponse" => StartTransactionResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Transaction.CommitTransaction" => CommitTransaction.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Transaction.CommitTransactionResponse" => CommitTransactionResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Transaction.RollbackTransaction" => RollbackTransaction.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Transaction.RollbackTransactionResponse" => RollbackTransactionResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Discovery.GetResources" => GetResources.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Discovery.GetResourcesResponse" => GetResourcesResponse.Read(ref reader),
        "Energistics.Etp.v12.Protocol.Discovery.GetResourcesEdgesResponse" => GetResourcesEdgesResponse.Read(ref reader),
        _ => throw new InvalidOperationException($"The module has no ETP record for {name}."),
    };

    /// <summary>Writes one value of the named type with the module's own record.</summary>
    public static void Write(string name, object value, EtpWriter writer)
    {
        switch (name)
        {
            case "Energistics.Etp.v12.Datatypes.MessageHeader": ((MessageHeader)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.AnyArray": ((AnyArray)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.AnyArrayType": writer.WriteEnum((int)(AnyArrayType)value); break;
            case "Energistics.Etp.v12.Datatypes.AnyLogicalArrayType": writer.WriteEnum((int)(AnyLogicalArrayType)value); break;
            case "Energistics.Etp.v12.Datatypes.AnySparseArray": ((AnySparseArray)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.AnySubarray": ((AnySubarray)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfBoolean": ((ArrayOfBoolean)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfBytes": ((ArrayOfBytes)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfDouble": ((ArrayOfDouble)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfFloat": ((ArrayOfFloat)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfInt": ((ArrayOfInt)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfLong": ((ArrayOfLong)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfNullableBoolean": ((ArrayOfNullableBoolean)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfNullableInt": ((ArrayOfNullableInt)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfNullableLong": ((ArrayOfNullableLong)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ArrayOfString": ((ArrayOfString)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArray": ((DataArray)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArrayIdentifier": ((DataArrayIdentifier)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.DataArrayTypes.DataArrayMetadata": ((DataArrayMetadata)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.DataArrayTypes.GetDataSubarraysType": ((GetDataSubarraysType)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutDataArraysType": ((PutDataArraysType)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutDataSubarraysType": ((PutDataSubarraysType)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.DataArrayTypes.PutUninitializedDataArrayType": ((PutUninitializedDataArrayType)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.DataValue": ((DataValue)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.ErrorInfo": ((ErrorInfo)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.Object.ActiveStatusKind": writer.WriteEnum((int)(ActiveStatusKind)value); break;
            case "Energistics.Etp.v12.Datatypes.Object.ContextInfo": ((ContextInfo)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.Object.ContextScopeKind": writer.WriteEnum((int)(ContextScopeKind)value); break;
            case "Energistics.Etp.v12.Datatypes.Object.DataObject": ((DataObject)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.Object.Dataspace": ((Dataspace)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.Object.Edge": ((Edge)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.Object.PutResponse": ((PutResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.Object.RelationshipKind": writer.WriteEnum((int)(RelationshipKind)value); break;
            case "Energistics.Etp.v12.Datatypes.Object.Resource": ((Resource)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.SupportedDataObject": ((SupportedDataObject)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.SupportedProtocol": ((SupportedProtocol)value).Write(writer); break;
            case "Energistics.Etp.v12.Datatypes.Version": ((ProtocolVersion)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.RequestSession": ((RequestSession)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.OpenSession": ((OpenSession)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.CloseSession": ((CloseSession)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.Authorize": ((Authorize)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.AuthorizeResponse": ((AuthorizeResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.Ping": ((Ping)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.Pong": ((Pong)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.ProtocolException": ((ProtocolFailure)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Core.Acknowledge": ((Acknowledge)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Dataspace.GetDataspaces": ((GetDataspaces)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Dataspace.GetDataspacesResponse": ((GetDataspacesResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Dataspace.PutDataspaces": ((PutDataspaces)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Dataspace.PutDataspacesResponse": ((PutDataspacesResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Dataspace.DeleteDataspaces": ((DeleteDataspaces)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Dataspace.DeleteDataspacesResponse": ((DeleteDataspacesResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataspaceOSDU.GetDataspaceInfo": ((GetDataspaceInfo)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataspaceOSDU.GetDataspaceInfoResponse": ((GetDataspaceInfoResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataspaceOSDU.LockDataspaces": ((LockDataspaces)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataspaceOSDU.LockDataspacesResponse": ((LockDataspacesResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Store.GetDataObjects": ((GetDataObjects)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Store.GetDataObjectsResponse": ((GetDataObjectsResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Store.PutDataObjects": ((PutDataObjects)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Store.PutDataObjectsResponse": ((PutDataObjectsResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Store.DeleteDataObjects": ((DeleteDataObjects)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Store.DeleteDataObjectsResponse": ((DeleteDataObjectsResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Store.Chunk": ((Chunk)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.GetDataArrays": ((GetDataArrays)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.GetDataArraysResponse": ((GetDataArraysResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.GetDataSubarrays": ((GetDataSubarrays)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.GetDataSubarraysResponse": ((GetDataSubarraysResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.PutDataArrays": ((PutDataArrays)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.PutDataArraysResponse": ((PutDataArraysResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.PutDataSubarrays": ((PutDataSubarrays)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.PutDataSubarraysResponse": ((PutDataSubarraysResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.PutUninitializedDataArrays": ((PutUninitializedDataArrays)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.PutUninitializedDataArraysResponse": ((PutUninitializedDataArraysResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.GetDataArrayMetadata": ((GetDataArrayMetadata)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.DataArray.GetDataArrayMetadataResponse": ((GetDataArrayMetadataResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Transaction.StartTransaction": ((StartTransaction)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Transaction.StartTransactionResponse": ((StartTransactionResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Transaction.CommitTransaction": ((CommitTransaction)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Transaction.CommitTransactionResponse": ((CommitTransactionResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Transaction.RollbackTransaction": ((RollbackTransaction)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Transaction.RollbackTransactionResponse": ((RollbackTransactionResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Discovery.GetResources": ((GetResources)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Discovery.GetResourcesResponse": ((GetResourcesResponse)value).Write(writer); break;
            case "Energistics.Etp.v12.Protocol.Discovery.GetResourcesEdgesResponse": ((GetResourcesEdgesResponse)value).Write(writer); break;
            default: throw new InvalidOperationException($"The module has no ETP record for {name}.");
        }
    }
}
