using SqlFlow.Core.Hosting;

namespace SqlFlow.Delivery.Hosting;

/// <summary>
/// How OSDU Delivery introduces itself, in the one place every host reads it from: the product name prose, page titles
/// and notification subjects carry, and the lockup the login page, the workbench title bar, the CLI banner and the
/// OpenAPI document carry, which is also what explains the <c>sqlflow</c> binary name.
/// </summary>
public static class OsduDeliveryBranding
{
    /// <summary>The product's name.</summary>
    public const string ProductName = "OSDU Delivery";

    /// <summary>The line where the product introduces itself.</summary>
    public const string Lockup = "OSDU Delivery, powered by SQLFlow";

    /// <summary>The CLI banner's text after <c>sqlflow - </c>.</summary>
    public const string CliTagline = "the OSDU Delivery command line, powered by SQLFlow";

    /// <summary>The branding every OSDU Delivery host passes to the platform's entry point.</summary>
    public static ProductBranding Product { get; } = new(ProductName, Lockup, CliTagline);
}
