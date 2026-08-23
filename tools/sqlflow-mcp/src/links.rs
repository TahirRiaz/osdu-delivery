//! GUI deep links attached to online tool results.
//!
//! Answers get read by people, and "arc.Citybike_Bikes is stale" is worth more when the table name
//! can be clicked through to its catalog page and its lineage graph. The control plane's JSON carries
//! identities (a global object key, a pipeline id, a run id) but no URLs, so this module maps those
//! identities onto the GUI's routes and decorates every read result with a `links` object per row.
//! The routes mirror the GUI router (`gui/src/App.tsx`), the lineage graph's `?focus=` deep link, and
//! the catalog tree's node-id contract (`gui/src/features/catalog/nodeIds.ts`).
//!
//! Rows are recognised by the identity fields they carry, not by which tool returned them, so a shape
//! that appears inside a dossier, a search hit, or a lineage walk is linked the same way everywhere,
//! and a tool added later is linked without being wired up.
//!
//! `SQLFLOW_GUI_URL` makes the links absolute, which is what a client rendering outside the GUI (the
//! Slack assistant, a desktop MCP client) needs. Unset, they stay root-relative: correct for the chat
//! assistant, which renders inside the GUI itself.

use serde_json::{Map, Value};

/// How deep the decorator walks before it stops. Every catalog payload nests far shallower than this;
/// the ceiling only exists so a pathological document cannot recurse without bound.
const MAX_DEPTH: usize = 16;

/// Percent-encodes one URL component, escaping everything outside RFC 3986's unreserved set. Object
/// keys and file paths carry `/`, `:`, spaces and (in this estate) Norwegian letters, all of which
/// have to survive the round trip into a query value.
fn encode(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for byte in value.as_bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => {
                out.push(*byte as char)
            }
            other => out.push_str(&format!("%{other:02X}")),
        }
    }
    out
}

/// The GUI's public base URL (empty for root-relative links) and the routes built from it.
#[derive(Clone, Debug, Default)]
pub struct GuiLinks {
    base: String,
}

impl GuiLinks {
    /// Reads `SQLFLOW_GUI_URL`; absent or blank leaves links root-relative.
    pub fn from_env() -> Self {
        Self::new(&std::env::var("SQLFLOW_GUI_URL").unwrap_or_default())
    }

    pub fn new(base: &str) -> Self {
        GuiLinks {
            base: base.trim().trim_end_matches('/').to_string(),
        }
    }

    /// The configured base, or "" when links are relative.
    pub fn base(&self) -> &str {
        &self.base
    }

    fn at(&self, path_and_query: &str) -> String {
        format!("{}{}", self.base, path_and_query)
    }

    /// A warehouse object's catalog page: its definition, columns, key, and consumers.
    ///
    /// The `node` value is the catalog tree's own id (`obj:<key>`), whose key segment is itself
    /// percent-encoded; encoding the composed id again is what makes the query value decode back to
    /// exactly that id, which the tree then decodes to the key.
    pub fn object(&self, key: &str) -> String {
        self.at(&format!("/catalog?node={}", encode(&format!("obj:{}", encode(key)))))
    }

    /// The lineage graph focused on one node. An object key needs no repo (the walk is cross-repo).
    pub fn object_lineage(&self, key: &str) -> String {
        self.at(&format!("/lineage?focus={}", encode(key)))
    }

    pub fn pipeline(&self, id: &str) -> String {
        self.at(&format!("/pipelines/{}", encode(id)))
    }

    /// The lineage graph focused on a flow. Its repo scopes the graph, which is what makes the node's
    /// Run action available once the user is there.
    pub fn pipeline_lineage(&self, repo_id: &str, id: &str) -> String {
        self.at(&format!("/lineage?repoId={}&focus={}", encode(repo_id), encode(id)))
    }

    pub fn run(&self, id: &str) -> String {
        self.at(&format!("/runs/{}", encode(id)))
    }

    pub fn run_group(&self, id: &str) -> String {
        self.at(&format!("/runs/groups/{}", encode(id)))
    }

    pub fn repo(&self, id: &str) -> String {
        self.at(&format!("/repos/{}", encode(id)))
    }

    pub fn subscriber(&self, key: &str) -> String {
        self.at(&format!("/subscribers?key={}", encode(key)))
    }

    /// A schedule has no page of its own; its runs board, filtered to it, is where an operator looks.
    pub fn schedule_runs(&self, id: &str) -> String {
        self.at(&format!("/runs?scheduleId={}", encode(id)))
    }

    /// Adds a `links` object to every row in a control-plane payload that carries an identity we can
    /// resolve to a GUI route. Rows without one are left exactly as they arrived.
    pub fn decorate(&self, value: &mut Value) {
        self.walk(value, 0);
    }

    fn walk(&self, value: &mut Value, depth: usize) {
        if depth >= MAX_DEPTH {
            return;
        }
        match value {
            Value::Array(items) => {
                for item in items.iter_mut() {
                    self.walk(item, depth + 1);
                }
            }
            Value::Object(map) => {
                for (_, nested) in map.iter_mut() {
                    self.walk(nested, depth + 1);
                }
                if let Some(links) = self.links_for(map) {
                    map.insert("links".to_string(), links);
                }
            }
            _ => {}
        }
    }

    /// The links for one row, from the identity fields it carries. Returns None when it carries none.
    fn links_for(&self, map: &Map<String, Value>) -> Option<Value> {
        // A row that already carries links (a re-decorated payload) keeps the ones it has.
        if map.contains_key("links") {
            return None;
        }

        let mut links = Map::new();

        // A lineage object: the global key plus the kind that says what it is. A node script carries
        // the same pair for a flow, where the key IS the pipeline id, so it links as the flow it is.
        match (text(map, "key"), text(map, "kind"), text(map, "type")) {
            (Some(key), Some(kind), _) => {
                if kind.eq_ignore_ascii_case("pipeline") || kind.eq_ignore_ascii_case("flow") {
                    links.insert("page".to_string(), self.pipeline(key).into());
                } else {
                    links.insert("page".to_string(), self.object(key).into());
                }
                links.insert("lineage".to_string(), self.object_lineage(key).into());
            }
            // A data subscriber (a report or dashboard): `type` is the tool that consumes the estate.
            // Its own `url` field points at the report itself and is left untouched.
            (Some(key), None, Some(_)) => {
                links.insert("page".to_string(), self.subscriber(key).into());
            }
            _ => {}
        }

        if let Some(id) = text(map, "id") {
            if let Some(repo_id) = text(map, "repoId") {
                if map.contains_key("name") && map.contains_key("kind") {
                    // A pipeline (flow) row.
                    links.insert("page".to_string(), self.pipeline(id).into());
                    links.insert("lineage".to_string(), self.pipeline_lineage(repo_id, id).into());
                } else if map.contains_key("timezone") && map.contains_key("enabled") {
                    // A schedule row.
                    links.insert("page".to_string(), self.schedule_runs(id).into());
                }
            } else if map.contains_key("lastSyncUtc") {
                // A repository row.
                links.insert("page".to_string(), self.repo(id).into());
            } else if map.contains_key("memberCount") {
                // One link of a schedule chain: the schedule it will fire.
                links.insert("page".to_string(), self.schedule_runs(id).into());
            }
        }

        // A run: `runId` is its identity in every shape that carries it, so it becomes the row's page
        // unless the row is already something else (a flow that reports its latest run, say).
        if let Some(run_id) = text(map, "runId") {
            let slot = if links.contains_key("page") { "run" } else { "page" };
            links.insert(slot.to_string(), self.run(run_id).into());
        }

        // References to other entities, on rows whose own identity is something else: a lineage step,
        // an edge, a column hit, a file receipt.
        if let Some(pipeline_id) = text(map, "pipelineId") {
            if !links.contains_key("lineage") {
                links.insert("flow".to_string(), self.pipeline(pipeline_id).into());
            }
        }
        if let Some(object_key) = text(map, "objectKey") {
            links.insert("object".to_string(), self.object(object_key).into());
            links.insert("objectLineage".to_string(), self.object_lineage(object_key).into());
        }
        if let Some(other_key) = text(map, "otherObjectKey") {
            links.insert("otherObject".to_string(), self.object(other_key).into());
        }
        if let Some(group_id) = text(map, "groupId") {
            links.insert("runGroup".to_string(), self.run_group(group_id).into());
        }

        if links.is_empty() {
            None
        } else {
            Some(Value::Object(links))
        }
    }
}

/// A non-empty string field, or None.
fn text<'a>(map: &'a Map<String, Value>, key: &str) -> Option<&'a str> {
    map.get(key)
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn links_of(value: &Value, pointer: &str) -> Value {
        value.pointer(pointer).cloned().unwrap_or(Value::Null)
    }

    #[test]
    fn object_rows_link_to_the_catalog_and_the_graph() {
        let links = GuiLinks::new("https://sqlflow.example.com/");
        let mut payload = json!([{ "key": "dw.arc.citybike_bikes", "kind": "Table", "serverRef": "dw" }]);
        links.decorate(&mut payload);

        assert_eq!(
            links_of(&payload, "/0/links/page"),
            json!("https://sqlflow.example.com/catalog?node=obj%3Adw.arc.citybike_bikes")
        );
        assert_eq!(
            links_of(&payload, "/0/links/lineage"),
            json!("https://sqlflow.example.com/lineage?focus=dw.arc.citybike_bikes")
        );
    }

    #[test]
    fn keys_needing_escapes_survive_both_encodings() {
        let links = GuiLinks::new("");
        // The catalog page decodes the query value once, then the node id's key segment once more.
        assert_eq!(links.object("dw.arc.Målt Verdi"), "/catalog?node=obj%3Adw.arc.M%25C3%25A5lt%2520Verdi");
        assert_eq!(links.object_lineage("dw.arc.Målt Verdi"), "/lineage?focus=dw.arc.M%C3%A5lt%20Verdi");
    }

    #[test]
    fn pipeline_rows_link_to_the_flow_and_its_graph() {
        let links = GuiLinks::new("");
        let mut payload = json!({
            "items": [{ "id": "p-1", "repoId": "r-1", "name": "citybike_00_api", "kind": "api" }],
            "page": 1, "pageSize": 50, "total": 1
        });
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/items/0/links/page"), json!("/pipelines/p-1"));
        assert_eq!(links_of(&payload, "/items/0/links/lineage"), json!("/lineage?repoId=r-1&focus=p-1"));
        // The page envelope itself carries no identity, so it is untouched.
        assert_eq!(links_of(&payload, "/links"), Value::Null);
    }

    #[test]
    fn a_run_row_links_to_the_run_its_flow_and_its_group() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "runId": "run-1", "pipelineId": "p-1", "repoId": "r-1", "groupId": "g-1",
            "flowName": "citybike_bikes_01_jsn", "status": "Succeeded"
        }]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/runs/run-1"));
        assert_eq!(links_of(&payload, "/0/links/flow"), json!("/pipelines/p-1"));
        assert_eq!(links_of(&payload, "/0/links/runGroup"), json!("/runs/groups/g-1"));
    }

    #[test]
    fn a_subscriber_keeps_its_own_url_and_gains_a_page() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "key": "powerbi:analyse_bysykkel", "name": "Analyse_Bysykkel_statistikk",
            "type": "PowerBI", "url": "https://app.powerbi.com/report"
        }]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/url"), json!("https://app.powerbi.com/report"));
        assert_eq!(
            links_of(&payload, "/0/links/page"),
            json!("/subscribers?key=powerbi%3Aanalyse_bysykkel")
        );
    }

    #[test]
    fn lineage_steps_link_every_hop() {
        let links = GuiLinks::new("");
        let mut payload = json!({
            "key": "dw.dwh.fact_trips", "kind": "Table",
            "upstream": [{ "depth": 1, "objectKey": "dw.arc.citybike_trips", "pipelineId": "p-2", "flowName": "f" }]
        });
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/links/page"), json!("/catalog?node=obj%3Adw.dwh.fact_trips"));
        assert_eq!(
            links_of(&payload, "/upstream/0/links/objectLineage"),
            json!("/lineage?focus=dw.arc.citybike_trips")
        );
        assert_eq!(links_of(&payload, "/upstream/0/links/flow"), json!("/pipelines/p-2"));
    }

    #[test]
    fn rows_without_an_identity_are_left_alone() {
        let links = GuiLinks::new("");
        let mut payload = json!({ "fileCount": 12, "avgBytes": 900, "recent": { "fileCount": 3 } });
        let untouched = payload.clone();
        links.decorate(&mut payload);
        assert_eq!(payload, untouched);
    }
}
