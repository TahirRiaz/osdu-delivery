import type { PipelineSummary } from "../../api/types";
import { projectOf } from "../repos/project";

/** Whether a pipeline matches a free-text search over the fields a user eyeballs to find a flow: its name, path,
 * kind, and the project (folder) it groups under. `needle` must already be lower-cased and trimmed. */
export function pipelineMatches(p: PipelineSummary, needle: string): boolean {
  return p.name.toLowerCase().includes(needle)
    || p.relativePath.toLowerCase().includes(needle)
    || p.kind.toLowerCase().includes(needle)
    || projectOf(p.relativePath).toLowerCase().includes(needle);
}

/** Group pipelines by their project (repo-root folder), each group's rows sorted by name and the groups sorted by
 * project name. */
export function groupByProject(pipelines: PipelineSummary[]): [string, PipelineSummary[]][] {
  const byProject = new Map<string, PipelineSummary[]>();
  for (const p of [...pipelines].sort((a, b) => a.name.localeCompare(b.name))) {
    const key = projectOf(p.relativePath);
    const group = byProject.get(key);
    if (group) {
      group.push(p);
    } else {
      byProject.set(key, [p]);
    }
  }
  return [...byProject.entries()].sort((a, b) => a[0].localeCompare(b[0]));
}
