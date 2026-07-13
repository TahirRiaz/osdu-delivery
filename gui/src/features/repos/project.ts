/**
 * The project a pipeline belongs to: the ROOT folder inside the repo that holds a source's pipelines. A repo is a
 * git repository; a project is a top-level folder within it (e.g. "sales", "erp"); the flows in that folder are the
 * project's pipelines. Derived from the flow's repo-relative path. A flow at the repo root has no folder and
 * belongs to the "(root)" project.
 */
export function projectOf(relativePath: string): string {
  const slash = relativePath.indexOf("/");
  return slash > 0 ? relativePath.slice(0, slash) : "(root)";
}

/**
 * The folder path of a flow document split into its individual segments, outermost first: "flows/api/x.yaml"
 * yields ["flows", "api"], and a root-level document yields []. This is the per-level node chain a folder tree
 * nests a pipeline under, so a shared prefix like "flows" collapses into one parent node instead of repeating.
 */
export function folderSegmentsOf(relativePath: string): string[] {
  const slash = relativePath.lastIndexOf("/");
  return slash > 0 ? relativePath.slice(0, slash).split("/") : [];
}

/** The file-name part of a repo-relative flow document path (the whole path when it has no folder). */
export function fileNameOf(relativePath: string): string {
  return relativePath.slice(relativePath.lastIndexOf("/") + 1);
}
