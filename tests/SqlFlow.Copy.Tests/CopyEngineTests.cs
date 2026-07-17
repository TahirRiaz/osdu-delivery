using System.IO.Compression;
using SqlFlow.Copy;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Copy.Tests;

/// <summary>
/// The copy engine over the local endpoint (deterministic, no cloud): verbatim copy with and without structure,
/// pattern and modified-within filtering, and a zip -> unzip round trip, plus the cpy YAML loader's mapping and
/// validation. The Azure and SFTP endpoints share the same engine seam; only the endpoint I/O differs.
/// </summary>
public sealed class CopyEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow-cpy-" + Guid.NewGuid().ToString("N")[..8]);

    public CopyEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CopyEngine Engine() => new([new LocalCopyEndpoint()], TimeProvider.System);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private CopyFlow Flow(CopyOperation op, string src, string trg, string pattern = "*", bool preserve = true, string? zipName = null)
        => new()
        {
            Name = "T",
            Operation = op,
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = Path.Combine(_dir, src), Pattern = pattern },
                Target = new CopyEndpoint { Location = Path.Combine(_dir, trg) },
            }],
            Options = new CopyOptions { PreserveStructure = preserve, ZipName = zipName },
        };

    private CopyStep Step(string src, string trg, string pattern = "*")
        => new()
        {
            Source = new CopyEndpoint { Location = Path.Combine(_dir, src), Pattern = pattern },
            Target = new CopyEndpoint { Location = Path.Combine(_dir, trg) },
        };

    [Fact]
    public async Task Copy_PreservesFolderStructure()
    {
        Write("src/detail.json", "{\"o\":1}");
        Write("src/sub/sess.json", "{\"o\":2}");

        var result = await Engine().RunAsync(Flow(CopyOperation.Copy, "src", "dst"), Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.Equal(2, result.Matched);
        Assert.Equal(2, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "detail.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "sub", "sess.json")));
    }

    [Fact]
    public async Task Copy_Flatten_DropsStructure()
    {
        Write("src/a/one.txt", "1");
        Write("src/b/two.txt", "2");

        var result = await Engine().RunAsync(Flow(CopyOperation.Copy, "src", "flat", preserve: false), Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_dir, "flat", "one.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "flat", "two.txt")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "flat", "a")));
    }

    [Fact]
    public async Task Copy_Pattern_FiltersByGlob()
    {
        Write("src/keep.json", "{}");
        Write("src/skip.csv", "x");

        var result = await Engine().RunAsync(Flow(CopyOperation.Copy, "src", "dst", pattern: "*.json"), Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "keep.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "dst", "skip.csv")));
    }

    [Fact]
    public async Task Zip_ThenUnzip_RoundTrips()
    {
        Write("src/detail.json", "{\"o\":1}");
        Write("src/sub/sess.json", "{\"o\":2}");

        var zip = await Engine().RunAsync(Flow(CopyOperation.Zip, "src", "zipout", zipName: "bundle.zip"), Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.True(zip.Success);
        var archivePath = Path.Combine(_dir, "zipout", "bundle.zip");
        Assert.True(File.Exists(archivePath));
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.Equal(2, archive.Entries.Count);
            Assert.Contains(archive.Entries, e => e.FullName is "detail.json" or "sub/sess.json");
        }

        var unzip = await Engine().RunAsync(Flow(CopyOperation.Unzip, "zipout", "extracted", pattern: "*.zip"), Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.True(unzip.Success);
        // Preserve-structure unzip nests each archive under a folder named for it.
        Assert.True(File.Exists(Path.Combine(_dir, "extracted", "bundle", "detail.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "extracted", "bundle", "sub", "sess.json")));
    }

    [Fact]
    public async Task Cancelled_Propagates_RatherThanReportingFailure()
    {
        Write("src/detail.json", "{\"o\":1}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Cancelled is its own terminal state, so the engine must NOT swallow the cancellation into a
        // Success = false result: that artifact would record an operator's cancel as a failed run. The callers
        // (RunWorker, the CLI) distinguish an operator cancel from a shutdown, so the cancellation reaches them.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine().RunAsync(Flow(CopyOperation.Copy, "src", "dst"), Guid.NewGuid(), NullRunEventSink.Instance, cts.Token));
    }

    [Fact]
    public async Task UnknownEndpoint_FailsCleanly()
    {
        var flow = new CopyFlow
        {
            Name = "T",
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = Path.Combine(_dir, "src") },
                Target = new CopyEndpoint { Location = "abfss://fs@acct.dfs.core.windows.net/x" },
            }],
        };
        Write("src/a.txt", "x");
        // A local engine with no Azure endpoint cannot handle an abfss target.
        var engine = new CopyEngine([new LocalCopyEndpoint()], TimeProvider.System);
        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.False(result.Success);
        Assert.Contains("No copy endpoint handles the target", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_MapsAndValidates()
    {
        var flow = new YamlCopyFlowLoader().Parse("""
            flowType: cpy
            name: BB_Baatbooking_00_cpy
            batch: BB
            operation: zip
            source:
              location: abfss://baatbooking@acct.dfs.core.windows.net
              pattern: "*.json"
              modifiedWithinDays: 14
            target:
              location: ./out
            options:
              zipName: bundle.zip
            """);

        Assert.Equal("BB_Baatbooking_00_cpy", flow.Name);
        Assert.Equal(CopyOperation.Zip, flow.Operation);
        var step = Assert.Single(flow.Steps);
        Assert.Equal("*.json", step.Source.Pattern);
        Assert.Equal(14, step.Source.ModifiedWithinDays);
        Assert.Equal("bundle.zip", flow.Options.ZipName);
    }

    [Fact]
    public void Loader_MissingSource_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlCopyFlowLoader().Parse("flowType: cpy\nname: x\ntarget: { location: ./out }\n"));
        Assert.Contains("'source' is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_ItemsList_MapsEachStep()
    {
        var flow = new YamlCopyFlowLoader().Parse("""
            flowType: cpy
            name: BB_Baatbooking_00_cpy
            batch: BB
            items:
              - source: { location: abfss://baatbooking@acct.dfs.core.windows.net/DETAIL, pattern: "*.json", modifiedWithinDays: 14 }
                target: { location: abfss://datalakev2@acct.dfs.core.windows.net/raw/baatbooking/history/detail }
              - source: { location: abfss://baatbooking@acct.dfs.core.windows.net/SESS }
                target: { location: abfss://datalakev2@acct.dfs.core.windows.net/raw/baatbooking/history/sess }
            """);

        Assert.Equal(2, flow.Steps.Count);
        Assert.EndsWith("/DETAIL", flow.Steps[0].Source.Location, StringComparison.Ordinal);
        Assert.EndsWith("/detail", flow.Steps[0].Target.Location, StringComparison.Ordinal);
        Assert.Equal("*.json", flow.Steps[0].Source.Pattern);
        Assert.EndsWith("/sess", flow.Steps[1].Target.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_ItemsAndSingleSource_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlCopyFlowLoader().Parse("""
            flowType: cpy
            name: x
            source: { location: ./a }
            target: { location: ./b }
            items:
              - source: { location: ./c }
                target: { location: ./d }
            """));
        Assert.Contains("not both", ex.Message, StringComparison.Ordinal);
    }

    private CopyFlow FlowWithWindow(string src, string trg, int modifiedWithinDays)
        => new()
        {
            Name = "T",
            Operation = CopyOperation.Copy,
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = Path.Combine(_dir, src), ModifiedWithinDays = modifiedWithinDays },
                Target = new CopyEndpoint { Location = Path.Combine(_dir, trg) },
            }],
        };

    [Fact]
    public async Task Copy_ModifiedWithinDays_ExcludesOlderFilesByDefault()
    {
        // The flow's declared window (the baatbooking runbook's 14 days) is the default when no override is supplied.
        var oldFile = Write("src/old.json", "{}");
        var newFile = Write("src/new.json", "{}");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-1));

        var result = await Engine().RunAsync(FlowWithWindow("src", "dst", 14), Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "new.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "dst", "old.json")));
    }

    [Fact]
    public async Task Copy_BackfillWindow_OverridesModifiedWithinDays_ToReachHistory()
    {
        // A trigger-time backfill window replaces the 14-day default, reaching files it would otherwise exclude.
        var oldFile = Write("src/old.json", "{}");
        var newFile = Write("src/new.json", "{}");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-1));

        var parameters = new RunParameters
        {
            BackfillFrom = DateTime.UtcNow.AddDays(-40),
            BackfillTo = DateTime.UtcNow.AddDays(-20),
        };
        var result = await Engine().RunAsync(
            FlowWithWindow("src", "dst", 14), Guid.NewGuid(), NullRunEventSink.Instance, default, parameters);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "old.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "dst", "new.json")));
    }

    [Fact]
    public async Task Copy_FullLoad_IgnoresModifiedWithinDays()
    {
        var oldFile = Write("src/old.json", "{}");
        var newFile = Write("src/new.json", "{}");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-100));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-1));

        var result = await Engine().RunAsync(
            FlowWithWindow("src", "dst", 14), Guid.NewGuid(), NullRunEventSink.Instance, default,
            new RunParameters { FullLoad = true });

        Assert.True(result.Success);
        Assert.Equal(2, result.FilesWritten);
    }

    [Fact]
    public async Task Copy_MultipleSteps_CopyEachSourceToItsTarget()
    {
        // One pipeline, several copies: each step's files land under its own target folder, counts aggregate.
        Write("detailsrc/d1.json", "{}");
        Write("detailsrc/2024/d2.json", "{}");
        Write("sesssrc/s1.json", "{}");

        var flow = new CopyFlow
        {
            Name = "BB",
            Steps = [Step("detailsrc", "lake/detail"), Step("sesssrc", "lake/sess")],
        };
        var result = await Engine().RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.Equal(3, result.Matched);
        Assert.Equal(3, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "lake", "detail", "d1.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "lake", "detail", "2024", "d2.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "lake", "sess", "s1.json")));
    }
}
