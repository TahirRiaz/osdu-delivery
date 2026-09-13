import { useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Eye, Loader2, Save, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { deliveryApi, type DeliveryTemplateDetail } from "../../api/delivery";
import { ProblemView, problemText, TemplateSheet } from "./TemplateSheet";

/** The largest schema file the page reads. A bundled OSDU schema runs to a few hundred kilobytes. */
const MaxSchemaMegabytes = 10;

/** A schema laid out for a look: what was laid out, where it came from, and the template it gave. */
interface Shown {
  kind: string;
  schema: Record<string, unknown>;
  origin: string;
  detail: DeliveryTemplateDetail;
}

/** The kind a bundled schema says it describes (its top-level x-osdu-schema-source), or null when it does not say. */
function declaredKind(text: string): string | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    // Text that is not JSON declares no kind. Preview reports what is wrong with it when it is asked to lay it out.
    return null;
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return null;
  }

  const source = (parsed as Record<string, unknown>)["x-osdu-schema-source"];
  return typeof source === "string" && source.trim() !== "" ? source.trim() : null;
}

/** Checks the kind and parses the schema text before anything is sent. The first problem found is the error. */
function readSchema(kind: string, text: string): { schema: Record<string, unknown> | null; error: string | null } {
  const trimmedKind = kind.trim();
  if (trimmedKind === "") {
    return { schema: null, error: "Give the kind the schema describes, such as osdu:wks:master-data--Wellbore:1.3.0." };
  }

  if (trimmedKind.split(":").length !== 4) {
    return { schema: null, error: "Write the kind as authority:source:entityType:version, such as osdu:wks:master-data--Wellbore:1.3.0." };
  }

  if (text.trim() === "") {
    return { schema: null, error: "Choose a schema file, or paste the schema JSON." };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    return { schema: null, error: `The schema is not valid JSON: ${error instanceof Error ? error.message : String(error)}` };
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return { schema: null, error: "The schema must be a JSON object." };
  }

  return { schema: parsed as Record<string, unknown>, error: null };
}

/**
 * Import file: a bundled schema file, or pasted schema JSON, laid out as a template and saved the same way as a schema
 * fetched from OSDU. For an environment without access to OSDU, and for tests.
 */
export function TemplatesImportTab({ canAuthor }: { canAuthor: boolean }) {
  const queryClient = useQueryClient();
  const fileInput = useRef<HTMLInputElement>(null);
  const [kind, setKind] = useState("");
  const [text, setText] = useState("");
  const [file, setFile] = useState<{ name: string; text: string } | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [reading, setReading] = useState(false);
  const [shown, setShown] = useState<Shown | null>(null);
  const [sheetOpen, setSheetOpen] = useState(false);

  const preview = useMutation({
    mutationFn: async (input: Omit<Shown, "detail">): Promise<Shown> =>
      ({ ...input, detail: await deliveryApi.previewTemplate(input.kind, input.schema) }),
    onSuccess: (result) => {
      save.reset();
      setShown(result);
      setSheetOpen(true);
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const save = useMutation({
    mutationFn: (input: Shown) => deliveryApi.saveTemplate(input.kind, input.schema, input.origin),
    onSuccess: (saved, input) => {
      toast.success(saved.outcome === "created"
        ? `Saved template ${saved.template.kind} version ${saved.template.version}.`
        : `Template ${saved.template.kind} version ${saved.template.version} was already saved, so nothing changed.`);
      // The sheet now shows the version as saved, with who saved it and from where.
      setShown({ ...input, detail: { ...input.detail, saved: saved.template } });
      void queryClient.invalidateQueries({ queryKey: ["delivery", "templates"] });
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const choose = (chosen: File | undefined) => {
    if (chosen === undefined) {
      return;
    }

    if (chosen.size > MaxSchemaMegabytes * 1024 * 1024) {
      setProblem(`The file '${chosen.name}' is ${(chosen.size / 1024 / 1024).toFixed(1)} MB; a schema file is at most ${MaxSchemaMegabytes} MB.`);
      return;
    }

    setReading(true);
    setProblem(null);
    chosen.text()
      .then((content) => {
        setText(content);
        setFile({ name: chosen.name, text: content });
        const declared = declaredKind(content);
        if (declared !== null) {
          setKind((current) => (current.trim() === "" ? declared : current));
        }
      })
      .catch((error: unknown) => {
        setProblem(`The file '${chosen.name}' could not be read: ${error instanceof Error ? error.message : String(error)}`);
      })
      .finally(() => {
        setReading(false);
        // Choosing the same file again after editing the text reads it afresh.
        if (fileInput.current !== null) {
          fileInput.current.value = "";
        }
      });
  };

  const runPreview = () => {
    const { schema, error } = readSchema(kind, text);
    setProblem(error);
    if (schema === null) {
      return;
    }

    // The origin names the file only while the text is still exactly what was read from it.
    const origin = file !== null && text === file.text ? `file ${file.name}` : "pasted schema";
    preview.mutate({ kind: kind.trim(), schema, origin });
  };

  const clear = () => {
    setKind("");
    setText("");
    setFile(null);
    setProblem(null);
    preview.reset();
  };

  return (
    <div className="flex flex-col gap-4" data-testid="templates-import">
      <Card className="gap-3 rounded-lg p-4" data-testid="templates-import-form">
        <p className="text-xs text-muted-foreground">
          A bundled schema holds the schema and every definition it refers to in one JSON document. It is laid out and saved
          exactly like a schema fetched from OSDU.
        </p>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="templates-import-kind">Kind</Label>
            <Input
              id="templates-import-kind"
              className="h-8 font-mono"
              placeholder="osdu:wks:master-data--Wellbore:1.3.0"
              value={kind}
              onChange={(event) => setKind(event.target.value)}
              data-testid="templates-import-kind"
            />
            <p className="text-xs text-muted-foreground">Filled in from the file when the schema names its kind.</p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="templates-import-file">Schema file</Label>
            <Input
              id="templates-import-file"
              ref={fileInput}
              type="file"
              accept=".json,application/json"
              className="h-8 text-[13px]"
              disabled={reading}
              onChange={(event) => choose(event.target.files?.[0])}
              data-testid="templates-import-file"
            />
            {reading && <p className="text-xs text-muted-foreground">Reading the file.</p>}
            {file !== null && !reading && (
              <p className="text-xs text-muted-foreground" data-testid="templates-import-file-name">
                Read <span className="font-mono">{file.name}</span>{text === file.text ? "." : ", and edited since."}
              </p>
            )}
          </div>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="templates-import-json">Schema JSON</Label>
          <Textarea
            id="templates-import-json"
            className="field-sizing-fixed h-72 resize-y font-mono text-[12px]"
            spellCheck={false}
            placeholder="Paste the bundled schema here, or choose a file above."
            value={text}
            onChange={(event) => setText(event.target.value)}
            data-testid="templates-import-json"
          />
        </div>
        {problem !== null && <p className="text-xs font-medium text-destructive" data-testid="templates-import-error">{problem}</p>}
        {preview.isError && <ProblemView error={preview.error} testId="templates-import-preview-error" />}
        <div className="flex flex-wrap items-center gap-2">
          <Button size="sm" onClick={runPreview} disabled={preview.isPending || reading} data-testid="templates-import-preview">
            {preview.isPending ? <Loader2 className="animate-spin" /> : <Eye />}
            Preview
          </Button>
          <Button variant="ghost" size="sm" onClick={clear} disabled={preview.isPending} data-testid="templates-import-clear">
            <X />
            Clear
          </Button>
          {shown !== null && !sheetOpen && (
            <Button variant="link" size="sm" onClick={() => setSheetOpen(true)} data-testid="templates-import-reopen">
              Show the last preview
            </Button>
          )}
        </div>
      </Card>

      <TemplateSheet
        open={sheetOpen && shown !== null}
        onClose={() => setSheetOpen(false)}
        title={shown?.kind ?? "Schema"}
        description={shown !== null ? `From ${shown.origin}. Nothing is saved until you save it.` : ""}
        progress={null}
        problem={save.isError ? <ProblemView error={save.error} testId="templates-import-save-error" /> : null}
        detail={shown?.detail}
        previewSchema={shown?.schema}
        actions={canAuthor && shown !== null ? (
          <Button size="sm" onClick={() => save.mutate(shown)} disabled={save.isPending} data-testid="templates-import-save">
            {save.isPending ? <Loader2 className="animate-spin" /> : <Save />}
            Save template
          </Button>
        ) : undefined}
        busy={save.isPending}
        testId="templates-import-sheet"
      />
    </div>
  );
}
