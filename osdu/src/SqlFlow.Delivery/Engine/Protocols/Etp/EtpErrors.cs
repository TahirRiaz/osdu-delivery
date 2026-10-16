namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The ETP error codes the server raises (osdu/specs/reservoir-ddms/INTEGRATION.md section 8.3, from the server's own
/// <c>etp/ErrorCodes.h</c>). The numbers are the contract; the names are the server's.
/// </summary>
public static class EtpErrorCodes
{
    public const int Ok = 0;
    public const int NoRole = 1;
    public const int NoSupportedProtocols = 2;
    public const int InvalidMessageType = 3;
    public const int UnsupportedProtocol = 4;
    public const int InvalidArgument = 5;
    public const int RequestDenied = 6;
    public const int NotSupported = 7;
    public const int InvalidState = 8;
    public const int InvalidUri = 9;
    public const int AuthorizationExpired = 10;
    public const int NotFound = 11;
    public const int LimitExceeded = 12;
    public const int CompressionNotSupported = 13;
    public const int InvalidObject = 14;
    public const int MaxTransactionsExceeded = 15;
    public const int DataObjectTypeNotSupported = 16;
    public const int MaxSizeExceeded = 17;
    public const int MultipartCancelled = 18;
    public const int InvalidMessage = 19;
    public const int InvalidIndexKind = 20;
    public const int NoSupportedFormats = 21;
    public const int RequestUuidRejected = 22;
    public const int UpdateGrowingObjectDenied = 23;
    public const int BackpressureLimitExceeded = 24;
    public const int BackpressureWarning = 25;
    public const int TimedOut = 26;
    public const int AuthorizationRequired = 27;
    public const int AuthorizationExpiring = 28;
    public const int NoSupportedDataObjectTypes = 29;

    /// <summary>The server's name for a code, for a log line or an error a person reads.</summary>
    public static string Name(int code) => code switch
    {
        Ok => "IS_OK",
        NoRole => "ENOROLE",
        NoSupportedProtocols => "ENOSUPPORTEDPROTOCOLS",
        InvalidMessageType => "EINVALID_MESSAGETYPE",
        UnsupportedProtocol => "EUNSUPPORTED_PROTOCOL",
        InvalidArgument => "EINVALID_ARGUMENT",
        RequestDenied => "EREQUEST_DENIED",
        NotSupported => "ENOTSUPPORTED",
        InvalidState => "EINVALID_STATE",
        InvalidUri => "EINVALID_URI",
        AuthorizationExpired => "EAUTHORIZATION_EXPIRED",
        NotFound => "ENOT_FOUND",
        LimitExceeded => "ELIMIT_EXCEEDED",
        CompressionNotSupported => "ECOMPRESSION_NOTSUPPORTED",
        InvalidObject => "EINVALID_OBJECT",
        MaxTransactionsExceeded => "EMAX_TRANSACTIONS_EXCEEDED",
        DataObjectTypeNotSupported => "EDATAOBJECTTYPE_NOTSUPPORTED",
        MaxSizeExceeded => "EMAXSIZE_EXCEEDED",
        MultipartCancelled => "EMULTIPART_CANCELLED",
        InvalidMessage => "EINVALID_MESSAGE",
        InvalidIndexKind => "EINVALID_INDEXKIND",
        NoSupportedFormats => "ENOSUPPORTEDFORMATS",
        RequestUuidRejected => "EREQUESTUUID_REJECTED",
        UpdateGrowingObjectDenied => "EUPDATEGROWINGOBJECT_DENIED",
        BackpressureLimitExceeded => "EBACKPRESSURE_LIMIT_EXCEEDED",
        BackpressureWarning => "EBACKPRESSURE_WARNING",
        TimedOut => "ETIMED_OUT",
        AuthorizationRequired => "EAUTHORIZATION_REQUIRED",
        AuthorizationExpiring => "EAUTHORIZATION_EXPIRING",
        NoSupportedDataObjectTypes => "ENOSUPPORTEDDATAOBJECTTYPES",
        1002 => "EINVALID_CHANNELID",
        4003 => "ENOCASCADE_DELETE",
        4004 => "EPLURAL_OBJECT",
        5001 => "ERETENTION_PERIOD_EXCEEDED",
        6001 => "ENOTGROWINGOBJECT",
        _ => $"code {code}",
    };

    /// <summary>
    /// Whether sending the same request again can succeed (INTEGRATION.md section 8.4): a write lock another session
    /// holds, a server under load, a session the server is closing, and a token that can be refreshed. Everything else
    /// needs the request or the estate to change first, so the record is held rather than retried.
    /// </summary>
    public static bool Retryable(int code, string? message = null) => code switch
    {
        MaxTransactionsExceeded or BackpressureLimitExceeded or BackpressureWarning or TimedOut => true,
        AuthorizationRequired or AuthorizationExpired or AuthorizationExpiring => true,
        InvalidState => message is not null && message.Contains("Session is about to close", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>Whether the request has to become smaller before it is sent again (a message or an array past a limit).</summary>
    public static bool TooLarge(int code) => code is MaxSizeExceeded or LimitExceeded;
}

/// <summary>
/// An error the ETP server reported, as the code and text it sent. It is a delivery failure like any other; the route
/// decides from <see cref="Retryable"/> whether the record is retried or held.
/// </summary>
public sealed class EtpProtocolException : DeliveryException
{
    public EtpProtocolException(int code, string message, string? request = null)
        : base(Describe(code, message, request))
    {
        Code = code;
        ServerMessage = message;
        Request = request;
    }

    public int Code { get; }

    /// <summary>The server's own text, kept apart from the sentence this exception reads as.</summary>
    public string ServerMessage { get; }

    /// <summary>The request the error answers, when it answered one.</summary>
    public string? Request { get; }

    public bool Retryable => EtpErrorCodes.Retryable(Code, ServerMessage);

    public bool TooLarge => EtpErrorCodes.TooLarge(Code);

    private static string Describe(int code, string message, string? request)
        => request is null
            ? $"The Reservoir DDMS answered {EtpErrorCodes.Name(code)}: {message}"
            : $"The Reservoir DDMS answered {request} with {EtpErrorCodes.Name(code)}: {message}";
}
