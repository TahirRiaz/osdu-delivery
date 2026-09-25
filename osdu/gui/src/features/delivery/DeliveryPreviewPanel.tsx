import { useMemo, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { CircleAlert, Eye, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { useAuth } from "@/auth/AuthContext";
import { deliveryApi, type DeliveryParameter, type DeliveryRecordPreview } from "../../api/delivery";
import { InterfacePicker } from "./InterfacePicker";
import { useInterfaceChoice } from "./useInterfaceChoice";
import { RecordPreviewView } from "./RecordPreviewView";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { isTerminalTask, useComputeTask } from "./useComputeTask";

/** The longest key the control plane takes; a longer text is a paste of something else. */
const MAX_KEY = 4000;

/** The values a preview is sent: those given, trimmed; a parameter left empty takes its declared default on the node. */
function valuesToSend(parameters: DeliveryParameter[], values: Record<string, string>): Record<string, string> {
  return Object.fromEntries(parameters
    .map((parameter) => [parameter.name, (values[parameter.name] ?? "").trim()] as const)
    .filter(([, value]) => value !== ""));
}

/**
 * The Preview tab of a delivery flow: one record rendered on a node exactly as a delivery would render it, and sent
 * nowhere. The record is the scope's first, or the one a key names; the flow's parameters fill the scope, their defaults
 * where none is given. What comes back is the record's document as its route sends it, what the next run would do with
 * it, the requests the route would make, the files it would upload and what it refers to.
 */
export function DeliveryPreviewPanel({ pipelineId, flowName }: { pipelineId: string; flowName: string }) {
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const { interfaces, names, many, interfaceName, current, selectInterface } = useInterfaceChoice(pipelineId);
  const parameters = useMemo(() => current?.parameters ?? [], [current]);
  const keyColumns = current?.keyColumns ?? [];
  const [key, setKey] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  const [taskId, setTaskId] = useState<string | null>(null);
  const task = useComputeTask(taskId);

  // What the last preview that finished answered: kept while the next one runs, so the page does not jump, and replaced
  // when the next one lands.
  const [outcome, setOutcome] = useState<{ taskId: string; result: DeliveryRecordPreview | null; failure: string | null } | null>(null);
  const settled = task.data !== undefined && isTerminalTask(task.data) ? task.data : undefined;
  if (settled !== undefined && outcome?.taskId !== settled.taskId) {
    const answered = settled.status === "succeeded" && settled.result !== null && settled.result !== undefined;
    setOutcome({
      taskId: settled.taskId,
      result: answered ? settled.result as DeliveryRecordPreview : null,
      failure: answered
        ? null
        : settled.error ?? (settled.status === "cancelled" ? "The preview was cancelled before a node finished it." : "The preview ended without an answer."),
    });
  }

  // Another interface is another table and another scope: its preview starts afresh.
  const shownFor = current?.flowId ?? null;
  const [previewedFor, setPreviewedFor] = useState<string | null>(shownFor);
  if (shownFor !== previewedFor) {
    setPreviewedFor(shownFor);
    setTaskId(null);
    setOutcome(null);
    setValues({});
  }

  const preview = useMutation({
    mutationFn: () => deliveryApi.preview(
      pipelineId,
      { key: key.trim() === "" ? null : key.trim(), values: valuesToSend(parameters, values) },
      interfaceName),
    onSuccess: (accepted) => setTaskId(accepted.taskId),
  });

  const missing = parameters.filter((p) => p.required && (p.default === null || p.default === undefined) && (values[p.name] ?? "").trim() === "");
  const tooLong = key.trim().length > MAX_KEY;
  const running = preview.isPending || (taskId !== null && !isTerminalTask(task.data));
  const blocked = !canOperate || current === undefined || missing.length > 0 || tooLong || running;
  const submit = () => {
    if (!blocked) {
      preview.mutate();
    }
  };

  const result = outcome?.result ?? null;
  const failure = outcome?.failure ?? null;

  if (interfaces.isError) {
    return <ProblemView error={interfaces.error} testId="preview-interfaces-error" />;
  }

  return (
    <div className="flex flex-col gap-4" data-testid="delivery-panel-preview">
      {many && (
        <InterfacePicker names={names} interfaceName={interfaceName} onSelect={selectInterface} caption={`of ${names.length} interfaces of ${flowName}`} />
      )}

      <Card className="gap-3 rounded-lg p-4" data-testid="preview-form-card">
        <div className="flex flex-col gap-1">
          <h2 className="text-sm font-semibold">Preview a record</h2>
          <p className="text-[13px] text-muted-foreground">
            Renders one record on a node exactly as a delivery would, and sends nothing: OSDU, the ledger and the work location are left as they are.
            Leave the key empty for the first record of the scope.
          </p>
        </div>
        {current === undefined
          ? <Skeleton className="h-9 w-full rounded-md" />
          : (
            <form
              className="flex flex-col gap-3"
              onSubmit={(event) => { event.preventDefault(); submit(); }}
              data-testid="preview-form"
            >
              <div className="flex flex-wrap items-end gap-2">
                <div className="flex min-w-0 flex-1 flex-col gap-1">
                  <Label htmlFor="preview-key" className="text-[12px] text-muted-foreground">
                    {keyColumns.length > 0 ? `Record key (${keyColumns.join(", ")})` : "Record key"}
                  </Label>
                  <Input
                    id="preview-key"
                    value={key}
                    onChange={(event) => setKey(event.target.value)}
                    placeholder="Empty for the first record; or a source key, a delivery key, an OSDU id, or [&quot;part&quot;, &quot;part&quot;]"
                    className="h-9 font-mono text-[13px]"
                    spellCheck={false}
                    autoComplete="off"
                    aria-invalid={tooLong}
                    data-testid="preview-key"
                  />
                </div>
                <Button type="submit" size="sm" className="h-9" disabled={blocked} title={canOperate ? undefined : "A preview runs on a node, which takes the operate scope."} data-testid="preview-run">
                  {running ? <Loader2 className="animate-spin" /> : <Eye />}
                  Preview
                </Button>
              </div>
              {tooLong && <p className="text-[12px] text-destructive">{`The key is ${key.trim().length.toLocaleString()} characters; a record key is at most ${MAX_KEY.toLocaleString()}.`}</p>}
              {parameters.length > 0 && (
                <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3" data-testid="preview-parameters">
                  {parameters.map((parameter) => (
                    <div key={parameter.name} className="flex flex-col gap-1">
                      <Label htmlFor={`preview-parameter-${parameter.name}`} className="text-[12px] text-muted-foreground" title={parameter.description ?? undefined}>
                        <span className="font-mono">{parameter.name}</span>
                        {parameter.required && (parameter.default === null || parameter.default === undefined) && <span className="text-destructive">*</span>}
                      </Label>
                      <Input
                        id={`preview-parameter-${parameter.name}`}
                        value={values[parameter.name] ?? ""}
                        onChange={(event) => setValues((was) => ({ ...was, [parameter.name]: event.target.value }))}
                        placeholder={parameter.default ?? (parameter.required ? "required" : "optional")}
                        className="h-8 font-mono text-[13px]"
                        spellCheck={false}
                        autoComplete="off"
                        data-testid={`preview-parameter-${parameter.name}`}
                      />
                    </div>
                  ))}
                </div>
              )}
              {missing.length > 0 && (
                <p className="text-[12px] text-muted-foreground" data-testid="preview-missing">
                  {`The scope needs ${missing.map((p) => p.name).join(", ")}: the flow declares ${missing.length === 1 ? "it" : "them"} required, with no default.`}
                </p>
              )}
            </form>
          )}
      </Card>

      {preview.isError && <ProblemView error={preview.error} testId="preview-error" />}
      {running && <TaskProgress label="Rendering the record on a node" task={task.data} testId="preview-progress" />}
      {task.isError && <ProblemView error={task.error} testId="preview-task-error" />}
      {failure !== null && !running && (
        <Alert variant="destructive" data-testid="preview-failed">
          <CircleAlert />
          <AlertTitle>The preview could not be made</AlertTitle>
          <AlertDescription className="whitespace-pre-wrap">{failure}</AlertDescription>
        </Alert>
      )}
      {result !== null && <RecordPreviewView preview={result} />}
    </div>
  );
}
