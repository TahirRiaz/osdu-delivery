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
 * The folder holding a flow document inside its repo: the full directory part of the repo-relative path (nested
 * folders keep their whole path, e.g. "erp/orders"), or "(root)" for a document at the repo root. Where projectOf
 * answers "which source does this belong to", folderOf answers "where does this file live", so it is the grouping
 * key for the folder-tree views.
 */
export function folderOf(relativePath: string): string {
  const slash = relativePath.lastIndexOf("/");
  return slash > 0 ? relativePath.slice(0, slash) : "(root)";
}

/** The file-name part of a repo-relative flow document path (the whole path when it has no folder). */
export function fileNameOf(relativePath: string): string {
  return relativePath.slice(relativePath.lastIndexOf("/") + 1);
}
