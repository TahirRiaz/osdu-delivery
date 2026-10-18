using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The suites run in two tiers (CONTRIBUTING.md, Tests). The unit tier leaves out every test that carries
/// <c>[Trait("Category", "Integration")]</c> and runs with no SQL Server at all, as CI's Build and test job does, so a test
/// that needs the database has to carry it. This assembly's SQL Server tests are the classes in the
/// <see cref="SqlServerSuite"/>, which the database refuses any other test, so each of them has to be an integration test.
/// The file is linked into every suite that holds such classes, and checks the assembly it is compiled into.
/// </summary>
public sealed class TestTierTests
{
    /// <summary>The trait that puts a test in the integration tier.</summary>
    public const string Category = "Category";

    /// <summary>The value of <see cref="Category"/> for a test that needs SQL Server, a host or another process.</summary>
    public const string Integration = "Integration";

    [Fact]
    public void Every_class_in_the_SQL_Server_collection_is_an_integration_test()
    {
        var unmarked = typeof(TestTierTests).Assembly.GetTypes()
            .Where(type => type.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType == typeof(CollectionAttribute)
                && attribute.ConstructorArguments.Count == 1
                && Equals(attribute.ConstructorArguments[0].Value, SqlServerSuite.Name)))
            .Where(type => !type.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && attribute.ConstructorArguments.Count == 2
                && Equals(attribute.ConstructorArguments[0].Value, Category)
                && Equals(attribute.ConstructorArguments[1].Value, Integration)))
            .Select(type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unmarked.Count == 0,
            $"These classes are in the {SqlServerSuite.Name} collection without [Trait(\"{Category}\", \"{Integration}\")], so the unit tier "
            + $"would run them with no database: {string.Join(", ", unmarked)}.");
    }
}
