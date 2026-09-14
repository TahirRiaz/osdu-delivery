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

/** Whether a repo file is a YAML document, the one kind of file the repo view previews: flows, mappings, and
 * schedule libraries are all written in it. */
export function isYamlPath(relativePath: string): boolean {
  return /\.ya?ml$/i.test(relativePath);
}

/** Whether a repo file is a shared schedule library: named schedules.yaml, or ending in .schedules.yaml. The same
 * rule the sync applies when it reads a repository. */
export function isScheduleLibraryPath(relativePath: string): boolean {
  const name = relativePath.slice(relativePath.lastIndexOf("/") + 1).toLowerCase();
  return name === "schedules.yaml" || name.endsWith(".schedules.yaml");
}
