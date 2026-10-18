using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The partitions the control plane's configuration names (<c>Osdu:Partitions</c>), written into the partition registry and
/// the central configuration on SQL Server: registered with their values and their default, taken over when registered by
/// hand, kept as configured against every other change, released (not removed) when the configuration stops naming them,
/// and written again without a change when nothing changed. Every test starts from an empty module.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class ConfiguredPartitionsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 14, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Now.AddHours(1);

    private readonly OsduTestDatabase _module = new();

    public void Dispose() => _module.Dispose();

    private DeliveryPartitionRegistry Registry() => new(_module.CreateDbContext);

    private DeliveryConfigStore Config() => new(_module.CreateDbContext);

    private static ConfiguredPartition Partition(string name, bool isDefault = false, string? description = null, params (string Name, string Value)[] properties)
        => new(name, description, isDefault, properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));

    private static ConfiguredPartition[] DevAndTest() =>
    [
        Partition("dev", isDefault: true, "The development platform",
            ("OSDU_URL", "https://osdu-dev.example.com"),
            ("OSDU_CLIENT_ID", "11111111-1111-1111-1111-111111111111"),
            ("OSDU_CLIENT_SECRET", "${keyvault:estate-vault/osdu-dev-client-secret}")),
        Partition("test", isDefault: false, null, ("OSDU_URL", "https://osdu-test.example.com")),
    ];

    [Fact]
    public async Task The_configuration_registers_its_partitions_with_their_default_and_the_values_their_runs_resolve()
    {
        var applied = await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), Now);

        Assert.Equal(["dev", "test"], applied.Registered);
        Assert.Equal("dev", applied.DefaultNow);
        Assert.Equal(4, applied.PropertiesWritten);

        var rows = await Registry().ListAsync();
        Assert.All(rows, r => Assert.True(r.IsConfigured));
        Assert.All(rows, r => Assert.Equal(ConfiguredPartitions.Actor, r.CreatedBy));
        Assert.Equal("dev", (await Registry().ReadAsync()).Default);
        Assert.Equal("The development platform", rows.Single(r => r.Name == "dev").Description);

        // A run bound to a partition resolves with that partition's own values; the secret travels as its reference.
        var dev = await Config().EffectiveAsync(repoId: null, "dev");
        Assert.Equal("https://osdu-dev.example.com", dev["OSDU_URL"]);
        Assert.Equal("${keyvault:estate-vault/osdu-dev-client-secret}", dev["OSDU_CLIENT_SECRET"]);
        Assert.Equal("https://osdu-test.example.com", (await Config().EffectiveAsync(repoId: null, "test"))["OSDU_URL"]);
        Assert.All(await Config().ListAsync(null, null), p => Assert.True(p.IsConfigured));
    }

    [Fact]
    public async Task Writing_the_same_configuration_again_changes_nothing()
    {
        await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), Now);

        var again = await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), Later);

        Assert.False(again.Changed);
        Assert.All(await Registry().ListAsync(), r => Assert.Equal(Now, r.UpdatedUtc));
        Assert.All(await Config().ListAsync(null, null), p => Assert.Equal(Now, p.UpdatedUtc));
    }

    [Fact]
    public async Task A_partition_registered_by_hand_is_taken_over_with_its_value_and_made_the_configured_default()
    {
        await Registry().AddAsync("dev", "by hand", makeDefault: false, "user:ops", Now);
        await Registry().AddAsync("Prod", null, makeDefault: false, "user:ops", Now);
        await Config().SetAsync(null, "Prod", "OSDU_URL", "https://old.example.com", null, "user:ops", Now, catalog: null);

        var applied = await ConfiguredPartitions.ApplyAsync(
            _module.CreateDbContext, [Partition("prod", isDefault: true, "Production", ("OSDU_URL", "https://osdu.example.com"))], Later);

        Assert.Empty(applied.Registered);
        Assert.Equal(["Prod"], applied.Adopted);
        Assert.Equal("Prod", applied.DefaultNow);
        var rows = await Registry().ListAsync();
        var prod = rows.Single(r => r.Name == "Prod");
        Assert.Equal((true, true, "Production", "user:ops"), (prod.IsConfigured, prod.IsDefault, prod.Description, prod.CreatedBy));
        var dev = rows.Single(r => r.Name == "dev");
        Assert.Equal((false, false, "by hand"), (dev.IsConfigured, dev.IsDefault, dev.Description));

        var url = Assert.Single(await Config().ListAsync(null, "Prod"));
        Assert.Equal(("https://osdu.example.com", true, ConfiguredPartitions.Actor), (url.Value, url.IsConfigured, url.UpdatedBy));
    }

    [Fact]
    public async Task What_the_configuration_sets_is_refused_everywhere_else_naming_the_setting_to_change()
    {
        await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), Now);
        await Registry().AddAsync("sandbox", null, makeDefault: false, "user:ops", Now);

        var described = await Assert.ThrowsAsync<SetByConfigurationException>(() => Registry().DescribeAsync("dev", "mine", "user:ops", Later));
        Assert.Equal("Osdu:Partitions:dev:Description", described.Setting);
        var removed = await Assert.ThrowsAsync<SetByConfigurationException>(() => Registry().RemoveAsync("TEST"));
        Assert.Equal("Osdu:Partitions:test", removed.Setting);
        var moved = await Assert.ThrowsAsync<SetByConfigurationException>(() => Registry().MakeDefaultAsync("sandbox", "user:ops", Later));
        Assert.Equal("Osdu:Partitions:dev:Default", moved.Setting);
        Assert.Contains("Osdu:Partitions:sandbox:Default", moved.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SetByConfigurationException>(() => Registry().AddAsync("prod", null, makeDefault: true, "user:ops", Later));

        var set = await Assert.ThrowsAsync<SetByConfigurationException>(
            () => Config().SetAsync(null, "dev", "OSDU_URL", "https://elsewhere.example.com", null, "user:ops", Later, catalog: null));
        Assert.Equal("Osdu:Partitions:dev:Properties:OSDU_URL", set.Setting);
        await Assert.ThrowsAsync<SetByConfigurationException>(() => Config().RemoveAsync(null, "dev", "OSDU_CLIENT_SECRET"));

        // Nothing was changed by the refusals; what the configuration does not set is kept by hand as before.
        Assert.Equal("dev", (await Registry().ReadAsync()).Default);
        Assert.Equal("https://osdu-dev.example.com", (await Config().EffectiveAsync(null, "dev"))["OSDU_URL"]);
        await Registry().DescribeAsync("sandbox", "a sandbox", "user:ops", Later);
        await Config().SetAsync(null, "dev", "OSDU_LEGAL_TAG", "dev-legal", null, "user:ops", Later, catalog: null);
        await Config().SetAsync(null, null, "OSDU_SCOPE", "https://scope.example.com/.default", null, "user:ops", Later, catalog: null);
        Assert.True(await Registry().RemoveAsync("sandbox"));
    }

    [Fact]
    public async Task A_partition_the_configuration_stops_naming_stays_registered_and_is_kept_by_hand_from_then_on()
    {
        await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), Now);

        var applied = await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, [DevAndTest()[0]], Later);

        Assert.Equal(["test"], applied.Released);
        Assert.Equal(1, applied.PropertiesReleased);
        var test = (await Registry().ListAsync()).Single(r => r.Name == "test");
        Assert.Equal((false, ConfiguredPartitions.Actor, Later), (test.IsConfigured, test.UpdatedBy, test.UpdatedUtc));
        var url = Assert.Single(await Config().ListAsync(null, "test"));
        Assert.Equal(("https://osdu-test.example.com", false), (url.Value, url.IsConfigured));

        // Released, both are an admin's to keep or remove.
        await Registry().DescribeAsync("test", "retired", "user:ops", Later);
        Assert.True(await Config().RemoveAsync(null, "test", "OSDU_URL"));
        Assert.True(await Registry().RemoveAsync("test"));
    }

    [Fact]
    public async Task A_configuration_that_names_no_partition_leaves_a_registry_kept_by_hand_as_it_is()
    {
        await Registry().AddAsync("dev", null, makeDefault: false, "user:ops", Now);

        var applied = await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, [], Later);

        Assert.False(applied.Changed);
        var dev = Assert.Single(await Registry().ListAsync());
        Assert.Equal((true, false), (dev.IsDefault, dev.IsConfigured));
    }

    [Fact]
    public async Task A_value_written_behind_the_configuration_s_back_is_set_back()
    {
        await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), Now);
        await using (var db = _module.CreateDbContext())
        {
            await db.DeliveryConfigProperties.Where(p => p.Name == "OSDU_URL" && p.Partition == "dev")
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.Value, "https://tampered.example.com"));
        }

        var applied = await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), Later);

        Assert.Equal(1, applied.PropertiesWritten);
        Assert.Equal("https://osdu-dev.example.com", (await Config().EffectiveAsync(null, "dev"))["OSDU_URL"]);
    }

    [Fact]
    public async Task The_values_set_for_no_partition_are_written_locked_and_released_as_a_partition_s_are()
    {
        var estate = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OSDU_DATA_DB"] = "Server=tcp:sql.example.net;Database=OsduData;Authentication=Active Directory Default",
            ["LANDING_URL"] = "abfss://landing@estate.dfs.core.windows.net/drops/",
        };

        var applied = await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), estate, Now);

        Assert.Equal(6, applied.PropertiesWritten);
        var effective = await Config().EffectiveAsync(null, null);
        Assert.Equal(estate["OSDU_DATA_DB"], effective["OSDU_DATA_DB"]);
        Assert.Equal(estate["LANDING_URL"], effective["LANDING_URL"]);
        Assert.Equal(estate["OSDU_DATA_DB"], (await Config().EffectiveAsync(null, "dev"))["OSDU_DATA_DB"]);
        Assert.False((await ConfiguredPartitions.ApplyAsync(_module.CreateDbContext, DevAndTest(), estate, Later)).Changed);

        // Set by the configuration, they are refused everywhere else, naming the setting.
        var set = await Assert.ThrowsAsync<SetByConfigurationException>(
            () => Config().SetAsync(null, null, "OSDU_DATA_DB", "Server=elsewhere", null, "user:ops", Later, catalog: null));
        Assert.Equal("Osdu:Properties:OSDU_DATA_DB", set.Setting);
        var removed = await Assert.ThrowsAsync<SetByConfigurationException>(() => Config().RemoveAsync(null, null, "LANDING_URL"));
        Assert.Equal("Osdu:Properties:LANDING_URL", removed.Setting);

        // A value the configuration stops setting is kept, an admin's to keep or remove from then on.
        var released = await ConfiguredPartitions.ApplyAsync(
            _module.CreateDbContext, DevAndTest(), new Dictionary<string, string> { ["OSDU_DATA_DB"] = estate["OSDU_DATA_DB"] }, Later);
        Assert.Equal(1, released.PropertiesReleased);
        var landing = Assert.Single(await Config().ListAsync(null, null), p => p.Name == "LANDING_URL");
        Assert.False(landing.IsConfigured);
        Assert.True(await Config().RemoveAsync(null, null, "LANDING_URL"));
    }
}

/// <summary>
/// What <c>Osdu:Partitions</c> may hold, checked when the control plane starts: one default, data-partition-ids, property
/// names a flow can spell, and secrets only as references. Each refusal names the setting at fault, and never a secret.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConfiguredPartitionsCheckTests
{
    private static ConfiguredPartition Partition(string name, bool isDefault = false, params (string Name, string Value)[] properties)
        => new(name, null, isDefault, properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));

    [Fact]
    public void One_partition_is_the_default_without_saying_so_and_values_are_trimmed()
    {
        var checkedOne = Assert.Single(ConfiguredPartitions.Check([Partition(" dev ", false, ("OSDU_URL", " https://osdu.example.com "))]));

        Assert.Equal("dev", checkedOne.Name);
        Assert.Equal("https://osdu.example.com", checkedOne.Properties["OSDU_URL"]);
    }

    [Fact]
    public void Several_partitions_need_exactly_one_default()
    {
        var none = Assert.Throws<InvalidOperationException>(() => ConfiguredPartitions.Check([Partition("dev"), Partition("test")]));
        Assert.Contains("Osdu:Partitions:<name>:Default", none.Message, StringComparison.Ordinal);

        var two = Assert.Throws<InvalidOperationException>(() => ConfiguredPartitions.Check([Partition("dev", true), Partition("test", true)]));
        Assert.Contains("'dev' and 'test'", two.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OSDU_CLIENT_SECRET")]
    [InlineData("SQL_PASSWORD")]
    [InlineData("GIT_TOKEN")]
    [InlineData("OSDU_API_KEY")]
    public void A_secret_is_taken_only_as_a_reference_and_never_repeated(string name)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfiguredPartitions.Check([Partition("dev", true, (name, "s3cr3t-value"))]));

        Assert.Contains($"Osdu:Partitions:dev:Properties:{name}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", ex.Message, StringComparison.Ordinal);
        Assert.Single(ConfiguredPartitions.Check([Partition("dev", true, (name, "${keyvault:vault/osdu-secret}"))]));
        Assert.Single(ConfiguredPartitions.Check([Partition("dev", true, (name, "${env:OSDU_DEV_SECRET}"))]));
    }

    [Theory]
    [InlineData("${keyvault:vault/a}x")]
    [InlineData("x${env:A}")]
    [InlineData("${env:}")]
    [InlineData("${env:A}${env:B}")]
    public void Only_one_whole_reference_is_a_reference(string value)
        => Assert.False(ConfiguredPartitions.IsReference(value));

    [Fact]
    public void The_token_url_is_no_secret()
    {
        Assert.False(ConfiguredPartitions.IsSecretName("OSDU_TOKEN_URL"));
        Assert.Single(ConfiguredPartitions.Check([Partition("dev", true, ("OSDU_TOKEN_URL", "https://login.example.com/token"))]));
    }

    [Theory]
    [InlineData("dev partition", "OSDU_URL", "https://osdu.example.com", "not a data-partition-id")]
    [InlineData("${env:PARTITION}", "OSDU_URL", "https://osdu.example.com", "reference")]
    [InlineData("dev", "OSDU-URL", "https://osdu.example.com", "Osdu:Partitions:dev:Properties:OSDU-URL does not name")]
    [InlineData("dev", "OSDU_URL", "  ", "Osdu:Partitions:dev:Properties:OSDU_URL is empty")]
    public void A_setting_that_is_not_a_partition_or_a_value_is_refused_by_its_name(string partition, string name, string value, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfiguredPartitions.Check([Partition(partition, true, (name, value))]));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OSDU-DATA-DB", "Server=tcp:sql;Database=OsduData", "Osdu:Properties:OSDU-DATA-DB does not name")]
    [InlineData("OSDU_DATA_DB", " ", "Osdu:Properties:OSDU_DATA_DB is empty")]
    [InlineData("OSDU_CLIENT_SECRET", "s3cr3t-value", "Osdu:Properties:OSDU_CLIENT_SECRET holds a value where a secret's reference belongs")]
    [InlineData("OSDU_DATA_DB", "Server=tcp:sql;Database=OsduData;User ID=etl;Password=s3cr3t-value", "Osdu:Properties:OSDU_DATA_DB is a connection string with its password")]
    public void A_value_set_for_no_partition_that_the_configuration_may_not_hold_is_refused_by_its_setting(string name, string value, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfiguredPartitions.CheckProperties(new Dictionary<string, string> { [name] = value }));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partition_s_connection_string_with_its_password_written_out_is_refused_too()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfiguredPartitions.Check([Partition("dev", true, ("SOURCE_DB", "Server=sql;Password=s3cr3t-value"))]));

        Assert.Contains("Osdu:Partitions:dev:Properties:SOURCE_DB is a connection string", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partition_or_a_value_named_twice_whatever_its_case_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ConfiguredPartitions.Check([Partition("dev", true), Partition("DEV")]));
        Assert.Throws<InvalidOperationException>(() => ConfiguredPartitions.Check([Partition("dev", true, ("OSDU_URL", "a"), ("osdu_url", "b"))]));
    }
}
