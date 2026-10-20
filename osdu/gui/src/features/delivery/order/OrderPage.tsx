import { useAuth } from "@/auth/AuthContext";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { PartitionOrderTab } from "./PartitionOrderTab";
import { ReleaseLineageTab } from "./ReleaseLineageTab";
import { ScheduleOrderTab } from "./ScheduleOrderTab";

type OrderTab = "schedules" | "partition" | "releases";

const TABS: readonly string[] = ["schedules", "partition", "releases"];

/**
 * Order (osdu/docs/reference/concepts/osdu-order.md): the order records reach OSDU in, read from what OSDU's schemas say
 * each kind refers to. Lineage orders each repository's flows by it, so a schedule runs them in that order; across
 * schedules nothing waits, and the Schedules tab shows which schedules run best before which and whether their fires fall
 * that way. The Partition tab shows a partition's order with every repository's deliveries, and Releases OSDU's own
 * lineage, release by release.
 */
export default function OrderPage() {
  const { hasScope } = useAuth();
  const [stored, setTab] = useLocalStorageState<string>("sqlflow.osdu.order.tab", "schedules");
  const tab: OrderTab = TABS.includes(stored) ? (stored as OrderTab) : "schedules";

  return (
    <Page data-testid="page-delivery-order">
      <PageHeader
        title="Order"
        subtitle="The order records reach OSDU in, from what each kind refers to. Each repository's flows run in it; schedules are shown against it."
      />
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList data-testid="order-tabs">
          <TabsTrigger value="schedules" data-testid="order-tab-schedules">Schedules</TabsTrigger>
          <TabsTrigger value="partition" data-testid="order-tab-partition">Partition</TabsTrigger>
          <TabsTrigger value="releases" data-testid="order-tab-releases">Releases</TabsTrigger>
        </TabsList>
        <TabsContent value="schedules">
          <ScheduleOrderTab />
        </TabsContent>
        <TabsContent value="partition">
          <PartitionOrderTab />
        </TabsContent>
        <TabsContent value="releases">
          <ReleaseLineageTab canBuild={hasScope("operate")} />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
