import { useState } from "react";
import { Braces, Layers, Telescope } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { RelativeTime } from "@/components/RelativeTime";
import { idParts } from "../osduRecordModel";
import { kindParts, recentRecords, recentTypes, scopeLabel, type RecentRecord, type RecentType } from "./explorerModel";

/** What the search field takes, as the welcome shows it: what is typed, an example, and what Enter does with it. */
function searches(partition: string | null): { what: string; example: string; does: string; lucene?: boolean }[] {
  return [
    { what: "A record id", example: `${partition ?? "dev"}:master-data--Wellbore:…`, does: "opens the record" },
    { what: "The start of an id", example: "master-data--Wellbore:NO-33", does: "lists the ids that start with it" },
    { what: "A name or any text", example: "NO 33/9-C-28 B", does: "finds it anywhere in a record" },
    { what: "A Lucene query", example: "data.FacilityName:\"NO 33*\"", does: "sent as written", lucene: true },
  ];
}

/**
 * Where the explorer opens: nothing read from OSDU until the reader asks. The search field above takes what they hold; the
 * records and types they opened lately (kept in this browser, so they show at once) are one click away; and the types of
 * the partition are read when asked for. A partition of millions answers its first question in the time that question
 * takes, and never in the time of one nobody asked.
 */
export function ExplorerWelcome({ partition, onBrowseTypes, onOpenRecent, onOpenType }: {
  partition: string | null;
  onBrowseTypes: () => void;
  onOpenRecent: (record: RecentRecord) => void;
  onOpenType: (type: RecentType) => void;
}) {
  // Read once when the welcome shows: what this browser remembers, which changes only by what the reader does elsewhere.
  const [records] = useState(() => recentRecords().slice(0, 8));
  const [types] = useState(() => recentTypes());
  return (
    <Card className="gap-0 rounded-lg p-0" data-testid="explorer-welcome">
      <div className="mx-auto flex w-full max-w-3xl flex-col gap-8 px-6 py-10">
        <div className="flex flex-col items-start gap-3">
          <Telescope className="size-8 text-muted-foreground" aria-hidden />
          <div>
            <h2 className="text-[17px] font-semibold">{`Explore what ${partition ?? "OSDU"} holds`}</h2>
            <p className="mt-1 text-[13px] text-muted-foreground">
              Search above, or start from a type. Nothing is read from OSDU until you ask.
            </p>
          </div>
          <Button variant="outline" size="sm" className="gap-1.5" onClick={onBrowseTypes} data-testid="explorer-browse-types">
            <Layers />
            Browse types
          </Button>
        </div>

        {(records.length > 0 || types.length > 0) && (
          <div className="grid gap-8 sm:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
            {records.length > 0 && (
              <section className="flex min-w-0 flex-col gap-1" data-testid="explorer-welcome-recent">
                <h3 className="px-2 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Opened lately</h3>
                {records.map((record) => {
                  const parts = idParts(record.id);
                  return (
                    <button
                      key={`${record.partition ?? ""}|${record.id}`}
                      type="button"
                      onClick={() => onOpenRecent(record)}
                      className="flex min-w-0 items-center gap-3 rounded-md px-2 py-1.5 text-left hover:bg-accent/60"
                      title={record.id}
                      data-testid="explorer-welcome-record"
                    >
                      <span className="min-w-0 flex-1 truncate text-[13px]">{record.name ?? parts.unique}</span>
                      <span className="shrink-0 text-[12px] text-muted-foreground">
                        {record.kind ? kindParts(record.kind).type : parts.type}
                        {record.partition !== null && record.partition !== partition ? ` in ${record.partition}` : ""}
                      </span>
                      <span className="w-24 shrink-0 text-right text-[11px] text-muted-foreground"><RelativeTime value={record.openedUtc} absolute={false} /></span>
                    </button>
                  );
                })}
              </section>
            )}
            {types.length > 0 && (
              <section className="flex min-w-0 flex-col gap-2" data-testid="explorer-welcome-types">
                <h3 className="px-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Browsed lately</h3>
                <div className="flex flex-wrap gap-1.5">
                  {types.map((type) => (
                    <Button
                      key={type.kind}
                      variant="secondary"
                      size="sm"
                      className="h-7 px-2.5 text-[12px] font-normal"
                      onClick={() => onOpenType(type)}
                      title={type.kind}
                      data-testid="explorer-welcome-type"
                    >
                      {scopeLabel(type.scope)}
                    </Button>
                  ))}
                </div>
              </section>
            )}
          </div>
        )}

        <section className="flex flex-col gap-1" data-testid="explorer-welcome-searches">
          <h3 className="px-2 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">What you can search</h3>
          <div className="grid grid-cols-[auto_auto_minmax(0,1fr)] items-baseline gap-x-6 gap-y-1.5 px-2 text-[12px]">
            {searches(partition).map((search) => (
              <div key={search.what} className="contents">
                <span className="text-foreground">{search.what}</span>
                <span className="inline-flex min-w-0 items-center gap-1.5 font-mono text-[11px] text-muted-foreground">
                  {search.lucene && <Braces className="size-3 shrink-0" aria-label="with the braces on" />}
                  <span className="truncate">{search.example}</span>
                </span>
                <span className="text-muted-foreground">{search.does}</span>
              </div>
            ))}
          </div>
        </section>
      </div>
    </Card>
  );
}
