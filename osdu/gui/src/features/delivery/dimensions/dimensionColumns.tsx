import type { Column } from "@/components/DataTable";
import type { DeliveryDimensionAttributeSpec, DeliveryDimensionKey } from "../../../api/delivery";
import { KeyAttributeCell } from "./DimensionAttributeFilter";

/** A column per attribute of a table of keys, in the order the dimension declares them. */
export function keyAttributeColumns(attributes: DeliveryDimensionAttributeSpec[]): Column<DeliveryDimensionKey>[] {
  return attributes.map((attribute) => ({
    id: `attribute-${attribute.name}`,
    header: attribute.name,
    render: (row: DeliveryDimensionKey) => <KeyAttributeCell row={row} name={attribute.name} />,
  }));
}
