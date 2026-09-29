import { useQuery } from "@tanstack/react-query";
import { Skeleton } from "@/components/ui/skeleton";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { deliveryApi, type DeliveryAssertionTotals } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { AssertionBoard } from "./AssertionBoard";
import { counted } from "./assertionFormat";

/** The page's one line of totals: the flows and tests of the workbench's partition, and how they last came out. */
function subtitle(totals: DeliveryAssertionTotals, partition: string | null): string {
  const where = partition === null ? "" : ` in ${partition}`;
  const parts = [
    `${totals.passed.toLocaleString("en-US")} passed`,
    `${totals.failed.toLocaleString("en-US")} failed`,
    ...(totals.warned > 0 ? [`${totals.warned.toLocaleString("en-US")} warned`] : []),
    ...(totals.errored > 0 ? [`${totals.errored.toLocaleString("en-US")} errored`] : []),
    `${totals.notRun.toLocaleString("en-US")} not run`,
  ];
  return `${counted(totals.tests, "test")} of ${counted(totals.flows, "assertion flow")}${where}: ${parts.join(", ")}.`;
}

/**
 * Every test of every assertion flow in the workbench's partition, on one board: how OSDU stands after the data has
 * landed, where it does not hold, and the reports each run kept. A test runs from here on its own, with others picked, or
 * with every test of its flow.
 */
export default function DeliveryAssertionsPage() {
  const [active] = useActivePartition();
  const board = useQuery({
    queryKey: ["delivery", "assertions", "board", active],
    queryFn: () => deliveryApi.assertionBoard(),
    refetchInterval: 15000,
  });

  return (
    <Page data-testid="page-delivery-assertions">
      <PageHeader title="Tests" subtitle={board.data ? subtitle(board.data.totals, board.data.partition) : undefined} />
      {board.isError
        ? <ProblemView error={board.error} testId="assertion-board-error" />
        : board.data === undefined
          ? <Skeleton className="h-72 w-full rounded-lg" />
          : <AssertionBoard board={board.data} scope="all" />}
    </Page>
  );
}
