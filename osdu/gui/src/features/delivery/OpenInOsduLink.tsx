import { Link as RouterLink } from "react-router-dom";
import { BookOpenCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { deliveryRecordRoute, type DeliveryRecordRef } from "../../api/delivery";

/**
 * A row action of a record listing: opens the record's page on its In OSDU tab, which reads the record as OSDU holds it.
 * A row's own click opens the page as it always has; this goes straight to what OSDU holds.
 */
export function OpenInOsduLink({ record }: { record: DeliveryRecordRef }) {
  return (
    <Button variant="ghost" size="sm" className="h-7 px-2" asChild>
      <RouterLink
        to={`${deliveryRecordRoute(record)}?tab=osdu`}
        onClick={(event) => event.stopPropagation()}
        title="Read the record as OSDU holds it"
        data-testid="open-in-osdu"
      >
        <BookOpenCheck />
        In OSDU
      </RouterLink>
    </Button>
  );
}
