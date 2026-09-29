using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>The formats a report is rendered in.</summary>
public enum ReportFormat
{
    Json,
    Markdown,
    Html,
    JUnit,
}

/// <summary>
/// The full report of one run of an assertion flow (docs/assertions-design.md section 8): the run, and every test's result as
/// the ledger keeps it, rendered as JSON for tools, Markdown for a pull request or a wiki, JUnit XML for a CI server, or a
/// self-contained HTML page to read, print or send. The API and the CLI render through this one class, so a report reads the
/// same wherever it is taken from.
/// </summary>
public sealed record AssertionReport(AssertionRunState Run, IReadOnlyList<TestResult> Results, DateTime GeneratedUtc)
{
    /// <summary>The report's format by the name the API and the CLI take (json, md, markdown, html, junit, xml).</summary>
    public static ReportFormat? FormatOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "json" => ReportFormat.Json,
        "md" or "markdown" => ReportFormat.Markdown,
        "html" => ReportFormat.Html,
        "junit" or "xml" => ReportFormat.JUnit,
        _ => null,
    };

    /// <summary>The media type and file extension of a format.</summary>
    public static (string MediaType, string Extension) Describe(ReportFormat format) => format switch
    {
        ReportFormat.Markdown => ("text/markdown; charset=utf-8", "md"),
        ReportFormat.Html => ("text/html; charset=utf-8", "html"),
        ReportFormat.JUnit => ("application/xml; charset=utf-8", "xml"),
        _ => ("application/json; charset=utf-8", "json"),
    };

    /// <summary>The file name a report downloads as: the flow, the partition and the run.</summary>
    public string FileName(ReportFormat format)
    {
        var safe = new string(Run.FlowName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return string.Create(CultureInfo.InvariantCulture, $"{safe}-{Run.Partition ?? "partition"}-report-{Run.AssertionRunId}.{Describe(format).Extension}");
    }

    public string Render(ReportFormat format) => format switch
    {
        ReportFormat.Markdown => ToMarkdown(),
        ReportFormat.Html => ToHtml(),
        ReportFormat.JUnit => ToJUnit(),
        _ => ToJson(),
    };

    /// <summary>The tests in the order a report lists them: by kind, then the ones that did not pass first, then by name.</summary>
    public IReadOnlyList<IGrouping<string, TestResult>> ByKind() => Results
        .OrderBy(r => Rank(r.Outcome))
        .ThenBy(r => r.Test, StringComparer.OrdinalIgnoreCase)
        .GroupBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Min(r => Rank(r.Outcome)))
        .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The share of the evaluated tests that passed, in percent; null when none was evaluated.</summary>
    public double? PassRate
    {
        get
        {
            var evaluated = Run.Counts.Tests - Run.Counts.Skipped;
            return evaluated <= 0 ? null : Math.Round(100.0 * Run.Counts.Passed / evaluated, 1);
        }
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["flow"] = Run.FlowName,
            ["partition"] = Run.Partition,
            ["assertionRunId"] = Run.AssertionRunId,
            ["runId"] = Run.RunId?.ToString("D"),
            ["actor"] = Run.Actor,
            ["status"] = Run.Status,
            ["startedUtc"] = Run.StartedUtc,
            ["completedUtc"] = Run.CompletedUtc,
            ["error"] = Run.Error,
            ["selection"] = Run.Selection is null ? null : JsonNode.Parse(Run.Selection),
            ["counts"] = new JsonObject
            {
                ["tests"] = Run.Counts.Tests,
                ["passed"] = Run.Counts.Passed,
                ["failed"] = Run.Counts.Failed,
                ["warned"] = Run.Counts.Warned,
                ["errored"] = Run.Counts.Errored,
                ["skipped"] = Run.Counts.Skipped,
            },
            ["passRate"] = PassRate,
            ["generatedUtc"] = GeneratedUtc,
            ["results"] = JsonSerializer.SerializeToNode(Results, TestResults.Json),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.Append("# ").Append(Run.FlowName).Append(" in ").Append(Run.Partition ?? "its partition").AppendLine();
        md.AppendLine();
        md.Append(CultureInfo.InvariantCulture, $"Report {Run.AssertionRunId}, **{Run.Status}**. Started {Stamp(Run.StartedUtc)}")
            .Append(Run.CompletedUtc is { } done ? $", completed {Stamp(done)}" : ", still running")
            .Append(CultureInfo.InvariantCulture, $", by {Cell(Run.Actor)}.").AppendLine();
        md.AppendLine();
        md.AppendLine("| Tests | Passed | Failed | Warned | Errored | Skipped | Pass rate |");
        md.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        md.Append(CultureInfo.InvariantCulture,
            $"| {Run.Counts.Tests} | {Run.Counts.Passed} | {Run.Counts.Failed} | {Run.Counts.Warned} | {Run.Counts.Errored} | {Run.Counts.Skipped} | {(PassRate is { } rate ? rate.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/a")} |")
            .AppendLine();
        if (Run.Error is { } error)
        {
            md.AppendLine().Append("> ").AppendLine(Cell(error));
        }

        foreach (var group in ByKind())
        {
            md.AppendLine().Append("## ").AppendLine(group.Key);
            foreach (var result in group)
            {
                md.AppendLine().Append("### ").Append(result.Test).Append(": ").AppendLine(result.Outcome.ToUpperInvariant());
                if (result.Description is { } description)
                {
                    md.AppendLine().AppendLine(description);
                }

                md.AppendLine();
                md.Append(CultureInfo.InvariantCulture, $"Matched {Number(result.Matched)}, read {Number(result.Evaluated)}")
                    .Append(result.Sampled ? " (a sample)" : string.Empty)
                    .Append(result.Template is { } template ? $", checked against template {template}" : string.Empty)
                    .Append(result.Query is { } query ? $", query `{query.Replace("`", "'", StringComparison.Ordinal)}`" : string.Empty)
                    .Append(CultureInfo.InvariantCulture, $", {result.DurationMs} ms.").AppendLine();
                if (result.Error is { } testError)
                {
                    md.AppendLine().Append("> ").AppendLine(Cell(testError));
                }

                foreach (var problem in result.Problems)
                {
                    md.Append("- ").AppendLine(Cell(problem));
                }

                foreach (var note in result.Notes)
                {
                    md.Append("- ").AppendLine(Cell(note));
                }

                if (result.Assertions.Count == 0)
                {
                    continue;
                }

                md.AppendLine();
                md.AppendLine("| Assertion | Severity | Outcome | Expected | Actual |");
                md.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var a in result.Assertions)
                {
                    md.Append(CultureInfo.InvariantCulture, $"| {Cell(a.Label)} | {a.Severity} | {a.Outcome} | {Cell(a.Expected)} | {Cell(a.Actual ?? a.Message ?? string.Empty)} |").AppendLine();
                }

                foreach (var a in result.Assertions.Where(a => a.Examples.Count > 0 && a.Outcome != TestOutcomes.Passed))
                {
                    md.AppendLine().Append("Failing examples of ").Append(Cell(a.Label)).AppendLine(":").AppendLine();
                    md.AppendLine("| Record | Value | Why |");
                    md.AppendLine("| --- | --- | --- |");
                    foreach (var example in a.Examples)
                    {
                        md.Append(CultureInfo.InvariantCulture, $"| {Cell(example.Id ?? string.Empty)} | {Cell(example.Value ?? string.Empty)} | {Cell(example.Reason)} |").AppendLine();
                    }
                }
            }
        }

        md.AppendLine().Append(CultureInfo.InvariantCulture, $"Generated {Stamp(GeneratedUtc)} by OSDU Delivery.").AppendLine();
        return md.ToString();
    }

    /// <summary>
    /// The report as JUnit XML, which CI servers read: a suite per kind, a case per test. A failed test is a failure, an
    /// errored one an error, a skipped one skipped; a warned test passes, with its warnings in its output.
    /// </summary>
    public string ToJUnit()
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var buffer = new StringWriterUtf8();
        using (var xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartElement("testsuites");
            xml.WriteAttributeString("name", $"{Run.FlowName}@{Run.Partition}");
            WriteCounts(xml, Results);
            xml.WriteAttributeString("time", Seconds(Results.Sum(r => r.DurationMs)));
            xml.WriteAttributeString("timestamp", Stamp(Run.StartedUtc));
            foreach (var group in ByKind())
            {
                xml.WriteStartElement("testsuite");
                xml.WriteAttributeString("name", group.Key);
                WriteCounts(xml, group.ToList());
                xml.WriteAttributeString("time", Seconds(group.Sum(r => r.DurationMs)));
                xml.WriteStartElement("properties");
                foreach (var (name, value) in new[]
                         {
                             ("flow", Run.FlowName), ("partition", Run.Partition ?? string.Empty),
                             ("report", Run.AssertionRunId.ToString(CultureInfo.InvariantCulture)), ("run", Run.RunId?.ToString("D") ?? string.Empty),
                         })
                {
                    xml.WriteStartElement("property");
                    xml.WriteAttributeString("name", name);
                    xml.WriteAttributeString("value", value);
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
                foreach (var result in group)
                {
                    xml.WriteStartElement("testcase");
                    xml.WriteAttributeString("classname", $"{Run.FlowName}.{group.Key}");
                    xml.WriteAttributeString("name", result.Test);
                    xml.WriteAttributeString("time", Seconds(result.DurationMs));
                    var detail = Detail(result);
                    switch (result.Outcome)
                    {
                        case TestOutcomes.Failed:
                            xml.WriteStartElement("failure");
                            xml.WriteAttributeString("message", First(result));
                            xml.WriteAttributeString("type", "assertion");
                            xml.WriteString(detail);
                            xml.WriteEndElement();
                            break;
                        case TestOutcomes.Errored:
                            xml.WriteStartElement("error");
                            xml.WriteAttributeString("message", result.Error ?? First(result));
                            xml.WriteAttributeString("type", "evaluation");
                            xml.WriteString(detail);
                            xml.WriteEndElement();
                            break;
                        case TestOutcomes.Skipped:
                            xml.WriteStartElement("skipped");
                            xml.WriteAttributeString("message", result.Error ?? "not run");
                            xml.WriteEndElement();
                            break;
                        default:
                            if (result.Outcome == TestOutcomes.Warned || result.Notes.Count > 0)
                            {
                                xml.WriteElementString("system-out", detail);
                            }

                            break;
                    }

                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        return buffer.ToString();
    }

    /// <summary>The report as one HTML page that needs nothing else: styles inline, light and dark, readable printed.</summary>
    public string ToHtml()
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(E(Run.FlowName)).Append(" test report</title>").AppendLine();
        html.AppendLine("<style>").AppendLine(Styles).AppendLine("</style></head><body><main>");
        var rate = PassRate;
        var degrees = rate is { } r ? (int)Math.Round(r * 3.6) : 0;
        html.Append(CultureInfo.InvariantCulture,
            $"<header><div><p class=\"eyebrow\">Test report {Run.AssertionRunId} &middot; OSDU Delivery</p><h1>{E(Run.FlowName)}</h1>");
        html.Append(CultureInfo.InvariantCulture,
            $"<p class=\"sub\">Partition <b>{E(Run.Partition ?? "?")}</b> &middot; started {E(Stamp(Run.StartedUtc))}{(Run.CompletedUtc is { } done ? " &middot; completed " + E(Stamp(done)) : " &middot; still running")} &middot; by {E(Run.Actor)}</p></div>");
        html.Append(CultureInfo.InvariantCulture,
            $"<div class=\"ring\" style=\"--pass:{degrees}deg\"><span>{(rate is { } shown ? shown.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/a")}</span><small>passed</small></div></header>");
        html.AppendLine("<section class=\"tiles\">");
        foreach (var (label, count, tone) in new[]
                 {
                     ("Tests", Run.Counts.Tests, "neutral"), ("Passed", Run.Counts.Passed, "passed"), ("Failed", Run.Counts.Failed, "failed"),
                     ("Warned", Run.Counts.Warned, "warned"), ("Errored", Run.Counts.Errored, "errored"), ("Skipped", Run.Counts.Skipped, "skipped"),
                 })
        {
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"tile {tone}\"><span>{count}</span><small>{label}</small></div>");
        }

        html.AppendLine("</section>");
        if (Run.Error is { } error)
        {
            html.Append("<p class=\"alert\">").Append(E(error)).AppendLine("</p>");
        }

        foreach (var group in ByKind())
        {
            html.Append("<section class=\"kind\"><h2>").Append(E(group.Key)).Append("</h2>");
            foreach (var result in group)
            {
                html.Append(CultureInfo.InvariantCulture, $"<article class=\"test {result.Outcome}\"><div class=\"head\"><h3>{E(result.Test)}</h3><span class=\"badge {result.Outcome}\">{result.Outcome}</span></div>");
                if (result.Description is { } description)
                {
                    html.Append("<p>").Append(E(description)).Append("</p>");
                }

                html.Append(CultureInfo.InvariantCulture,
                    $"<p class=\"facts\">matched <b>{Number(result.Matched)}</b> &middot; read <b>{Number(result.Evaluated)}</b>{(result.Sampled ? " (a sample)" : string.Empty)}{(result.Template is { } template ? " &middot; template <code>" + E(template) + "</code>" : string.Empty)} &middot; {result.DurationMs} ms</p>");
                if (result.Query is { } query)
                {
                    html.Append("<p class=\"facts\">query <code>").Append(E(query)).Append("</code></p>");
                }

                if (result.Error is { } testError)
                {
                    html.Append("<p class=\"alert\">").Append(E(testError)).Append("</p>");
                }

                foreach (var line in result.Problems.Concat(result.Notes))
                {
                    html.Append("<p class=\"note\">").Append(E(line)).Append("</p>");
                }

                if (result.Assertions.Count > 0)
                {
                    html.Append("<table><thead><tr><th>Assertion</th><th>Severity</th><th>Outcome</th><th>Expected</th><th>Actual</th></tr></thead><tbody>");
                    foreach (var a in result.Assertions)
                    {
                        html.Append(CultureInfo.InvariantCulture,
                            $"<tr><td>{E(a.Label)}</td><td>{a.Severity}</td><td><span class=\"badge {a.Outcome}\">{a.Outcome}</span></td><td>{E(a.Expected)}</td><td>{E(a.Actual ?? string.Empty)}{(a.Message is { } message && a.Outcome != TestOutcomes.Passed ? "<br><small>" + E(message) + "</small>" : string.Empty)}</td></tr>");
                        if (a.Examples.Count > 0 && a.Outcome != TestOutcomes.Passed)
                        {
                            html.Append(CultureInfo.InvariantCulture,
                                $"<tr class=\"examples\"><td colspan=\"5\"><details><summary>{a.Examples.Count} failing example(s){(a.ExamplesTrimmed ? ", cut back" : string.Empty)}</summary><table><tbody>");
                            foreach (var example in a.Examples)
                            {
                                html.Append("<tr><td><code>").Append(E(example.Id ?? string.Empty)).Append("</code></td><td>").Append(E(example.Value ?? string.Empty))
                                    .Append("</td><td>").Append(E(example.Reason)).Append("</td></tr>");
                            }

                            html.Append("</tbody></table></details></td></tr>");
                        }
                    }

                    html.Append("</tbody></table>");
                }

                html.AppendLine("</article>");
            }

            html.AppendLine("</section>");
        }

        html.Append("<footer>Generated ").Append(E(Stamp(GeneratedUtc))).AppendLine(" by OSDU Delivery, powered by SQLFlow.</footer>");
        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private const string Styles = """
        :root { --bg:#f7f8fa; --card:#ffffff; --ink:#1b1f24; --muted:#5b6470; --line:#e3e6ea; --passed:#1f8f55; --failed:#c62f3b; --warned:#b7791f; --errored:#7a3fc2; --skipped:#8a929c; --neutral:#2c5fb3; }
        @media (prefers-color-scheme: dark) { :root { --bg:#111418; --card:#191d22; --ink:#e7eaee; --muted:#9aa3ad; --line:#2a3037; } }
        * { box-sizing:border-box; }
        body { margin:0; background:var(--bg); color:var(--ink); font:14px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width:1100px; margin:0 auto; padding:32px 16px 48px; }
        header { display:flex; justify-content:space-between; gap:24px; align-items:center; }
        h1 { margin:4px 0; font-size:26px; } h2 { font-size:15px; margin:32px 0 12px; color:var(--muted); font-family:ui-monospace, monospace; }
        h3 { margin:0; font-size:15px; } .eyebrow { margin:0; color:var(--muted); text-transform:uppercase; letter-spacing:.06em; font-size:11px; }
        .sub, .facts { color:var(--muted); margin:4px 0; } code { font-family:ui-monospace, monospace; font-size:12px; overflow-wrap:anywhere; }
        .ring { width:120px; height:120px; border-radius:50%; background:conic-gradient(var(--passed) var(--pass), var(--line) 0); display:flex; flex-direction:column; align-items:center; justify-content:center; flex:none; position:relative; }
        .ring::after { content:""; position:absolute; inset:14px; border-radius:50%; background:var(--bg); }
        .ring span, .ring small { position:relative; z-index:1; } .ring span { font-size:22px; font-weight:700; line-height:1.1; } .ring small { color:var(--muted); }
        .tiles { display:grid; grid-template-columns:repeat(auto-fit, minmax(120px, 1fr)); gap:10px; margin:24px 0; }
        .tile { background:var(--card); border:1px solid var(--line); border-top:3px solid var(--neutral); border-radius:10px; padding:12px; }
        .tile span { display:block; font-size:24px; font-weight:700; } .tile small { color:var(--muted); }
        .tile.passed { border-top-color:var(--passed); } .tile.failed { border-top-color:var(--failed); } .tile.warned { border-top-color:var(--warned); }
        .tile.errored { border-top-color:var(--errored); } .tile.skipped { border-top-color:var(--skipped); }
        .test { background:var(--card); border:1px solid var(--line); border-left:4px solid var(--skipped); border-radius:10px; padding:14px 16px; margin:10px 0; break-inside:avoid; }
        .test.passed { border-left-color:var(--passed); } .test.failed { border-left-color:var(--failed); } .test.warned { border-left-color:var(--warned); } .test.errored { border-left-color:var(--errored); }
        .head { display:flex; justify-content:space-between; align-items:center; gap:12px; }
        .badge { font-size:11px; font-weight:600; padding:2px 8px; border-radius:999px; color:#fff; background:var(--skipped); text-transform:uppercase; letter-spacing:.04em; }
        .badge.passed { background:var(--passed); } .badge.failed { background:var(--failed); } .badge.warned { background:var(--warned); } .badge.errored { background:var(--errored); }
        table { width:100%; border-collapse:collapse; margin-top:10px; font-size:13px; } th, td { text-align:left; padding:6px 8px; border-top:1px solid var(--line); vertical-align:top; overflow-wrap:anywhere; }
        th { color:var(--muted); font-weight:600; font-size:12px; } .examples td { padding:0 8px 8px; border-top:none; } details summary { cursor:pointer; color:var(--muted); }
        .alert { border:1px solid var(--failed); color:var(--failed); border-radius:8px; padding:8px 12px; } .note { color:var(--muted); margin:4px 0; font-size:13px; }
        footer { margin-top:32px; color:var(--muted); font-size:12px; }
        @media print { body { background:#fff; } .test, .tile { break-inside:avoid; } details { display:block; } details > * { display:block; } }
        @media (max-width:640px) { header { flex-direction:column; align-items:flex-start; } }
        """;

    private static int Rank(string outcome) => outcome switch
    {
        TestOutcomes.Failed => 0,
        TestOutcomes.Errored => 1,
        TestOutcomes.Warned => 2,
        TestOutcomes.Passed => 3,
        _ => 4,
    };

    private static void WriteCounts(XmlWriter xml, IReadOnlyCollection<TestResult> results)
    {
        xml.WriteAttributeString("tests", results.Count.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("failures", results.Count(r => r.Outcome == TestOutcomes.Failed).ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("errors", results.Count(r => r.Outcome == TestOutcomes.Errored).ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("skipped", results.Count(r => r.Outcome == TestOutcomes.Skipped).ToString(CultureInfo.InvariantCulture));
    }

    private static string First(TestResult result)
    {
        var first = result.Assertions.FirstOrDefault(a => a.Outcome is TestOutcomes.Failed or TestOutcomes.Errored);
        return first is null ? result.Error ?? result.Outcome : $"{first.Label}: {first.Message ?? first.Actual}";
    }

    private static string Detail(TestResult result)
    {
        var text = new StringBuilder();
        foreach (var line in result.Problems.Concat(result.Notes))
        {
            text.AppendLine(line);
        }

        foreach (var a in result.Assertions.Where(a => a.Outcome != TestOutcomes.Passed))
        {
            text.Append(CultureInfo.InvariantCulture, $"[{a.Severity}] {a.Label}: {a.Outcome}; expected {a.Expected}; actual {a.Actual ?? "n/a"}").AppendLine();
            if (a.Message is { } message)
            {
                text.Append("  ").AppendLine(message);
            }

            foreach (var example in a.Examples.Take(10))
            {
                text.Append("  ").Append(example.Id ?? "-").Append(": ").Append(example.Value ?? string.Empty).Append(" (").Append(example.Reason).AppendLine(")");
            }
        }

        return text.ToString();
    }

    private static string Number(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Seconds(long milliseconds) => (milliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Text as a Markdown table cell holds it: pipes escaped, one line.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>A string writer that says UTF-8, so the XML declaration does.</summary>
    private sealed class StringWriterUtf8 : StringWriter
    {
        public StringWriterUtf8()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
