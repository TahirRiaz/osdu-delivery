using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Providers.MySql;
using SqlFlow.Providers.Postgres;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The provider seams that need no live database: type mappers (every mapping row), SQL dialects,
/// canonicalizer gates, and the composite factories' closed-registry errors.</summary>
public sealed class SourceProviderTests
{
    private static CatalogColumn Col(string nativeType) => new() { Name = "C", Ordinal = 1, NativeType = nativeType };

    // ---- MySQL type mapping (SSMA-grounded, with the documented deviations) ----

    [Theory]
    [InlineData("tinyint", "smallint")]                       // MySQL tinyint is signed
    [InlineData("tinyint(1)", "bit")]                         // boolean idiom
    [InlineData("tinyint unsigned", "tinyint")]
    [InlineData("smallint", "smallint")]
    [InlineData("smallint unsigned", "int")]
    [InlineData("mediumint", "int")]
    [InlineData("mediumint unsigned", "int")]
    [InlineData("int", "int")]
    [InlineData("int unsigned", "bigint")]
    [InlineData("bigint", "bigint")]
    [InlineData("bigint unsigned", "decimal(20, 0)")]         // deviation: SSMA's bigint overflows
    [InlineData("decimal(10,2)", "decimal(10, 2)")]
    [InlineData("numeric(18,4)", "decimal(18, 4)")]
    [InlineData("float", "real")]
    [InlineData("double", "float(53)")]
    [InlineData("bit(1)", "bit")]
    [InlineData("bit(12)", "binary(2)")]
    [InlineData("date", "date")]
    [InlineData("datetime(3)", "datetime2(3)")]
    [InlineData("timestamp(6)", "datetime2(6)")]              // deviation: not legacy datetime
    [InlineData("time(3)", "time(3)")]
    [InlineData("year", "smallint")]
    [InlineData("char(10)", "nchar(10)")]
    [InlineData("varchar(255)", "nvarchar(255)")]
    [InlineData("varchar(5000)", "nvarchar(max)")]
    [InlineData("tinytext", "nvarchar(255)")]
    [InlineData("longtext", "nvarchar(max)")]
    [InlineData("binary(16)", "binary(16)")]
    [InlineData("varbinary(9000)", "varbinary(max)")]
    [InlineData("blob", "varbinary(max)")]
    [InlineData("enum('a','b')", "nvarchar(255)")]
    [InlineData("set('a','b')", "nvarchar(max)")]
    [InlineData("json", "nvarchar(max)")]
    [InlineData("geometry", "varbinary(max)")]
    public void MySqlTypes_MapToSqlServer(string mysql, string expected)
        => Assert.Equal(expected, new MySqlSourceTypeMapper().ToSqlServerType(Col(mysql)));

    [Theory]
    [InlineData("decimal(40,2)")]   // precision beyond SQL Server's 38: silent capping would corrupt data
    [InlineData("frobnicate")]      // unknown type: must throw, never degrade
    public void MySqlTypes_Unmappable_Throw(string mysql)
    {
        var ex = Assert.Throws<SqlFlowException>(() => new MySqlSourceTypeMapper().ToSqlServerType(Col(mysql)));
        Assert.Contains("'C'", ex.Message, StringComparison.Ordinal);
    }

    // ---- PostgreSQL type mapping ----

    [Theory]
    [InlineData("bool", "bit")]
    [InlineData("int2", "smallint")]
    [InlineData("int4", "int")]
    [InlineData("int8", "bigint")]
    [InlineData("oid", "bigint")]
    [InlineData("numeric(10,2)", "decimal(10, 2)")]
    [InlineData("float4", "real")]
    [InlineData("float8", "float(53)")]
    [InlineData("money", "decimal(19, 2)")]
    [InlineData("date", "date")]
    [InlineData("timestamp(6)", "datetime2(6)")]
    [InlineData("timestamptz(6)", "datetimeoffset(6)")]       // a UTC instant keeps its offset type
    [InlineData("time(3)", "time(3)")]
    [InlineData("timetz", "nvarchar(50)")]
    [InlineData("interval", "nvarchar(50)")]
    [InlineData("bpchar(10)", "nchar(10)")]
    [InlineData("varchar(50)", "nvarchar(50)")]
    [InlineData("varchar", "nvarchar(max)")]                  // unmodified varchar is unlimited
    [InlineData("text", "nvarchar(max)")]
    [InlineData("bytea", "varbinary(max)")]
    [InlineData("uuid", "uniqueidentifier")]
    [InlineData("json", "nvarchar(max)")]
    [InlineData("jsonb", "nvarchar(max)")]
    [InlineData("xml", "xml")]
    [InlineData("_int4", "nvarchar(max)")]                    // arrays ride as text
    [InlineData("inet", "nvarchar(max)")]
    public void PostgresTypes_MapToSqlServer(string postgres, string expected)
        => Assert.Equal(expected, new PostgresSourceTypeMapper().ToSqlServerType(Col(postgres)));

    [Theory]
    [InlineData("numeric")]         // unbounded numeric: needs explicit precision
    [InlineData("numeric(50,2)")]   // precision beyond 38
    [InlineData("frobnicate")]
    public void PostgresTypes_Unmappable_Throw(string postgres)
    {
        var ex = Assert.Throws<SqlFlowException>(() => new PostgresSourceTypeMapper().ToSqlServerType(Col(postgres)));
        Assert.Contains("'C'", ex.Message, StringComparison.Ordinal);
    }

    // ---- Dialects ----

    private static readonly RelationalObject Table = new() { Database = "erp", Schema = "erp", Name = "orders" };

    [Fact]
    public void MySqlDialect_Backticks_DateSub_DatabaseQualification()
    {
        var dialect = new MySqlSourceDialect();
        Assert.Equal("`o``dd`", dialect.QuoteIdentifier("o`dd"));
        Assert.Equal("`erp`.`orders`", dialect.QualifyObject(Table));    // schema part IS the database
        Assert.Equal("DATE_SUB(MIN(`d`), INTERVAL 7 DAY)", dialect.DateSubtractDays("MIN(`d`)", 7));
        Assert.Equal("0xDEAD", dialect.FormatBinaryLiteral([0xDE, 0xAD]));
    }

    [Fact]
    public void PostgresDialect_DoubleQuotes_Interval_ByteaLiteral()
    {
        var dialect = new PostgresSourceDialect();
        Assert.Equal("\"o\"\"dd\"", dialect.QuoteIdentifier("o\"dd"));
        Assert.Equal("\"erp\".\"orders\"", dialect.QualifyObject(Table));
        Assert.Equal("(MIN(\"d\") - INTERVAL '7 days')", dialect.DateSubtractDays("MIN(\"d\")", 7));
        Assert.Equal("'\\xDEAD'::bytea", dialect.FormatBinaryLiteral([0xDE, 0xAD]));
    }

    [Fact]
    public void SqlServerDialect_Brackets_DateAdd_HexLiteral()
    {
        var dialect = new SqlServerSourceDialect();
        Assert.Equal("[o]]dd]", dialect.QuoteIdentifier("o]dd"));
        Assert.Equal("[erp].[orders]", dialect.QualifyObject(Table));
        Assert.Equal("DATEADD(day, -7, MIN([d]))", dialect.DateSubtractDays("MIN([d])", 7));
        Assert.Equal("0xDEAD", dialect.FormatBinaryLiteral([0xDE, 0xAD]));
    }

    // ---- Canonicalizers (builder parsing is local, no live database) ----

    [Fact]
    public void MySqlCanonicalizer_TrustedPasses_AndRedacts()
    {
        var canonical = new MySqlConnectionStringCanonicalizer()
            .Canonicalize("Server=db;Database=erp;User ID=u;Password=p", ConnectionRole.Source, SecretlessPolicy.Trusted);
        Assert.Contains("Password", canonical.Canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=p", canonical.Redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQLFlow Source", canonical.Canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void MySqlCanonicalizer_InlineLiteral_RejectedClearly()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new MySqlConnectionStringCanonicalizer()
            .Canonicalize("Server=db;User ID=u;Password=p", ConnectionRole.Source, SecretlessPolicy.RequireSelfAuthenticating));
        Assert.Contains("${...}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresCanonicalizer_TrustedPasses_InlineRejected()
    {
        var canonicalizer = new PostgresConnectionStringCanonicalizer();
        var canonical = canonicalizer.Canonicalize("Host=db;Database=erp;Username=u;Password=p", ConnectionRole.Source, SecretlessPolicy.Trusted);
        Assert.DoesNotContain("Password=p", canonical.Redacted, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<SqlFlowException>(() => canonicalizer
            .Canonicalize("Host=db;Username=u;Password=p", ConnectionRole.Source, SecretlessPolicy.RequireSelfAuthenticating));
    }

    // ---- Composite registries: a closed set with clear errors, never a silent null ----

    [Fact]
    public async Task CompositeConnectionFactory_UnknownKind_FailsClearly()
    {
        var composite = new CompositeConnectionFactory([new SqlConnectionFactory()]);
        var resolved = Resolved(DataSourceKind.PostgreSQL);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => composite.OpenAsync(resolved));
        Assert.Contains("PostgreSQL", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SqlFlow.Providers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeCatalogReaderFactory_DispatchesByKind()
    {
        var composite = new CompositeCatalogReaderFactory([new MySqlCatalogReader(), new PostgresCatalogReader()]);
        Assert.IsType<MySqlCatalogReader>(composite.ReaderFor(Resolved(DataSourceKind.MySQL)));
        Assert.IsType<PostgresCatalogReader>(composite.ReaderFor(Resolved(DataSourceKind.PostgreSQL)));
        Assert.Throws<SqlFlowException>(() => composite.ReaderFor(Resolved(DataSourceKind.MSSQL)));
    }

    private static ResolvedConnection Resolved(DataSourceKind kind) => new()
    {
        Kind = kind,
        CanonicalString = "Server=x;Database=y;",
        RedactedString = "Server=x;Database=y;",
    };
}
