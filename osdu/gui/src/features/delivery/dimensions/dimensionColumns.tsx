import type { Column } from "@/components/DataTable";
import type { DeliveryDimensionAttributeSpec, DeliveryDimensionKey } from "../../../api/delivery";
import { KeyAttributeCell } from "./DimensionAttributeFilter";

/** How a grid names an attribute's column, so a reader can leave it out. */
export function attributeColumnId(name: string): string {
  return `attribute-${name}`;
}

/**
 * A column per attribute of a table of keys, in the order the dimension declares them, without the ones left out; the
 * value the dimension gives what it could not read is drawn faint.
 */
export function keyAttributeColumns(
  attributes: DeliveryDimensionAttributeSpec[], faint: string | null = null, hidden: ReadonlySet<string> = new Set(),
): Column<DeliveryDimensionKey>[] {
  return attributes
    .filter((attribute) => !hidden.has(attributeColumnId(attribute.name)))
    .map((attribute) => ({
      id: attributeColumnId(attribute.name),
      header: attribute.name,
      render: (row: DeliveryDimensionKey) => <KeyAttributeCell row={row} name={attribute.name} faint={faint} />,
    }));
}
