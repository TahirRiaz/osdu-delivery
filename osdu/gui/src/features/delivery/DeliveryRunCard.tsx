import { Info } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import type { RunDetail } from "@/api/types";
import { CodeView } from "@/components/CodeView";
import { prettyJson } from "./prettyJson";
import { runRequest } from "./runOutcome";

const OUTCOME_COPY: Record<string, string> = {
  cache: "What the refresh reported when it finished: the version it wrote (or that nothing changed), each type's record count and changes, and the delivered records those changes reach.",
  retrieval: "What the retrieval reported when it finished: the window it covered, the records and files it wrote, and where they went.",
  assertion: "How the tests came out: the report it kept, each outcome's count, and the tests that did not pass. Every result, whole, is in the report.",
};

const DEFAULT_OUTCOME_COPY = "What the run reported when it finished: its counts, the submission it worked on, and how far it fanned out.";

const REDELIVER_LABELS: Record<string, string> = {
  all: "metadata and payload",
  metadata: "metadata only",
  payload: "payload only",
  record: "the record only",
  files: "files only",
  bulk: "bulk data only",
  workflow: "the workflow run only",
};

/**
 * What a delivery, retrieval, cache or assertion run was asked to do (its operation, the payload its trigger sent, the flow parameter
 * values it was given) and, once it finished, what it reported.
 */
export default function DeliveryRunCard({ run }: { run: RunDetail }) {
  const request = runRequest(run);
  const values = Object.entries(run.values ?? {});
  const asked = run.operation !== null || values.length > 0 || Object.keys(run.payload ?? {}).length > 0;

  return (
    <>
      {asked && (
        <Alert data-testid="run-parameters">
          <Info />
          <AlertDescription>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium text-foreground">Parameters:</span>
              {run.operation !== null && (
                <Badge variant="secondary" className="bg-info/12 text-info" data-testid="run-operation">{run.operation}</Badge>
              )}
              {request.force && <Badge variant="secondary" className="bg-warning/15 text-warning">forced</Badge>}
              {request.submissionId !== null && (
                <Badge variant="secondary" className="font-mono">submission {request.submissionId}</Badge>
              )}
              {request.recordKeys.length > 0 && (
                <Badge variant="secondary">
                  {request.recordKeys.length} scoped {request.recordKeys.length === 1 ? "record" : "records"}
                </Badge>
              )}
              {request.redeliver !== null && (
                <Badge variant="secondary" data-testid="run-redeliver">
                  {`redeliver ${REDELIVER_LABELS[request.redeliver] ?? request.redeliver}${request.recordKeys.length === 0 ? " of every delivered record" : ""}`}
                </Badge>
              )}
              {request.slices.length > 0 && (
                <Badge variant="secondary" className="font-mono" data-testid="run-slices">
                  {`${request.slices.length === 1 ? "slice" : "slices"} ${request.slices.join(", ")}`}
                </Badge>
              )}
              {request.tests.length > 0 && (
                <Badge variant="secondary" className="font-mono" data-testid="run-tests-picked">
                  {`${request.tests.length === 1 ? "test" : "tests"} ${request.tests.join(", ")}`}
                </Badge>
              )}
              {request.tags.map((tag) => (
                <Badge key={`tag-${tag}`} variant="secondary" className="font-mono" data-testid="run-tags-picked">#{tag}</Badge>
              ))}
              {values.map(([name, value]) => (
                <Badge key={name} variant="secondary" className="font-mono">{name}={value}</Badge>
              ))}
            </div>
          </AlertDescription>
        </Alert>
      )}

      {run.resultJson !== null && (
        <Card className="gap-2 rounded-lg p-3" data-testid="run-result">
          <h2 className="text-[13px] font-medium">Outcome</h2>
          <p className="text-[13px] text-muted-foreground">{OUTCOME_COPY[run.flowKind] ?? DEFAULT_OUTCOME_COPY}</p>
          <CodeView value={prettyJson(run.resultJson)} language="json" height={220} data-testid="run-result-json" />
        </Card>
      )}
    </>
  );
}
