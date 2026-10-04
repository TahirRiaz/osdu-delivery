//! The OSDU Delivery documentation, indexed for the doc tools beside SQLFlow's reference corpus.
//!
//! The pages are the product's own (`osdu/docs`, and the design pages under `docs/` the code cites), compiled into the
//! binary. They are written for people and several are long, so each is indexed as its sections: a page per `##`
//! heading, and per `###` heading or per run of paragraphs where a section is still longer than a model should be
//! handed in one answer. A section's body is a slice of the page, so nothing is copied and nothing is rewritten: what
//! `get_doc` returns is the text of the file. The page before its first heading is the document's own entry, and its
//! `related_docs` are its sections, which is its table of contents.

use sqlflow_mcp::{DocMeta, DocPage};

/// The longest body one indexed page carries, in bytes. A section longer than this is split further.
const PAGE_LIMIT: usize = 20_000;

/// One documentation file: how its pages are identified and found, and its text.
struct Source {
    /// The id of the document's own page, and the prefix of its sections' ids. Unique, and prefixed `delivery-`
    /// so no id can collide with one of SQLFlow's.
    id: &'static str,
    title: &'static str,
    /// One of the corpus's types: concept, guide, flow-reference, cli-command.
    doc_type: &'static str,
    /// The file, relative to the repository root.
    path: &'static str,
    summary: &'static str,
    keywords: &'static [&'static str],
    /// True for a page whose sections are CLI verbs, named by their headings.
    verbs: bool,
    text: &'static str,
}

macro_rules! osdu_doc {
    ($file:literal) => {
        include_str!(concat!("../../../docs/", $file))
    };
}

macro_rules! design_doc {
    ($file:literal) => {
        include_str!(concat!("../../../../docs/", $file))
    };
}

const SOURCES: &[Source] = &[
    Source {
        id: "delivery-domain",
        title: "The delivery domain",
        doc_type: "concept",
        path: "osdu/docs/README.md",
        summary: "What OSDU Delivery is: the chain from source files to OSDU records, the flow kinds, and where each part lives.",
        keywords: &["osdu", "delivery", "overview", "domain", "flow", "repository"],
        verbs: false,
        text: osdu_doc!("README.md"),
    },
    Source {
        id: "delivery-architecture",
        title: "Architecture",
        doc_type: "concept",
        path: "osdu/docs/architecture.md",
        summary: "The shape of the product: SQLFlow underneath, the delivery module on top, the hosts that compose them, and the databases.",
        keywords: &["architecture", "module", "host", "control plane", "node", "database", "osdu schema"],
        verbs: false,
        text: osdu_doc!("architecture.md"),
    },
    Source {
        id: "delivery-documents",
        title: "Document reference",
        doc_type: "flow-reference",
        path: "osdu/docs/documents.md",
        summary: "Every document the module adds, key by key: delivery, retrieval, cache, assertion and dimension flows, mappings and dictionaries, sources with interfaces, and partitions.",
        keywords: &["flow", "yaml", "delivery", "retrieval", "cache", "assertion", "dimension", "mapping", "dictionary", "interface", "partition", "flowType"],
        verbs: false,
        text: osdu_doc!("documents.md"),
    },
    Source {
        id: "delivery-mappings",
        title: "Templates and mappings",
        doc_type: "flow-reference",
        path: "osdu/docs/mapping-templates.md",
        summary: "The mapping language (every $ word, modifier and setting), templates saved from OSDU schemas, the checks a mapping passes, fixtures, and the mapping builder.",
        keywords: &["mapping", "template", "schema", "variable", "lookup", "fixture", "preflight", "render", "coalesce", "builder"],
        verbs: false,
        text: osdu_doc!("mapping-templates.md"),
    },
    Source {
        id: "delivery-ledger",
        title: "The ledger",
        doc_type: "concept",
        path: "osdu/docs/ledger.md",
        summary: "The delivery ledger in the osdu schema: its tables, record states, attempts, submissions, leasing, retention, partitions, and how a record is reconstructed from it.",
        keywords: &["ledger", "record", "attempt", "submission", "activity", "status", "lease", "retention", "traceability", "osdu schema", "table"],
        verbs: false,
        text: osdu_doc!("ledger.md"),
    },
    Source {
        id: "delivery-protocols",
        title: "Delivery protocols",
        doc_type: "concept",
        path: "osdu/docs/protocols.md",
        summary: "How a record reaches OSDU by route: storage, file, dataset, manifest, workflow, composed, ddms and the others, with what each sends and what a removal does.",
        keywords: &["protocol", "route", "storage", "file", "dataset", "manifest", "workflow", "ddms", "wellbore ddms", "seismic", "delete", "purge"],
        verbs: false,
        text: osdu_doc!("protocols.md"),
    },
    Source {
        id: "delivery-operations",
        title: "Operations",
        doc_type: "guide",
        path: "osdu/docs/operations.md",
        summary: "Running OSDU Delivery: the GUI page by page, the API, the runbook for failures and held records, metrics, and day-to-day procedures.",
        keywords: &["operations", "runbook", "gui", "api", "held", "failed", "redeliver", "rerender", "bring up to date", "release", "verify", "drift", "metrics", "cache page", "records page"],
        verbs: false,
        text: osdu_doc!("operations.md"),
    },
    Source {
        id: "delivery-environment",
        title: "Environment variables and secret references",
        doc_type: "guide",
        path: "osdu/docs/environment-variables.md",
        summary: "Every environment variable and secret reference the product reads, once: what each is for and which tier resolves it.",
        keywords: &["environment", "variable", "secret", "reference", "env", "keyvault", "configuration", "SQLFLOW_OSDU_DB"],
        verbs: false,
        text: osdu_doc!("environment-variables.md"),
    },
    Source {
        id: "delivery-design",
        title: "The delivery design",
        doc_type: "concept",
        path: "osdu/docs/design.md",
        summary: "Why delivery works as it does: the delivery grain, ids, change detection, rendering, gates, work batches, fan-out and reading from OSDU.",
        keywords: &["design", "change detection", "hash", "tier", "delivery key", "work batch", "fan-out", "intake", "drain", "gate"],
        verbs: false,
        text: osdu_doc!("design.md"),
    },
    Source {
        id: "delivery-dimensions",
        title: "Dimension flows",
        doc_type: "concept",
        path: "osdu/docs/dimension-plan.md",
        summary: "Dimension flows: the distinct keys OSDU records hold at a path, their labels and cleaned values, the dimension's own table, builds, the change log and filters.",
        keywords: &["dimension", "key", "value", "label", "attribute", "build", "filter", "search", "table", "dim_"],
        verbs: false,
        text: osdu_doc!("dimension-plan.md"),
    },
    Source {
        id: "delivery-explorer",
        title: "The explorer",
        doc_type: "guide",
        path: "osdu/docs/explorer.md",
        summary: "The Explorer page: browsing what an OSDU partition holds by type, searching by id or text, a record under the place it sits, two versions compared, and the API it reads through.",
        keywords: &["explorer", "browse", "search", "kind", "type", "lucene", "near ids", "mentions", "compare versions", "partition"],
        verbs: false,
        text: osdu_doc!("explorer.md"),
    },
    Source {
        id: "delivery-interfaces",
        title: "Interfaces: one flow per source",
        doc_type: "concept",
        path: "docs/interfaces-design.md",
        summary: "A source with interfaces: one flow delivering every OSDU type a source feeds, the order interfaces run in, routes, waiting records and redelivery scopes.",
        keywords: &["interface", "source", "route", "wave", "waiting", "after", "redeliver", "scope"],
        verbs: false,
        text: design_doc!("interfaces-design.md"),
    },
    Source {
        id: "delivery-partitions",
        title: "Partitions",
        doc_type: "concept",
        path: "docs/partitions-design.md",
        summary: "OSDU data partitions in the product: the registry, flows that name their partitions, a ledger and a cache per partition, central configuration per partition, and the workbench's partition switcher.",
        keywords: &["partition", "data-partition-id", "registry", "default", "configuration", "cache", "ledger", "switcher"],
        verbs: false,
        text: design_doc!("partitions-design.md"),
    },
    Source {
        id: "delivery-assertions",
        title: "Assertion flows",
        doc_type: "concept",
        path: "docs/assertions-design.md",
        summary: "Assertion flows: tests against what OSDU holds, their subjects, conditions and severities, how a run is selected and recorded, and the report.",
        keywords: &["assertion", "test", "report", "junit", "severity", "tag", "failRunOn", "data quality"],
        verbs: false,
        text: design_doc!("assertions-design.md"),
    },
    Source {
        id: "delivery-lineage",
        title: "Lineage of OSDU flows",
        doc_type: "concept",
        path: "docs/lineage-design.md",
        summary: "How OSDU flows appear in SQLFlow's lineage: the tables they read, the OSDU kinds they write, mappings and cache types as nodes, and the order runs take.",
        keywords: &["lineage", "graph", "dataset", "mapping node", "cache type", "wave", "derivation"],
        verbs: false,
        text: design_doc!("lineage-design.md"),
    },
    Source {
        id: "delivery-cli",
        title: "The OSDU verbs and run options",
        doc_type: "cli-command",
        path: "osdu/docs/reference/cli/delivery.md",
        summary: "sqlflow check, preview, values, fixtures, cache, template, assertions and dimensions, and the operation, values and payload a run of an OSDU flow takes.",
        keywords: &["cli", "check", "preview", "values", "fixtures", "cache", "template", "assertions", "dimensions", "run", "operation", "payload", "recordKeys", "redeliver", "rerender", "force"],
        verbs: true,
        text: osdu_doc!("reference/cli/delivery.md"),
    },
    Source {
        id: "delivery-cli-validate",
        title: "sqlflow validate for OSDU documents",
        doc_type: "cli-command",
        path: "osdu/docs/reference/cli/validate.md",
        summary: "What sqlflow validate checks in a delivery, retrieval, cache, assertion or mapping document.",
        keywords: &["cli", "validate", "document", "mapping", "flow"],
        verbs: false,
        text: osdu_doc!("reference/cli/validate.md"),
    },
    Source {
        id: "delivery-cli-db",
        title: "sqlflow db and the osdu module database",
        doc_type: "cli-command",
        path: "osdu/docs/reference/cli/db.md",
        summary: "The osdu module database in sqlflow db migrate and db status: its schema version, its migrations, and what refuses to run.",
        keywords: &["cli", "db", "migrate", "status", "schema version", "migration", "osdu schema"],
        verbs: false,
        text: osdu_doc!("reference/cli/db.md"),
    },
    Source {
        id: "delivery-cli-worker",
        title: "sqlflow worker for an OSDU Delivery node",
        doc_type: "cli-command",
        path: "osdu/docs/reference/cli/worker.md",
        summary: "What an OSDU Delivery node needs beyond a SQLFlow node: the module database reference, the OSDU references, and the private networks it may reach.",
        keywords: &["cli", "worker", "node", "pool", "SQLFLOW_OSDU_DB"],
        verbs: false,
        text: osdu_doc!("reference/cli/worker.md"),
    },
    Source {
        id: "delivery-cli-control-plane",
        title: "Control-plane verbs for OSDU flows",
        doc_type: "cli-command",
        path: "osdu/docs/reference/cli/control-plane.md",
        summary: "Triggering and following OSDU runs from a terminal: trigger with an operation, values and a payload, the runs verbs, and schedules per operation.",
        keywords: &["cli", "trigger", "runs", "schedule", "operation", "follow"],
        verbs: false,
        text: osdu_doc!("reference/cli/control-plane.md"),
    },
    Source {
        id: "delivery-cli-auth",
        title: "sqlflow auth in OSDU Delivery",
        doc_type: "cli-command",
        path: "osdu/docs/reference/cli/auth.md",
        summary: "Which OSDU Delivery paths use the Azure credential sqlflow auth checks.",
        keywords: &["cli", "auth", "azure", "credential", "managed identity"],
        verbs: false,
        text: osdu_doc!("reference/cli/auth.md"),
    },
    Source {
        id: "delivery-control-plane",
        title: "The delivery control plane",
        doc_type: "concept",
        path: "osdu/docs/reference/concepts/control-plane.md",
        summary: "The delivery API surface, the module's background services, and its configuration.",
        keywords: &["control plane", "api", "endpoint", "background service", "rollout", "probe", "configuration"],
        verbs: false,
        text: osdu_doc!("reference/concepts/control-plane.md"),
    },
    Source {
        id: "delivery-identity",
        title: "Authentication and identity in OSDU Delivery",
        doc_type: "concept",
        path: "osdu/docs/reference/concepts/authentication-and-identity.md",
        summary: "Who may operate the delivery surface (read, operate, author, admin), and what the ledger records about them.",
        keywords: &["authentication", "identity", "scope", "operate", "author", "admin", "actor", "audit"],
        verbs: false,
        text: osdu_doc!("reference/concepts/authentication-and-identity.md"),
    },
    Source {
        id: "delivery-deployment",
        title: "Deploying OSDU Delivery",
        doc_type: "guide",
        path: "osdu/docs/reference/guides/deployment.md",
        summary: "The images, the tiers, and triggering from an external scheduler.",
        keywords: &["deployment", "image", "container", "docker", "kubernetes", "azure", "tier"],
        verbs: false,
        text: osdu_doc!("reference/guides/deployment.md"),
    },
    Source {
        id: "delivery-notifications",
        title: "Notifications for OSDU flows",
        doc_type: "guide",
        path: "osdu/docs/reference/guides/notifications.md",
        summary: "Failure notifications for delivery, retrieval, cache and assertion flows.",
        keywords: &["notification", "failure", "email", "digest", "alert"],
        verbs: false,
        text: osdu_doc!("reference/guides/notifications.md"),
    },
    Source {
        id: "delivery-mcp",
        title: "The OSDU Delivery MCP server",
        doc_type: "guide",
        path: "osdu/docs/reference/guides/mcp.md",
        summary: "This server: what it adds to SQLFlow's, its tools by question, how it is installed, signed in and deployed, and what it will not do.",
        keywords: &["mcp", "assistant", "tool", "install", "claude", "http", "token", "allowlist"],
        verbs: false,
        text: osdu_doc!("reference/guides/mcp.md"),
    },
];

/// The decisions on record, one page each: short enough to be read whole.
const DECISIONS: &[(&str, &str, &str, &str)] = &[
    ("delivery-decision-0001", "Decision 0001: the delivery grain", "osdu/docs/decisions/0001-delivery-grain.md", osdu_doc!("decisions/0001-delivery-grain.md")),
    ("delivery-decision-0002", "Decision 0002: client-supplied ids", "osdu/docs/decisions/0002-client-supplied-ids.md", osdu_doc!("decisions/0002-client-supplied-ids.md")),
    ("delivery-decision-0003", "Decision 0003: where rendering happens", "osdu/docs/decisions/0003-rendering-location.md", osdu_doc!("decisions/0003-rendering-location.md")),
    ("delivery-decision-0004", "Decision 0004: preserved keys", "osdu/docs/decisions/0004-preserved-keys.md", osdu_doc!("decisions/0004-preserved-keys.md")),
    ("delivery-decision-0005", "Decision 0005: ledger retention", "osdu/docs/decisions/0005-ledger-retention.md", osdu_doc!("decisions/0005-ledger-retention.md")),
    ("delivery-decision-0006", "Decision 0006: work batches", "osdu/docs/decisions/0006-work-batches.md", osdu_doc!("decisions/0006-work-batches.md")),
    ("delivery-decision-0007", "Decision 0007: manifest dataset ids", "osdu/docs/decisions/0007-manifest-dataset-ids.md", osdu_doc!("decisions/0007-manifest-dataset-ids.md")),
    ("delivery-decision-0008", "Decision 0008: retrieval lands raw records", "osdu/docs/decisions/0008-retrieval-lands-raw-records.md", osdu_doc!("decisions/0008-retrieval-lands-raw-records.md")),
    ("delivery-decision-0009", "Decision 0009: searched references", "osdu/docs/decisions/0009-searched-references.md", osdu_doc!("decisions/0009-searched-references.md")),
    ("delivery-decision-0010", "Decision 0010: assertion flows are read-only", "osdu/docs/decisions/0010-assertion-flows-read-only.md", osdu_doc!("decisions/0010-assertion-flows-read-only.md")),
    ("delivery-decision-0011", "Decision 0011: dimension flows", "osdu/docs/decisions/0011-dimension-flows.md", osdu_doc!("decisions/0011-dimension-flows.md")),
];

/// The key census of each flow kind and document the module adds, as the editor tooling reads them
/// (`osdu/docs/census`).
pub const CENSUS: &[&str] = &[
    osdu_doc!("census/keys.delivery.json"),
    osdu_doc!("census/keys.retrieval.json"),
    osdu_doc!("census/keys.cache.json"),
    osdu_doc!("census/keys.assertion.json"),
    osdu_doc!("census/keys.dimension.json"),
    osdu_doc!("census/keys.mapping.json"),
    osdu_doc!("census/keys.dictionary.json"),
];

/// One piece of a document: the headings it sits under, outermost first, and its text.
struct Piece {
    headings: Vec<&'static str>,
    part: Option<usize>,
    body: &'static str,
}

/// The byte offsets of every line of `text` that is a heading of `level` (`## ` for 2), outside fenced code, with
/// the heading's text.
fn headings(text: &'static str, level: usize) -> Vec<(usize, &'static str)> {
    let marker = format!("{} ", "#".repeat(level));
    let mut found = Vec::new();
    let mut fenced = false;
    let mut offset = 0;
    for line in text.split_inclusive('\n') {
        let trimmed = line.trim_end_matches(['\n', '\r']);
        if trimmed.trim_start().starts_with("```") {
            fenced = !fenced;
        } else if !fenced {
            if let Some(heading) = trimmed.strip_prefix(marker.as_str()) {
                found.push((offset, heading.trim()));
            }
        }
        offset += line.len();
    }
    found
}

/// Splits `text` into pieces no longer than the page limit where its structure allows: by the headings of `level`,
/// then of the levels below, and last by runs of paragraphs. The text before the first heading keeps the headings
/// it was reached through.
fn split(text: &'static str, level: usize, under: &[&'static str], out: &mut Vec<Piece>) {
    if text.trim().is_empty() {
        return;
    }
    if text.len() <= PAGE_LIMIT && level > 2 {
        out.push(Piece { headings: under.to_vec(), part: None, body: text });
        return;
    }
    if level > 4 {
        split_paragraphs(text, under, out);
        return;
    }

    let marks = headings(text, level);
    if marks.is_empty() {
        if text.len() <= PAGE_LIMIT {
            out.push(Piece { headings: under.to_vec(), part: None, body: text });
        } else {
            split(text, level + 1, under, out);
        }
        return;
    }

    let lead = &text[..marks[0].0];
    if !lead.trim().is_empty() {
        if lead.len() <= PAGE_LIMIT {
            out.push(Piece { headings: under.to_vec(), part: None, body: lead });
        } else {
            split_paragraphs(lead, under, out);
        }
    }
    for (index, (start, heading)) in marks.iter().enumerate() {
        let end = marks.get(index + 1).map(|next| next.0).unwrap_or(text.len());
        let mut below = under.to_vec();
        below.push(heading);
        split(&text[*start..end], level + 1, &below, out);
    }
}

/// Splits `text` into parts no longer than the page limit at blank lines outside fenced code. A single block longer
/// than the limit stays whole: cutting inside a table or a code block would return something that is not the text.
fn split_paragraphs(text: &'static str, under: &[&'static str], out: &mut Vec<Piece>) {
    let mut breaks = Vec::new();
    let mut fenced = false;
    let mut offset = 0;
    for line in text.split_inclusive('\n') {
        let trimmed = line.trim();
        if trimmed.starts_with("```") {
            fenced = !fenced;
        } else if !fenced && trimmed.is_empty() {
            breaks.push(offset + line.len());
        }
        offset += line.len();
    }
    breaks.push(text.len());

    let mut start = 0;
    let mut last = 0;
    let mut part = 1;
    for end in breaks {
        if end - start > PAGE_LIMIT && last > start {
            out.push(Piece { headings: under.to_vec(), part: Some(part), body: &text[start..last] });
            part += 1;
            start = last;
        }
        last = end;
    }
    if !text[start..].trim().is_empty() {
        out.push(Piece { headings: under.to_vec(), part: (part > 1).then_some(part), body: &text[start..] });
    }
}

/// A heading as part of an id: lowercase letters and digits, hyphens between them, at most 48 characters.
fn slug(heading: &str) -> String {
    let mut out = String::new();
    for c in heading.chars() {
        if c.is_ascii_alphanumeric() {
            out.push(c.to_ascii_lowercase());
        } else if !out.is_empty() && !out.ends_with('-') {
            out.push('-');
        }
    }
    let trimmed = out.trim_matches('-');
    let cut: String = trimmed.chars().take(48).collect();
    cut.trim_matches('-').to_string()
}

/// A heading as a person reads it: without the backticks and the section number the files write.
fn readable(heading: &str) -> String {
    let plain = heading.replace('`', "");
    let unnumbered = plain
        .split_once(". ")
        .filter(|(number, _)| !number.is_empty() && number.chars().all(|c| c.is_ascii_digit() || c == '.'))
        .map(|(_, rest)| rest)
        .unwrap_or(&plain);
    unnumbered.trim().to_string()
}

/// The first lines of a piece that say something, as its one-line summary: the first paragraph after its heading,
/// without markup, cut at a sentence where one ends in time.
fn summary_of(body: &str) -> String {
    let mut text = String::new();
    let mut fenced = false;
    for line in body.lines() {
        let trimmed = line.trim();
        if trimmed.starts_with("```") {
            fenced = !fenced;
            continue;
        }
        if fenced || trimmed.starts_with('#') || trimmed.starts_with('|') {
            continue;
        }
        if trimmed.is_empty() {
            if text.is_empty() {
                continue;
            }
            break;
        }
        if !text.is_empty() {
            text.push(' ');
        }
        text.push_str(trimmed);
        if text.len() > 400 {
            break;
        }
    }
    let plain: String = text.chars().filter(|c| !matches!(c, '`' | '*')).collect();
    if plain.chars().count() <= 240 {
        return plain;
    }
    let head: String = plain.chars().take(240).collect();
    match head.rfind(". ") {
        Some(end) if end > 80 => head[..=end].to_string(),
        _ => format!("{}...", head.trim_end()),
    }
}

/// The words of a heading worth matching a search on.
fn heading_keywords(heading: &str) -> Vec<String> {
    heading
        .split(|c: char| !c.is_alphanumeric() && c != '_' && c != '-')
        .map(|word| word.trim_matches('-').to_lowercase())
        .filter(|word| word.len() >= 3)
        .collect()
}

/// Every page of the delivery documentation: each document's own entry followed by its sections.
pub fn pages() -> Vec<DocPage> {
    let mut pages: Vec<DocPage> = Vec::new();
    let mut taken: Vec<String> = Vec::new();

    for source in SOURCES {
        let mut pieces = Vec::new();
        split(source.text, 2, &[], &mut pieces);

        let mut entries: Vec<DocPage> = Vec::new();
        for piece in pieces {
            let mut id = source.id.to_string();
            for heading in &piece.headings {
                let part = slug(heading);
                if !part.is_empty() {
                    id.push('-');
                    id.push_str(&part);
                }
            }
            if let Some(part) = piece.part.filter(|part| *part > 1) {
                id.push_str(&format!("-part-{part}"));
            }
            // Two headings of one document can read the same; the later one says which it is.
            let mut unique = id.clone();
            let mut again = 2;
            while taken.contains(&unique) {
                unique = format!("{id}-{again}");
                again += 1;
            }
            taken.push(unique.clone());

            let top = piece.headings.is_empty() && piece.part.is_none_or(|part| part == 1);
            let mut title = source.title.to_string();
            for heading in &piece.headings {
                title.push_str(": ");
                title.push_str(&readable(heading));
            }
            if let Some(part) = piece.part.filter(|part| *part > 1) {
                title.push_str(&format!(" (part {part})"));
            }

            let mut keywords: Vec<String> = source.keywords.iter().map(|k| k.to_string()).collect();
            for heading in &piece.headings {
                for word in heading_keywords(heading) {
                    if !keywords.contains(&word) {
                        keywords.push(word);
                    }
                }
            }
            let keywords: Vec<&str> = keywords.iter().map(String::as_str).collect();

            let anchor = piece.headings.last().map(|heading| format!("#{}", slug(heading))).unwrap_or_default();
            // A section that opens with a table or a listing says nothing in prose; its heading is its summary.
            let summary = if top { source.summary.to_string() } else { summary_of(piece.body) };
            let summary = if summary.trim().is_empty() { title.clone() } else { summary };
            let mut meta = DocMeta::page(
                &unique,
                &title,
                source.doc_type,
                &format!("{}{anchor}", source.path),
                &summary,
                &keywords,
            );
            // A verb's section answers get_doc_by_cli_command for that verb, on the page whose sections are verbs.
            if source.verbs && piece.part.is_none_or(|part| part == 1) {
                if let [heading] = piece.headings.as_slice() {
                    let verb = heading.trim_matches('`');
                    if !verb.is_empty() && verb.chars().all(|c| c.is_ascii_lowercase() || c == '-') {
                        meta = meta.for_cli_command(verb);
                    }
                }
            }
            entries.push(DocPage { meta, body: piece.body });
        }

        // The document's own entry lists its sections, and each section points back at it.
        let first = entries.first().map(|page| page.meta.id.clone());
        if let Some(first) = first {
            let others: Vec<String> = entries.iter().skip(1).map(|page| page.meta.id.clone()).collect();
            let others: Vec<&str> = others.iter().map(String::as_str).collect();
            for (index, page) in entries.iter_mut().enumerate() {
                let meta = std::mem::replace(&mut page.meta, DocMeta::page("", "", "", "", "", &[]));
                page.meta = if index == 0 { meta.related_to(&others) } else { meta.related_to(&[first.as_str()]) };
            }
        }
        pages.extend(entries);
    }

    for (id, title, path, text) in DECISIONS {
        pages.push(DocPage {
            meta: DocMeta::page(
                id,
                title,
                "concept",
                path,
                &summary_of(text),
                &["decision", "adr", "why", "design"],
            ),
            body: text,
        });
    }
    pages
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;
    use std::path::{Path, PathBuf};

    /// Documentation files that are deliberately NOT indexed, each with why. A file under `osdu/docs` that is in
    /// neither this list nor the index fails the test below, so a new page is a decision rather than an omission.
    const NOT_INDEXED: &[(&str, &str)] = &[
        ("osdu/docs/cache-lookups-plan.md", "a plan for work in progress, not a description of what the product does"),
        ("osdu/docs/validation-plan.md", "a plan for work in progress, not a description of what the product does; documents.md says what validation does"),
        ("osdu/docs/osdu-testing.md", "how the project tests against a live OSDU: internal procedure, not product reference"),
        ("osdu/docs/test-matrix.md", "the project's own test coverage matrix"),
        ("osdu/docs/reference/README.md", "the index of the reference folder; its pages are indexed themselves"),
        ("osdu/docs/census/README.md", "describes the census files for editor tooling; the census itself is registered"),
        ("osdu/docs/decisions/README.md", "the index of the decisions; each decision is indexed itself"),
        ("osdu/docs/walkthrough/1-welllog-schema.md", "a worked example against one estate's data, not reference"),
        ("osdu/docs/walkthrough/2-how-we-populate-welllog.md", "a worked example against one estate's data, not reference"),
    ];

    fn repo_root() -> PathBuf {
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..").canonicalize().expect("the repository root")
    }

    fn markdown_under(dir: &Path, found: &mut Vec<PathBuf>) {
        for entry in std::fs::read_dir(dir).expect("a readable docs folder") {
            let path = entry.expect("a directory entry").path();
            if path.is_dir() {
                markdown_under(&path, found);
            } else if path.extension().is_some_and(|ext| ext == "md") {
                found.push(path);
            }
        }
    }

    #[test]
    fn every_page_of_the_product_docs_is_indexed_or_left_out_on_purpose() {
        let root = repo_root();
        let mut files = Vec::new();
        markdown_under(&root.join("osdu/docs"), &mut files);

        let indexed: HashSet<&str> = SOURCES.iter().map(|s| s.path).chain(DECISIONS.iter().map(|d| d.2)).collect();
        let left_out: HashSet<&str> = NOT_INDEXED.iter().map(|(path, _)| *path).collect();

        let mut undecided = Vec::new();
        for file in &files {
            let relative = file.strip_prefix(&root).expect("under the root").to_string_lossy().replace('\\', "/");
            if !indexed.contains(relative.as_str()) && !left_out.contains(relative.as_str()) {
                undecided.push(relative);
            }
        }
        undecided.sort();
        assert!(
            undecided.is_empty(),
            "These documentation pages are neither indexed for the doc tools nor listed as left out on purpose, so \
             an assistant cannot find them and nobody decided that: {undecided:?}. Add each to SOURCES (or \
             DECISIONS) in src/docs.rs, or to NOT_INDEXED with the reason."
        );

        for path in indexed.iter().chain(left_out.iter()) {
            assert!(root.join(path).is_file(), "{path} is listed and is not a file of the repository");
        }
        assert!(indexed.is_disjoint(&left_out), "a page is both indexed and left out");
    }

    #[test]
    fn every_page_has_an_id_of_its_own_a_title_and_a_body_within_reach() {
        let pages = pages();
        assert!(pages.len() > 120, "expected the documents split into their sections, found {} pages", pages.len());

        let mut ids = HashSet::new();
        for page in &pages {
            assert!(ids.insert(page.meta.id.clone()), "the id {} is used twice", page.meta.id);
            assert!(page.meta.id.starts_with("delivery-"), "{} would collide with SQLFlow's ids", page.meta.id);
            assert!(!page.meta.id.contains(char::is_whitespace), "{}", page.meta.id);
            assert!(!page.meta.title.trim().is_empty(), "{} has no title", page.meta.id);
            assert!(!page.body.trim().is_empty(), "{} has no body", page.meta.id);
            assert!(!page.meta.summary.trim().is_empty(), "{} has no summary", page.meta.id);
        }

        // Nearly every page fits what a model should be handed at once; one that does not is a single block (a
        // table, a code listing) that would not be the text if it were cut.
        let over: Vec<&DocPage> = pages.iter().filter(|p| p.body.len() > PAGE_LIMIT).collect();
        assert!(over.len() <= 6, "{} pages are over the limit: {:?}", over.len(), over.iter().map(|p| &p.meta.id).collect::<Vec<_>>());
        for page in over {
            assert!(page.body.len() <= PAGE_LIMIT * 3, "{} is {} bytes", page.meta.id, page.body.len());
        }
    }

    #[test]
    fn splitting_loses_nothing_and_repeats_nothing() {
        // Every document's pieces, joined, are the document: the index is the text, in order.
        for source in SOURCES {
            let mut pieces = Vec::new();
            split(source.text, 2, &[], &mut pieces);
            let joined: String = pieces.iter().map(|piece| piece.body).collect();
            assert_eq!(joined.len(), source.text.len(), "{} lost or repeated text", source.path);
            assert!(joined == source.text, "{} did not survive the split in order", source.path);
        }
    }

    #[test]
    fn a_documents_entry_lists_its_sections_and_each_points_back() {
        let pages = pages();
        let entry = pages.iter().find(|p| p.meta.id == "delivery-ledger").expect("the ledger's entry");
        assert_eq!(entry.meta.title, "The ledger");
        assert!(entry.meta.related.len() >= 8, "{:?}", entry.meta.related);
        let section = pages.iter().find(|p| p.meta.id == entry.meta.related[0]).expect("a listed section");
        assert_eq!(section.meta.related, ["delivery-ledger"]);
        assert!(section.meta.title.starts_with("The ledger: "), "{}", section.meta.title);
        assert!(section.body.starts_with("## "), "a section starts at its heading");
        assert!(section.meta.path.starts_with("osdu/docs/ledger.md#"), "{}", section.meta.path);
    }

    #[test]
    fn a_verbs_section_answers_for_its_command() {
        let pages = pages();
        for verb in ["check", "preview", "values", "fixtures", "cache", "template", "assertions", "dimensions"] {
            let page = pages
                .iter()
                .find(|p| p.meta.cli_command.as_deref() == Some(verb))
                .unwrap_or_else(|| panic!("no page answers for the verb {verb}"));
            assert!(page.meta.id.starts_with("delivery-cli-"), "{}", page.meta.id);
            assert_eq!(page.meta.doc_type, "cli-command");
        }
        // A heading that is not a verb does not claim a command.
        assert!(pages.iter().all(|p| p.meta.cli_command.as_deref() != Some("The run options")));
    }

    #[test]
    fn headings_inside_code_are_not_sections() {
        let text: &'static str = "intro\n\n## One\n\n```bash\n## not a heading\n```\n\n## Two\n\nbody\n";
        let marks = headings(text, 2);
        assert_eq!(marks.iter().map(|(_, h)| *h).collect::<Vec<_>>(), ["One", "Two"]);
    }

    #[test]
    fn a_slug_and_a_title_are_readable() {
        assert_eq!(slug("6. Change detection"), "6-change-detection");
        assert_eq!(slug("`ddms`"), "ddms");
        assert_eq!(slug("  A source with interfaces  "), "a-source-with-interfaces");
        assert_eq!(readable("6. Change detection"), "Change detection");
        assert_eq!(readable("`ddms`"), "ddms");
        assert_eq!(readable("The run options"), "The run options");
    }

    #[test]
    fn a_summary_is_the_first_thing_a_section_says() {
        let body = "## Leasing\n\nA node takes a batch by leasing it. The lease expires.\n\nMore follows.\n";
        assert_eq!(summary_of(body), "A node takes a batch by leasing it. The lease expires.");

        let long = format!("## X\n\n{}. {}\n", "a".repeat(120), "b".repeat(200));
        let summary = summary_of(&long);
        assert!(summary.ends_with('.') && summary.len() < 130, "{summary}");
    }

    #[test]
    fn every_census_names_the_kind_it_describes() {
        assert_eq!(CENSUS.len(), 7);
        let mut kinds = HashSet::new();
        for census in CENSUS {
            let parsed: serde_json::Value = serde_json::from_str(census).expect("a census file is json");
            let kind = parsed["flowType"].as_str().or(parsed["documentType"].as_str()).expect("a kind").to_string();
            assert!(kinds.insert(kind.clone()), "{kind} is described twice");
        }
        for kind in ["delivery", "retrieval", "cache", "assertion", "dimension", "mapping", "dictionary"] {
            assert!(kinds.contains(kind), "no census describes {kind}");
        }
    }
}
