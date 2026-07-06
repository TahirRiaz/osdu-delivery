//! The key census: SQLFlow's machine-readable `.flow.yaml` model.
//!
//! Nine `keys*.json` files under `docs/reference/flow/` describe every
//! attribute of every flow document kind. They are embedded here at compile
//! time (`include_str!`) so the analysis engine ships as a single binary with
//! no runtime file dependency. Each file is selected by the open document's
//! `flowType`; the shared file (`keys.shared.json`) is always merged in because
//! its blocks (`connections`, `servicePrincipals`, the invoke hooks) are reused
//! across kinds.
//!
//! A census `path` is a dot-path with three segment shapes:
//!   * a plain key            `source.options.header`
//!   * a list element `[]`    `transform.columns[].name`
//!   * a dotted literal key    `source.options["fileDate.from"]`
//!   * an open-dict wildcard   `connections.<name>.provider`
//!
//! These are parsed into [`Seg`] sequences so an authored YAML path can be
//! matched structurally rather than by fragile string equality.

use serde::Deserialize;
use std::collections::BTreeMap;

/// One census attribute as stored in a `keys*.json` file.
#[derive(Debug, Clone, Deserialize)]
pub struct KeyEntry {
    pub path: String,
    #[serde(rename = "type")]
    pub ty: String,
    #[serde(default)]
    pub required: bool,
    #[serde(default)]
    pub default: Option<String>,
    #[serde(default, rename = "enumValues")]
    pub enum_values: Option<Vec<String>>,
    #[serde(default)]
    pub description: String,
    #[serde(default, rename = "appliesWhen")]
    pub applies_when: Option<String>,
    #[serde(default, rename = "definedIn")]
    pub defined_in: Option<String>,
    #[serde(default)]
    pub validation: Option<String>,
    /// Parsed structural form of `path`, computed once at load time.
    #[serde(skip)]
    pub segs: Vec<Seg>,
}

/// A single segment of a census path.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Seg {
    /// A fixed key, e.g. `source` or the dotted literal `fileDate.from`.
    Key(String),
    /// A sequence element, written `[]` in the census.
    List,
    /// An arbitrary dictionary key, written `<name>` / `<param>` in the census.
    Wild,
}

/// A segment of a path reconstructed from an authored document.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuthoredSeg {
    Key(String),
    List,
}

/// Parse a census `path` string into structural segments.
///
/// Single-pass scanner: it never splits on `.` blindly, because a dotted
/// literal such as `["fileDate.from"]` legitimately contains a `.`.
pub fn parse_path(path: &str) -> Vec<Seg> {
    let mut segs = Vec::new();
    let mut ident = String::new();
    let mut chars = path.chars().peekable();

    // Flush the accumulated identifier as a Key (or Wild for `<...>`).
    fn flush(ident: &mut String, segs: &mut Vec<Seg>) {
        if ident.is_empty() {
            return;
        }
        if ident.starts_with('<') && ident.ends_with('>') {
            segs.push(Seg::Wild);
        } else {
            segs.push(Seg::Key(std::mem::take(ident)));
        }
        ident.clear();
    }

    while let Some(c) = chars.next() {
        match c {
            '.' => flush(&mut ident, &mut segs),
            '[' => {
                flush(&mut ident, &mut segs);
                match chars.peek() {
                    Some(']') => {
                        chars.next();
                        segs.push(Seg::List);
                    }
                    Some('"') | Some('\'') => {
                        let quote = chars.next().unwrap();
                        let mut lit = String::new();
                        for qc in chars.by_ref() {
                            if qc == quote {
                                break;
                            }
                            lit.push(qc);
                        }
                        // consume the trailing ']'
                        while let Some(&nc) = chars.peek() {
                            chars.next();
                            if nc == ']' {
                                break;
                            }
                        }
                        segs.push(Seg::Key(lit));
                    }
                    _ => {
                        // Unbracketed index form is not used by the census, but
                        // treat any `[...]` we do not recognise as a list.
                        for nc in chars.by_ref() {
                            if nc == ']' {
                                break;
                            }
                        }
                        segs.push(Seg::List);
                    }
                }
            }
            other => ident.push(other),
        }
    }
    flush(&mut ident, &mut segs);
    segs
}

impl Seg {
    fn matches(&self, authored: &AuthoredSeg) -> bool {
        match (self, authored) {
            (Seg::Key(k), AuthoredSeg::Key(a)) => k == a,
            (Seg::Wild, AuthoredSeg::Key(_)) => true,
            (Seg::List, AuthoredSeg::List) => true,
            _ => false,
        }
    }
}

/// A resolved key census for one document kind (the flowType file merged with
/// the shared file).
#[derive(Debug, Clone, Default)]
pub struct Census {
    pub entries: Vec<KeyEntry>,
}

/// Outcome of resolving an authored path against the census.
pub enum Resolution<'a> {
    /// The path names a documented attribute.
    Exact(&'a KeyEntry),
    /// The path is a valid container (a strict prefix of documented attributes)
    /// but has no leaf entry of its own.
    Container,
    /// The path is a member of an open dictionary (`<name>`), so any key is
    /// valid here.
    OpenDictMember,
    /// The path matches nothing in the census.
    Unknown,
}

impl Census {
    /// Deserialize a `keys*.json` body, parsing every path into segments.
    pub fn parse(json: &str) -> Result<Vec<KeyEntry>, serde_json::Error> {
        #[derive(Deserialize)]
        struct File {
            #[serde(default)]
            keys: Vec<KeyEntry>,
        }
        let mut file: File = serde_json::from_str(json)?;
        for e in &mut file.keys {
            e.segs = parse_path(&e.path);
        }
        Ok(file.keys)
    }

    /// Build the census for a given `flowType` (None/"" selects the file flow),
    /// merging in the shared cross-cutting blocks.
    pub fn for_flow_type(flow_type: Option<&str>) -> Census {
        let ft = flow_type.map(str::trim).filter(|s| !s.is_empty());
        let primary = match ft {
            None => FILE_FLOW,
            Some("ing") => ING,
            Some("exp") => EXP,
            Some("sp") => SP,
            Some("inv") => INV,
            Some("hc") => HC,
            Some("scm") => SCM,
            Some("batch") => BATCH,
            // Unknown flowType: fall back to the file flow so hover/completion
            // still work on the shared and root keys while diagnostics flag the
            // bad discriminator.
            Some(_) => FILE_FLOW,
        };
        let mut entries = Census::parse(primary).unwrap_or_default();
        // The `flowType` discriminator is documented once, centrally, in the
        // file-flow census and intentionally not repeated in the per-kind
        // files. Ensure it is always present so it is never mis-flagged as an
        // unknown key on a typed document.
        if ft.is_some() && !entries.iter().any(|e| e.path == "flowType") {
            if let Some(flow_type_entry) = Census::parse(FILE_FLOW)
                .unwrap_or_default()
                .into_iter()
                .find(|e| e.path == "flowType")
            {
                entries.push(flow_type_entry);
            }
        }
        // Merge shared blocks that the primary file does not already define.
        let shared = Census::parse(SHARED).unwrap_or_default();
        let have: std::collections::HashSet<&str> =
            entries.iter().map(|e| e.path.as_str()).collect::<std::collections::HashSet<_>>();
        let extra: Vec<KeyEntry> = shared
            .into_iter()
            .filter(|e| !have.contains(e.path.as_str()))
            .collect();
        entries.extend(extra);
        Census { entries }
    }

    /// Resolve an authored path to a census entry, container, or unknown.
    pub fn resolve(&self, authored: &[AuthoredSeg]) -> Resolution<'_> {
        // Exact match (wildcards and list markers included).
        if let Some(e) = self.entries.iter().find(|e| segs_match(&e.segs, authored)) {
            return Resolution::Exact(e);
        }
        // A strict prefix of any documented path: this is a valid container.
        if self
            .entries
            .iter()
            .any(|e| e.segs.len() > authored.len() && segs_match(&e.segs[..authored.len()], authored))
        {
            return Resolution::Container;
        }
        // Parent is an open dictionary: entry == parent ++ [Wild].
        if !authored.is_empty() {
            let parent = &authored[..authored.len() - 1];
            let wants_wild = self.entries.iter().any(|e| {
                e.segs.len() == parent.len() + 1
                    && segs_match(&e.segs[..parent.len()], parent)
                    && e.segs[parent.len()] == Seg::Wild
            });
            if wants_wild {
                return Resolution::OpenDictMember;
            }
        }
        Resolution::Unknown
    }

    /// Child keys available one segment below `parent`, for completion.
    ///
    /// Returns fixed keys only (wildcards and list elements cannot be offered as
    /// concrete completions). Each child is returned with the deepest entry that
    /// documents it so the item can carry detail/documentation.
    pub fn children_of(&self, parent: &[AuthoredSeg]) -> Vec<ChildKey<'_>> {
        let depth = parent.len();
        let mut seen: BTreeMap<String, &KeyEntry> = BTreeMap::new();
        for e in &self.entries {
            if e.segs.len() <= depth {
                continue;
            }
            if !segs_match(&e.segs[..depth], parent) {
                continue;
            }
            if let Seg::Key(k) = &e.segs[depth] {
                // Prefer the entry whose path ends exactly at this child (the
                // child's own documentation) over a deeper descendant.
                let is_leaf_here = e.segs.len() == depth + 1;
                seen.entry(k.clone())
                    .and_modify(|cur| {
                        if is_leaf_here && cur.segs.len() != depth + 1 {
                            *cur = e;
                        }
                    })
                    .or_insert(e);
            }
        }
        seen.into_iter()
            .map(|(name, entry)| ChildKey { name, entry })
            .collect()
    }

    /// Every documented attribute whose immediate parent is `parent` and which
    /// carries a `default`, for the insert-defaults code action.
    pub fn defaults_at(&self, parent: &[AuthoredSeg]) -> Vec<&KeyEntry> {
        let depth = parent.len();
        self.entries
            .iter()
            .filter(|e| {
                e.segs.len() == depth + 1
                    && matches!(e.segs.last(), Some(Seg::Key(_)))
                    && segs_match(&e.segs[..depth], parent)
                    && e.default.is_some()
            })
            .collect()
    }

    /// Required attributes at the top level (segment depth 1), for diagnostics.
    pub fn required_top_level(&self) -> Vec<&KeyEntry> {
        self.entries
            .iter()
            .filter(|e| e.required && e.segs.len() == 1 && matches!(e.segs.first(), Some(Seg::Key(_))))
            .collect()
    }
}

/// A completion candidate: a child key plus the entry documenting it.
pub struct ChildKey<'a> {
    pub name: String,
    pub entry: &'a KeyEntry,
}

fn segs_match(census: &[Seg], authored: &[AuthoredSeg]) -> bool {
    census.len() == authored.len() && census.iter().zip(authored).all(|(c, a)| c.matches(a))
}

// --- Embedded census files -------------------------------------------------
// Paths are relative to this source file: tools/sqlflow-lang/src/ -> repo root.
const FILE_FLOW: &str = include_str!("../../../docs/reference/flow/keys.json");
const ING: &str = include_str!("../../../docs/reference/flow/keys.ing.json");
const EXP: &str = include_str!("../../../docs/reference/flow/keys.exp.json");
const SP: &str = include_str!("../../../docs/reference/flow/keys.sp.json");
const INV: &str = include_str!("../../../docs/reference/flow/keys.inv.json");
const HC: &str = include_str!("../../../docs/reference/flow/keys.hc.json");
const SCM: &str = include_str!("../../../docs/reference/flow/keys.scm.json");
const BATCH: &str = include_str!("../../../docs/reference/flow/keys.batch.json");
const SHARED: &str = include_str!("../../../docs/reference/flow/keys.shared.json");

#[cfg(test)]
mod tests {
    use super::*;

    fn ak(parts: &[&str]) -> Vec<AuthoredSeg> {
        parts.iter().map(|p| AuthoredSeg::Key(p.to_string())).collect()
    }

    #[test]
    fn parses_plain_list_and_dict_paths() {
        assert_eq!(parse_path("source.options.header"),
            vec![Seg::Key("source".into()), Seg::Key("options".into()), Seg::Key("header".into())]);
        assert_eq!(parse_path("transform.columns[].name"),
            vec![Seg::Key("transform".into()), Seg::Key("columns".into()), Seg::List, Seg::Key("name".into())]);
        assert_eq!(parse_path("source.options[\"fileDate.from\"]"),
            vec![Seg::Key("source".into()), Seg::Key("options".into()), Seg::Key("fileDate.from".into())]);
        assert_eq!(parse_path("connections.<name>.provider"),
            vec![Seg::Key("connections".into()), Seg::Wild, Seg::Key("provider".into())]);
    }

    #[test]
    fn file_flow_census_loads_and_resolves() {
        let c = Census::for_flow_type(None);
        assert!(c.entries.len() > 100, "file flow census should be large");
        // A known attribute resolves exactly.
        assert!(matches!(c.resolve(&ak(&["source", "type"])), Resolution::Exact(_) | Resolution::Container));
        // A container resolves as a container or exact.
        assert!(!matches!(c.resolve(&ak(&["transform"])), Resolution::Unknown));
        // An open-dict member (arbitrary connection name) is accepted.
        assert!(matches!(
            c.resolve(&ak(&["connections", "myConn", "provider"])),
            Resolution::Exact(_)
        ));
        // Gibberish at the root is unknown.
        assert!(matches!(c.resolve(&ak(&["totallyBogusRootKey"])), Resolution::Unknown));
    }

    #[test]
    fn flow_type_key_is_known_in_every_census() {
        // The discriminator lives only in keys.json but must resolve for all kinds.
        for ft in [Some("ing"), Some("exp"), Some("batch"), Some("hc"), None] {
            let c = Census::for_flow_type(ft);
            assert!(
                matches!(c.resolve(&ak(&["flowType"])), Resolution::Exact(_)),
                "flowType should be known for flowType={ft:?}"
            );
        }
    }

    #[test]
    fn ingestion_census_has_list_attributes() {
        let c = Census::for_flow_type(Some("ing"));
        assert!(matches!(
            c.resolve(&[AuthoredSeg::Key("assertions".into()), AuthoredSeg::List, AuthoredSeg::Key("name".into())]),
            Resolution::Exact(_)
        ));
    }
}
