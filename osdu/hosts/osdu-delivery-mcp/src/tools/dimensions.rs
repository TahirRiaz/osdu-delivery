//! Dimension flows, as metadata: which dimensions exist, what each reads, how many values and keys it holds, and how
//! its builds went. The values and keys themselves are what OSDU records hold, which is data, so they are not read here.

use serde::Deserialize;
use serde_json::{json, Map};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};

use super::{DeliveryTools, PIPELINE_HINT};
use crate::support::{composed, optional_guid, refuse, section, Query};

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct DimensionsInput {
    /// Read ONE dimension with its recent builds: the `dimensionId` a board lists. Other arguments are ignored.
    #[serde(rename = "dimensionId")]
    pub dimension_id: Option<i64>,
    /// One dimension flow's board: its pipeline id, `id` in list_pipelines(kind="dimension"). Omit for every flow.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// The partition to read the board in. Omit for the one a run of the flow would build in.
    pub partition: Option<String>,
    /// With dimensionId: most builds, newest first (default 10, max 500).
    #[serde(rename = "maxBuilds")]
    pub max_builds: Option<i64>,
}

#[tool_router(router = dimensions_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "Dimensions: the distinct values OSDU records hold at a path (every operator, every curve type), collected by \
dimension flows. Returns their definitions and build health, never the values. The board lists each dimension with \
the OSDU kind, query and path it reads, how many values and keys it holds, when it was last built, and whether its \
last build failed or its definition changed since. With dimensionId, returns one dimension with its recent builds: \
records read, key completeness, what was added and removed, notes and error. Use it for 'which dimensions exist', \
'is the operator dimension up to date', 'why did the last build fail'. Build one by running its flow with \
trigger_run."
    )]
    async fn delivery_dimensions(&self, Parameters(i): Parameters<DimensionsInput>) -> String {
        if let Some(dimension) = i.dimension_id {
            if dimension <= 0 {
                return refuse("dimensionId is the positive number a board lists for a built dimension.");
            }
            let base = format!("/api/v1/delivery/dimensions/{dimension}");
            let detail = match self.ctx.read(&base, &[]).await {
                Ok(detail) => detail,
                Err(error) => return refuse(format!("{error:#}")),
            };
            let query = Query::new().value("max", Some(i.max_builds.unwrap_or(10).clamp(1, 500)));
            let builds = self.ctx.read(&format!("{base}/builds"), query.pairs()).await;
            let mut links = Map::new();
            links.insert(
                "page".to_string(),
                json!(self.ctx.links().route(&format!("/delivery/dimensions?d={dimension}"))),
            );
            return composed(
                vec![("dimension", detail), ("builds", section(builds))],
                links,
                "dimension.dimension is the dimension as its flow declares it and as its builds settled it; \
                 dimension.pipelineId is the flow to build it with (null when the catalog no longer holds the flow). \
                 builds are newest first.",
            );
        }

        let pipeline = match optional_guid("pipelineId", i.pipeline_id.as_deref(), PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let query = Query::new().text("partition", i.partition);
        let links = json!({ "page": self.ctx.links().route("/delivery/dimensions") });
        match pipeline {
            Some(pipeline) => {
                self.ctx
                    .get_about(
                        &format!("/api/v1/delivery/flows/{pipeline}/dimensions"),
                        query.pairs(),
                        ("pipelineId", &pipeline),
                        links,
                    )
                    .await
            }
            None => {
                self.ctx
                    .get_about("/api/v1/delivery/dimensions", query.pairs(), ("listing", "dimensions"), links)
                    .await
            }
        }
    }
}
