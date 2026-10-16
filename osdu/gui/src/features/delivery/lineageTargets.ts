import type { DatasetJumpTarget } from "@/components/LineageJumpButton";

/** The system a mapping's lineage node belongs to (`OsduLineage.MappingSystem`). */
const MAPPING_SYSTEM = "osdu-mapping";

/** The system a partition's cache types belong to (`OsduLineage.CacheSystem`). */
const CACHE_SYSTEM = "osdu-cache";

/** The group of a mapping whose mappings directory is the checkout's own root (`OsduLineage.RootGroup`). */
const ROOT_GROUP = ".";

/**
 * The namespace of a node declared by a flow that leaves its partitions to the registry (`PartitionNames.Every`): it
 * stands for every registered partition, so it is the node of whichever partition the workbench works in too.
 */
const EVERY_PARTITION = "*";

/** The namespaces a node of `partition` is declared under; none when the workbench works in no partition. */
function partitionNamespaces(partition: string | null): string[] | undefined {
  return partition === null ? undefined : [partition, EVERY_PARTITION];
}

/**
 * Every directory above a file of the checkout, nearest first, ending with the checkout's root. A mapping's node is
 * grouped under the mappings directory its flows name, and the document may sit in a folder below that directory.
 */
function directoriesAbove(relativePath: string): string[] {
  const segments = relativePath.replace(/\\/g, "/").split("/").filter((segment) => segment !== "").slice(0, -1);
  return [...segments.map((_, index) => segments.slice(0, segments.length - index).join("/")), ROOT_GROUP];
}

/**
 * A mapping document in the lineage graph: the node the delivery flows rendering with it read, as the partition the
 * workbench works in renders it. The same document rendered for another partition is another node, offered when
 * this partition has none.
 */
export function mappingLineageTarget(
  mapping: { reference: string; relativePath: string }, partition: string | null,
): DatasetJumpTarget {
  return {
    kind: "dataset",
    system: MAPPING_SYSTEM,
    name: mapping.reference,
    namespaces: partitionNamespaces(partition),
    groups: directoriesAbove(mapping.relativePath),
    label: mapping.reference,
    sublabel: mapping.relativePath,
  };
}

/** A type of a partition's cache in the lineage graph: the node its cache flows write and the mappings resolving against it read. */
export function cacheTypeLineageTarget(
  type: { name: string; entityType: string }, partition: string,
): DatasetJumpTarget {
  return {
    kind: "dataset",
    system: CACHE_SYSTEM,
    name: type.name,
    namespaces: partitionNamespaces(partition),
    label: type.name,
    sublabel: type.entityType,
  };
}
