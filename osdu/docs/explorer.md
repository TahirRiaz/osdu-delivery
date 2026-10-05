# The explorer

The explorer is a browser of what an OSDU partition holds, read live from OSDU's own search and storage services. It
answers "what is in OSDU?", where the Records page answers "what did the delivery system do?". Nothing it shows comes
from the ledger, a mapping, the cache or the catalog: the types, the records, their versions, their links and the
records that mention them are what OSDU answers at that moment.

It is the **Explorer** entry of the OSDU navigation group (`/delivery/explorer`), and it reads the partition the
workbench's title bar names.

## How it reaches OSDU

The explorer is part of OSDU Delivery and uses its way into OSDU, nothing else of it. Every read runs in the control
plane as it is asked (`delivery-explore`), as a record page's read-back does, through the OSDU connection of a delivery
flow that reaches the partition: the flow's endpoint, credentials and `data-partition-id`, resolved with the
partition's central configuration as a run resolves them. The connection is kept between reads, so a token is fetched
once rather than for every read. The reader never picks the flow; the control plane picks it for the partition, the same one every time:

- a flow that names or serves the partition (`partitions:`, or the registry), or whose `data-partition-id` header names
  it, or whose ledger the directory keeps in it;
- through the platform's own services: any route but `dspdm` and `etp`, whose endpoints are not the platform's;
- the `storage` route first, since its endpoint is the platform root by definition, then by flow and interface name.

The green dot beside the page's title says the view is live; its tooltip names the flow whose connection is used and the endpoint
as the flow writes it (a reference stays a reference). A partition no flow reaches says so, with what would lend it a
connection. Reading OSDU with a flow's credentials is an operate action, as reading a record back is, so the page asks
for the operate scope.

Whatever route the flow delivers by, the explorer reads the platform's own services: `POST /api/search/v2/query` for
types, records and groups, and the storage service for a record and its versions (`GET /api/storage/v2/records/{id}`,
`/records/versions/{id}` and `/records/{id}/{version}`; a storage flow's own `protocolOptions.verifyPath` where it names
one). The read of a record is the record page's own read-back (`ReadRecordOperation.ReadBackAsync`) over the storage
protocol, so both pages read a record the same way. A check of records against their schemas ([Validate](#validate))
reads the Schema service as well (`GET /api/schema-service/v1/schema/{id}`), and looks the records they refer to up in
storage's batch read (`POST /api/storage/v2/query/records`, or the flow's `protocolOptions.verifyBatchPath`). The types
that refer to a type ([Referenced by](#referenced-by)) are read from the Schema service's listing
(`GET /api/schema-service/v1/schema`) and the schemas it lists.

## Browsing

The page opens on a welcome that reads nothing from OSDU, so it shows at once however large the partition. It fills the
window as the browse view does: **Recently opened**, the records opened lately as the grid the records of a type use
(arrow keys and Enter open one, the id copies on hover, **Clear** forgets them), and beside it **Types**, with **Browse
every type** and the types browsed lately, over **Search syntax**, what the search field takes with an example of each.
Both lists are kept in the browser. Each group of types has its glyph (master data, reference data, work product
components, datasets), the same in these lists and in the list of types. `/` anywhere on the page but in a field puts
the cursor in the search that is the list's (the type's when one is picked, else the header's), and the field shows the
key while it is empty. OSDU is read only once the reader asks: Browse types reads the types alone and waits for one to be
picked, a type picked reads its records, a search reads what it finds, and a record opened reads that record and
nothing behind it. While a read is under way, a slim bar sweeps along the top of the pane it will fill, and what is
already shown stays readable.

Once asked, the records of the place picked stand beside the kinds of the partition:

- **Types.** One search aggregation over `kind` across every kind (`*:*:*:*`) counts the records of each, and the list
  groups them as OSDU names them: the group (`master-data`, `reference-data`, `work-product-component`, `dataset`, ...),
  the type in it, and under a type kept in several kinds (versions, authorities) each kind. A count past a hundred
  thousand reads compact (1.3M), the exact number on hover. A group of more than 24 types starts folded, and a filter
  finds a type among hundreds. Picking a group, a type or a kind narrows the records to it (`*:*:master-data--*:*`,
  `*:*:master-data--Wellbore:*`, or the kind). The counts follow the text and the values narrowed to, so the list also
  says where else a search finds something. When the service names fewer groups than there are kinds, the foot of the
  list counts the records in kinds not listed.
- **Records.** A grid of one row per record: its name (`data.FacilityName`, `data.Name`, `data.ProjectName`,
  `data.Code` or its file's name, the first it holds; else the unique part of its id), its type and kind version when
  every type is in view, its id cut from the start so the end that tells ids apart stays visible (a copy on hover), and
  when it last changed. The grid fits its panel and scrolls inside it under a fixed header; only the rows in view are
  drawn, so ten thousand rows scroll as lightly as a hundred. Rows load a hundred at a time as the grid is scrolled. The
  arrow keys, Page Up and Down, Home and End move through the rows, and Enter opens one. The records of a group, a type
  or a kind have a search field of their own over them, in line with the filter over the types, which searches inside
  it ([Searching](#searching)).
- **The place.** One line over the grid says where the records are (the partition, the group, the type, the kind, each a
  step back), how many there are (the index's exact count, `trackTotalCount`), how the search was read, and what the
  service made the explorer do (an order it would not sort by, a clause it refused), with the query sent one copy away.
- **Order.** Index order (best match once something is typed), last modified or last created, newest first. An order
  the service refuses is dropped with a note.
- **Group by.** Any property of the records in view, grouped by its distinct values with their counts, and a value
  picked narrows the records to it. The properties offered are the record's own (its kind, who created and changed it,
  its viewers, owners, legal tags and countries) for every type, and for a type the properties of its content, read from
  one of its records and typed by the value it holds. A list of objects is left out, since it may be indexed as nested,
  which a plain path does not reach. Each value narrowed to is a chip, asked exactly as the platform indexes the property
  (`OsduQuery.Equal`: the `keyword` sub-field of text).

The search service pages through the first 10,000 records a query matches (Elasticsearch's result window); past them
the foot of the grid says to narrow by type, text or a value. A partition of millions reads as fast as one of hundreds:
every page is one query of the index.

## Searching

Each search field searches the list it stands over. The field in the page's header searches every type (and opens an
id); the filter over the types finds a type in the list; and once a group, a type or a kind is picked, a field over its
records, in line with the filter, searches inside it ("Search Wellbore by id, name or any text"). The search shows in the
field whose place it searches: a search of every type moves into the type's field when a type is then picked, and back
to the header when **All types** or the partition is picked again. Searching from the header while a type is picked
searches every type, the values narrowed to kept; Enter on its empty field there asks nothing, so the type keeps its
search. Clearing a field clears the search it shows, not only what it holds: its cross, Escape, or Enter on the field
emptied (the hint then says **clear**) lists every record of the type again, or of every type from the header. A search
sent leaves the cursor in the field. Both fields read what is typed the same way. Enter reads it, and a hint at the end
of the field says what Enter will do:

| Typed | Read as | Query |
| --- | --- | --- |
| nothing | every record | none |
| a whole id, `dev:master-data--Wellbore:abc` (a version or a trailing colon is dropped) | opens the record | |
| the start of an id, `dev:master-data--Wellbore:NO-3` or `master-data--Wellbore:NO-3` (completed with the partition) | ids starting with it | `id:dev\:master\-data\-\-Wellbore\:NO\-3*` |
| words, `NO 33/9-C-28 B` | a phrase anywhere in a record | `"NO 33/9-C-28 B"` |
| one word in a type, `NO-33-9-C-28-B` with Wellbore picked | the phrase, or the record of that type whose id is or starts with it | `"..." OR id:"dev:master-data--Wellbore:..." OR id:...*` |
| a minted unique part (a GUID, a hash) in every type | the phrase, or any id of the partition ending with it | `"..." OR id:dev\:*\:...*` |
| a Lucene query, with the braces on | as written | the text |

A value is always a quoted phrase, never bare, since the service ORs bare words together; a bare term in front of a
wildcard has every reserved character escaped (`LuceneText.Escape`), and one holding an angle bracket, which no escape
carries, is asked as a phrase. A query the service refuses (400) is not a failure: the explorer drops the order, then
the clause the reading added beyond its core (the id clauses), each with a note, and a query still refused is the
page's answer with the service's words. Anything else that keeps an answer from coming fails the read, since an empty
page would say OSDU holds nothing.

Under the place, every list says what it is read by, exactly as the explorer sends it to the search service: the
`kind` and the Lucene `query` (none where the list is every record of the kind). The search box, the place and the values
narrowed to all make it, so it is the expression to reuse: copied as written or as a search request, or taken as a
Lucene query (**Edit**) into the field that shows the search (the type's, or the header's for every type) with the
cursor at its end, to be changed there, the place kept.

## A record

A record opens in the record inspector the record pages use, filling the page, with the place it sits in leading its
location (`dev > master-data > Wellbore > NO 33/9-C-28 B > data > ...`), the way back to the list as it was left
before it:

- every view of the inspector: the full document, the system fields, access and legal, the content branch by branch
  as fields, tables or JSON, and the linked records, each opened after it on the trail;
- **Validation**, under the outline's Checks: the record checked against the schema of its kind ([Validate](#validate));
- **Mentioned by**: the records whose values name this one (`"<id>" AND NOT id:"<id>"` over every property), the first
  hundred listed with their count by type, each opened on the trail, and all of them one click from a search of their own;
- **versions**: the picker reads the record as it was at any version OSDU keeps, and **Compare** puts any two side by
  side: what was added, changed and removed (from the left's point of view), the two documents with their unchanged
  stretches folded, and every moved value with its path. A version is read once and kept, since a version never changes.
  The record pages' OSDU tab compares the same way;
- **an id OSDU holds nothing under** offers the records whose ids are near it: those that go on from it (a paste cut
  short) and the same unique part under another type. An id of another partition says so, with a way to read it there.

Every record opened is remembered in the browser (the last twenty, nowhere else), under **Recent** and on the welcome,
and so is every type browsed (the last twelve).

## Validate

The explorer checks what OSDU holds against what OSDU expects of it: a record, or the records a search finds, against
the schema of their kind. The check is the one every validation of the delivery system makes
([validation-plan.md](validation-plan.md)), the gate before a document is sent among them, so a record the explorer
calls invalid breaks the same rule a document would be held for under `target.validation: { mode: enforce }`
([documents.md](documents.md)).

**Validation**, under the record's Checks, checks the version in view. What it is checked against is the reader's to
pick:

- **OSDU schema** (the default): what the partition's Schema service holds for the record's kind. Every schema it refers
  to by id (`osdu:wks:AbstractCommonResources:1.0.0`, with or without a fragment into it) is read too and bundled, each
  once, at most 200 for a kind. A reference the reader cannot follow (a web address, a schema the service does not hold,
  the bound reached) is named, and the values it describes are counted as not checked rather than passed;
- **Saved template**: a template saved in the delivery system, the kind's newest or a version picked from those saved.
  It is the one thing of the delivery system's own the explorer reads, and only when the reader picks it.

The answer is the verdict a delivered record carries in its history:

- the outcome, with the schema and how much was checked: **valid**, **invalid** (with the number of problems), or
  **unverified** when a part could not be checked (a pattern neither ECMAScript nor .NET reads, a reference not followed,
  a bound reached) and nothing else is wrong. The tooltip beside it says which schema, from where, how many rules were
  applied, and what the schema states that no check asserts;
- **Problems**: each one a row with where it is, the rule (`type`, `pattern`, `enum`, `required`, `relationship`, ...),
  and what is wrong with the value; the place opens that element of the record. Under it, what the problem comes to for
  the person fixing it ([Guidance](#guidance)): the value **found**, what the schema **takes** there, and the **fix**.
  Each field of the record a problem sits in, or sits inside, carries a mark whose tooltip lists them, each with its fix;
- **Not checked**: the parts the check could not judge, and why;
- **References not found**: the ids the record names that storage holds nothing under, each with where it names it. The
  references are looked up in storage alone, all at once: what OSDU holds is the answer, never the ledger or the cache.

A record storage does not hold (at that version) says so. A kind the Schema service holds no schema of, or a saved
template the kind does not have, is not checked; the pane says why and, where a template of the kind is saved, offers
to check against it. A Schema service that refuses to answer (403, or a failure of its own) leaves the record not
checked, with the service's words, redacted, as the reason.

A record read back from storage can hold its `ancestry`, `meta` or `tags` null or empty, which is how a record with
none of them can read whoever wrote it. The explorer reads such a block as absent rather than as a value that breaks the
schema, and the tooltip beside the outcome says it did (`meta is null, ...`). A document the gate checks before sending
is the system's own, and is held to the schema whole.

A record page's OSDU tab offers the same **Validation** under the record's Checks, for the record it shows and for each
record it opens after it. The check is this one, made of the record as the tab reads it: through the route and
credentials of the record's flow, in the partition its ledger names, rather than through the connection the explorer
picks for a partition. A flow whose route keeps no record in storage (dspdm, etp) has none to check, and the pane says
so.

### Guidance

Each problem comes with what it takes to fix it, worked out from the schema the record was checked against:

- **Found**: the value at the place, as the record holds it (`null`, `'E2E validate'`, `a list of 3 items`), or
  `absent` for a property the schema requires;
- **Takes**: what a value there is, in one line (`text matching ^NO `, `a list of objects (AbstractMetaItem)`, `the id
  of a master-data--Well record`, `one of 12 values`), and whether it is required;
- **Fix**: how to make the value meet the rule: leave an optional property out rather than null, the values an
  enumeration allows, the form a date takes, the number without quotes, the id of a record rather than its name or
  code, where a property of one's own belongs (`data.ExtensionProperties`), or that a record it refers to must be in
  OSDU first;
- **What the schema says here**, unfolded on request: the property's title and description in the schema's own words
  (the words written beside a reference, as OSDU writes them, before those of what it refers to), what it takes, whether
  it is required, its patterns, the values it allows, the entity types it points to, what each item or property is, the
  schema's own examples, and **OSDU's example**: the value at that place in the example record the OSDU data
  definitions publish for the kind (`Examples/<group>/<entity>.<version>.json`, the first item of each list taken).

The example records are read from the data definitions the Templates page browses
([mapping-templates.md](mapping-templates.md), Where templates come from): from the newest release, once per kind
version, and kept beside the release in the control plane's local copy, as a release that publishes none for a kind is.
A kind not of OSDU's own has none to look for; one the data definitions publish none for, or a repository that cannot
be reached, is said under the verdict, and every problem is guided by the schema alone. A part not checked says there is
nothing to change in the record for it. Validate these records gives the fix of each rule's example the same way.

### Validate these records

The shield in the list's toolbar checks the records the search finds, as it stands (the text, the place, the values
narrowed to), against the schemas of their kinds: the first 1,000 the search returns, read from storage in batches.
It answers:

- how many records the search matches and how many were read, and how many came to each outcome; a record whose kind no
  schema could be had for is counted apart, with why;
- **Rules broken most often**: each rule at each place, the number of records that break it, and one of them, with what
  is wrong there, what the schema takes there and the fix ([Guidance](#guidance)); the record opens in the explorer. The
  place reads a list's items as one (`data.VerticalMeasurements[]`), so a rule broken in every item of many records is
  one row;
- **Records**: each record read, with its outcome and its first problem; a record opens in the explorer.

A search matching more records than a check reads says so: the counts are of the first ones. A whole kind is checked by
an assertion flow's `conforms` test ([documents.md](documents.md#assertion-flow)), which reads up to a million records. The ids the
search found that storage did not return (deleted since, or not the caller's to read) are counted as such, and so are
the records holding an optional block null or empty, read as absent.

## Referenced by

**Referenced by**, in the list's toolbar when one type or one kind of it is picked, lists the types whose records name
records of that type: every kind the partition's Schema service holds with a property that refers to it, the versions
that do and those that do not, and where. It answers where a type is used across the partition's schemas: the users of
a code list before it changes, the kinds a master record is named from, the version a reference was added in.

- **What counts.** A property marked with `x-osdu-relationship` naming the type, wherever it sits: in `data`, in `meta`
  (`meta[].unitOfMeasureID`), inside a list (`data.VerticalMeasurements[].VerticalMeasurementUnitOfMeasureID`), inside
  an abstract schema the kind is made of, or in one form of a choice (`oneOf`, `anyOf`), found through the rules a check
  reads (`SchemaRules`). Where a schema marks no relationship, an id pattern naming the type counts too
  (`^[\w\-\.]+:reference-data\-\-UnitOfMeasure:...`), marked **by pattern**: OSDU's own schemas always write both, and a
  partition's own may write the pattern alone. The record's own `id` is never one. A reference names a type, never a
  version of it, so the answer holds for every version of the type picked; a kind picked asks about its type.
- **The answer.** The types by group (master data, work products, datasets, reference data), each with every version
  the partition holds of it as a chip: solid where its schema names the type, dashed where it does not, with the
  authority and source where a type's versions span several (`eq:custom 1.0.0`), a mark on a version in development or
  obsolete, and one on a version whose schema refers to a schema that could not be followed, behind which a place is not
  seen. Under it each property path, with the versions holding it where not all do (`in 1.1.0 to 1.5.0`). A type opens
  its records, a version the records of that kind. Properties that name any record of the type's group
  (`{"GroupType": "dataset"}`) are listed apart and folded, since a value there may be one of the type.
- **Where it is used.** The records of each kind are counted by the aggregation the list of types reads, and **Only
  types with records** keeps the versions the partition holds records of, and the types with such a version naming the
  type: where the type is used, not only where it may be. It is offered when the search service counted every kind.
- **A long answer.** A filter finds a type, a kind or a property path; a long list (a unit of measure is named by nearly
  every kind) is drawn sixty types at a time as it is scrolled.

**How it is read.** The Schema service cannot be asked which schemas refer to a type: its listing
(`GET /api/schema-service/v1/schema`) names schemas without their content. So the control plane reads them once and
keeps what each one names (`PartitionSchemaIndex`): one reading for each Schema service and partition, shared by every
reader, sixteen partitions at most. A pass lists every status (`PUBLISHED`, `DEVELOPMENT`, `OBSOLETE`) and scope
(`INTERNAL`, `SHARED`), a hundred at a time, then reads the schema of every kind (`GET /schema/{id}`) eight at a time,
bundled with the schemas it refers to as [Validate](#validate) bundles one; each of those is read once in a pass however
many kinds refer to it, and an abstract schema no kind refers to is never read. A pass runs on its own, through a
connection of its own: an ask waits a few seconds for it, then answers with where it stands (listing, or kinds read of
the kinds to read), and the dialog asks again until it is done; closing the dialog leaves it running.

A pass after the first reads only what may have changed: the kinds added since and those in development, since a
published or obsolete schema never changes; a kind no longer listed is dropped. A pass starts when nothing has been
read, when the reader asks for one (the refresh button), and when the reading is older than twelve hours; the reading it
replaces answers meanwhile. A kind whose schema cannot be read is listed with why, and the pass goes on; a pass that
fails fifty reads, more than it answered, stops, since the service is failing rather than one schema. A listing the
service refuses as asked (400) is noted, and any other refusal fails the pass. A pass that fails says why, keeps the
last reading, and runs again when asked, or after two minutes. The log has one line for each pass, never one for each
schema. A host that keeps no index (a node, the CLI) reads every schema for the ask, and answers once they are read.

## The query of an element

Beside every value and every section of a record (each row of the fields view, each item of a list's table, each line of
the JSON view), a search button shows the Lucene query that finds the records holding exactly that element, with
**Copy** and **Search** (which searches the explorer with the record's kind):

- **A value**: `data.FacilityName.keyword:"NO 16/2-9 S"`, a text by its `keyword` sub-field that holds it whole, a
  keyword, a number, a boolean or a date as itself; in a nested list, `nested(data.GeoContexts, (GeoTypeID.keyword:"..."))`.
  Beside it, its words (`data.FacilityName:"NO 16/2-9 S"`, in any case) and whether a record holds one
  (`_exists_:data.FacilityName`, not asked of a nested list, which a top-level query does not see).
- **An item of a nested list**: all its values together in one item,
  `nested(data.GeoContexts, (GeoPoliticalEntityID.keyword:"...Norway:" AND GeoTypeID.keyword:"...Country:"))`, the flat
  `AND` the search service's parser reads one property at a time.
- **A whole list, an object or a list of values**: every value it holds, each item of a nested list as its own
  `nested(...)`, joined by `AND`. A record may hold more besides. Up to 48 values are compared.

How each value is indexed is read from the saved template of the record's kind (another version of its type where that
one is not saved), as every search the module writes reads it; a list whose items are a choice of forms (`oneOf`, as
`GeoContexts` is) is read through its forms. Where the template cannot say (no template saved, a property it does not
declare, a list it leaves unindexed), the query is written from the value and marked **guessed**, with why in its
tooltip. How the query was written (the field, the template, the notes) is in the info tooltip; nothing is read from
OSDU to write it.

## Building a dimension

**Build a dimension**, in the explorer's header, turns the explorer into a dimension's workbench: the builder docks beside
the records, and everything else stays as it is. The workbench's side bar folds while the builder is docked, so the
records and the builder share the width; the activity bar opens it meanwhile, and it comes back as it was left when the
builder closes. The records are browsed, searched and drilled into as ever, and each
part of the dimension is picked on the value it is read from, in the record where it is. The output is the item a
dimension flow lists under `dimensions:` ([dimension-plan.md](dimension-plan.md), The document).

**The kind and the key.** The build reads the kind or the type the explorer shows (a type reads every version of its
kind, `*:*:<type>:*`), or, started where none is picked, the first one picked after. As it starts, it reads the kind's
saved template for the values that name other records (`x-osdu-relationship`): those are the keys a dimension most often
has, the record a record belongs to, whose name is the value. They are listed under **Keys** in the builder, the
likeliest first (a single value before a list of them, a property of `data` itself before a nested one, master data
before work products and reference data, and a property named after the type it names, such as `WellboreID`, before
another), and in every record of the kind each of them is marked with a key. The likeliest one the record in view holds
is made the key at once, and the dimension named after the type it names; any other is one click away, in the list or on
the value itself. A kind with no saved template suggests nothing, and says so.

**Picks where the value is.** Beside every value of a record, in the fields and the JSON views, a small menu offers what
the value can be made, by where the record is on the explorer's trail:

- In a record of the dimension's kind (the first on the trail): **Make it the key**, or **Collect it** (an attribute
  holding every value the key's records hold there, each with how many hold it; a dimension collects one).
- In a record opened from one of its links: **Read it as the value**, or **Read it as an attribute**. The link the first
  record was left by is the key (it is made the key where none is yet), and every link followed after it is a step of
  the path, so a value read two links on (a wellbore's country, reached through its GeoContexts) is written as the
  steps a build follows: `data.GeoContexts.GeoPoliticalEntityID`, then `data.GeoPoliticalEntityName`. A value or an
  attribute reads through at most three records, as the document allows.

A pick that cannot be made says why on the menu: a record of another kind, a record reached through another link than the
key's (where making that link the key is offered instead), one opened from the records that mention the one before it
(which no path reaches), or one too many links away. What the draft reads is marked on the values themselves: the key,
the value, each attribute, and the links a value or an attribute is read through.

**A step through one item of a list.** Following a link inside a list (the fourth of a wellbore's GeoContexts) passes
through one item of it, where a build follows every item, and reading a value inside one keeps the first it finds. Where
the items answer differently, the builder asks which are meant: it lists them, suggests the filter keeping the one
followed, preferring a property that names a type (`data.GeoContexts[GeoTypeID*=GeoPoliticalEntityType:Country:]`),
marks what the filter keeps as it is changed, and says what a build would then read; or every item is followed, or the
first value kept. A filter's text holds no `]`, and one that keeps nothing is refused.

**The table, the example and the YAML.** The builder holds the table (a column for the key, the value and each
attribute, each renamed in place and filled with the example row), how each value is cleaned (`trim`, `collapseSpaces`,
`upper`, `lower`, `nfc`, `nfkc`, `foldSeparators`, and any number of `replace` steps, applied in the order picked; a
`map` step, which reads a table of the database, is written in the flow file itself), what a key holds where nothing is
read (`unlabelled`), and under **More settings** the description, every version of the kind, the query, exact counting
and the most values. The example key is the record in view's own, then the keys held by the most records (up to 25), and
every part is filled with what it reads, made by the build's own code: the labeller following at most 20 references a
step, the cleaning, the attributes, the count of the records holding the key, and the collected values with theirs.
Where the value is read from a record, a key naming no record, or whose record holds nothing there, takes the
`unlabelled` text; without one, and where the key is its own value, it is the key as a value shows it (the code a record
id ends with). The YAML is written again as each pick is made, each problem listed with the part it is about (pointing
at it lights the line and the column), **Copy the YAML** takes it, and **How a row is built** draws it as a flow's
Definition tab draws a dimension, filled with the example key's row.

**Checked as a flow's own dimension.** The YAML is written by the control plane's writer, which quotes whatever YAML
would read otherwise (`yes`, `1.0`, `#`, a leading `-`, a colon and a space, a control character), and read back by the
loader a flow is read by, so what the builder shows is what a flow loads. It is then described by the dimension's
blueprint against the saved templates, and compared with the dimensions the database and the synced flows hold. An
error is what the loader refuses (pointed at its part), or a key or a collected path of `data` with no saved template of
the kind, which a build refuses too (with a link to the Templates page). A warning is a path a template does not
declare, records reached by id that no saved template describes (read as written), or a name whose table another
dimension writes.

**Nothing is saved.** The builder makes no flow and no dimension, writes nothing to OSDU or the database, and keeps
nothing of its own: the YAML is pasted into a dimension flow, which a repository sync brings in. The draft is part of the
explorer's address (`dim`), so a link or a refresh brings the same build back beside the same records; closing the
builder drops it, and asks first once it has a key. Its reads of OSDU are the explorer's own (the records browsed and
followed) and two through the same connection (`delivery-explore`, as the actions `dimension-keys` and
`dimension-example`): the commonest keys of the key's path, grouped by one search, and for the example the counts and
groups of the records holding the key. The suggested keys are read from the saved templates alone. Reading OSDU with a
flow's credentials takes the operate scope.

## Kept in the address

The text and whether it is Lucene (`q`, `lq`), the place (`kind`, `*:*:*:*` for every type), the values narrowed to
(`f`), the order (`sort`), the types alone (`view=types`), the record open (`id`, and `v` for a version) and a dimension
being built (`dim`) are the page's address, so a link, Back and a refresh land on the same view; a link naming
a partition (`partition`) makes it the title bar's. What was read is kept for a minute and reused (the kinds of a whole
partition for ten), so going back to a type or a page already read shows it at once; the refresh button reads again.

## The API

| Route | Scope | What it does |
| --- | --- | --- |
| `GET /api/v1/delivery/explorer/connection?partition=` | read | How the partition (else the workbench's, else the registry's default) is reached: `available`, `through` (the flow, `flow/interface` for a source's interface), `endpoint` as written, `route`, or `reason` when nothing reaches it. |
| `POST /api/v1/delivery/explorer/types?partition=` | operate | The count of the records a search finds, kind by kind. Body: `text`, `lucene`, `mentions`, `filters`. |
| `POST /api/v1/delivery/explorer/search?partition=` | operate | One page of the records a search finds. Body: `text`, `lucene`, or `mentions` (an id); `kind` (wildcards per segment); `filters` (`path`, `index` text, keyword, number, boolean or date, `value`; at most 12); `sort` (relevance, modified, created); `offset` and `limit` (1 to 200, inside the first 10,000); `facet` (`path`, `index`) to group by. |
| `POST /api/v1/delivery/explorer/fields?partition=` | operate | The properties the records of a `kind` hold, read from one of them. |
| `POST /api/v1/delivery/explorer/read?partition=` | operate | One record by `targetId` from the storage service, at its latest or at `version`, with its version list. |
| `POST /api/v1/delivery/explorer/validate?partition=` | operate | One record checked against the schema of its kind ([Validate](#validate)). Body: `targetId`, `version` (its latest when left out), `schema` (`osdu`, the default, for the Schema service's; `saved` for a saved template), `templateVersion` (with `saved`; the kind's newest when left out). Answers the `targetId`, the `version` checked, whether storage holds the record (`found`), its `kind`, the `schema` used (`kind`, `version`, `source` schema-service or template, the schema ids `read`, the references left `unresolved`, `notes`), the `verdict` as a record's history holds it, or why nothing was checked (`problem`), the template versions saved for the kind (`savedVersions`, newest first), and the `guidance`: one guide per problem and per part not checked, in the verdict's order (`path`, `rule`, the value `found`, the `advice`, and the key of what is `expected` there), the `expectations` by that key (`summary`, `title`, `description`, `types`, `required`, `patterns`, `allowed` and `allowedCount`, `formats`, the bounds, `entityTypes`, `properties`, `items`, `forms`, the schema's `examples`, `osduExample`), the data definitions' `example` quoted (`release`, `path`, `webUrl`), or the `exampleNote` saying why none is. |
| `POST /api/v1/delivery/flows/{pipelineId}/osdu/validate?interface=&partition=` | operate | The same check of one record, read through the flow's own route and credentials as a record page reads it (`interface` and `partition` naming the ledger, as every route of a flow's records does). Body and answer as `/explorer/validate`. A flow whose route keeps no record in storage (dspdm, etp) is refused with 409. |
| `POST /api/v1/delivery/explorer/validate-list?partition=` | operate | The records a search finds checked against their schemas ([Validate these records](#validate-these-records)). Body: `search` (as `/explorer/search` takes it; its page and grouping are not used), `max` (1 to 1,000, the default), `schema` (`osdu` or `saved`, each kind's newest). Answers `matched`, `asked`, `read`, the ids storage did not return (`notFound`), the counts `valid`, `invalid`, `unverified` and `notChecked`, the `rules` broken (`at`, `rule`, `records`, `problems`, an example's `exampleId`, `examplePath`, `exampleMessage`, `exampleValue`, what the schema takes there as `expected`, and the `advice`; at most 50), each record (`id`, `kind`, `outcome`, `problems`, `unverified`, its `first` problem), the `schemas` used, the kinds `unavailable` with why, `cut` when the search matches more than was read, and `notes`. |
| `POST /api/v1/delivery/explorer/referenced-by?partition=` | operate | The types whose schemas name records of a type ([Referenced by](#referenced-by)). Body: `type` (a type, `reference-data--UnitOfMeasure`, or a kind naming one), `refresh` (true to read the partition's schemas again first). Answers the `entityType`, the `state` (`reading`, `ready`, or `failed` with the `problem`), the reading answered from (`readUtc`, the schemas `listed`, the `kinds` read), the pass under way (`progress`: `startedUtc`, `listing`, `listed`, `toRead`, `read`, `failed`), the `types` and those naming any record of the type's group (`anyOfGroup`), each with its `entityType`, the `kinds` naming it (`kind`, `version`, `status`, `places` with `at` and `byPattern`, `morePlaces`, `partial`) and those that do not (`without`), the kinds not read (`unread`, each `kind` and `why`, and `unreadCount`), and `notes`. |
| `POST /api/v1/delivery/explorer/element-queries` | operate | The Lucene query that finds the records holding exactly an element of a record ([The query of an element](#the-query-of-an-element)). Body: `kind` (the record's), `path` (as the record inspector names it, a list's items by their place: `data.GeoContexts[1].GeoTypeID`), `section` (true for an object, a list or an item), `value` (a value's: a text, a number, a boolean or null), `values` (a section's, at most 48: each `path` and `value`). Answers the queries, the exact one first (`purpose` exact, words or exists, `query`, `says`), how the index holds the element (`field`, `reading`), the saved `template` read, why the query is a guess (`guess`), why none can be written (`problem`), and `notes`. Read from the saved templates alone. |
| `GET /api/v1/delivery/explorer/dimension/candidates?kind=` | operate | The keys the saved template of a `kind` (wildcards per segment) suggests for a dimension ([Building a dimension](#building-a-dimension)), the likeliest first: `path`, the entity types it `names`, whether it is `repeated` in a record, and the template's `title` and `description`; with the `template` read, or why nothing is suggested (`missing`). Read from the saved templates alone. |
| `POST /api/v1/delivery/explorer/dimension/keys?partition=` | operate | The commonest keys of a drafted dimension's path, which its example steps through. Body: `kind` (wildcards per segment), `query` (as long as a search's text), `path` (a property path, no filter). Answers the records the kind (and the query) holds, the keys with their records (up to 25, `moreKeys` when there are more), how the path is indexed (`keyFieldGuessed` when no saved template says, read as text), notes, and the service's words when it refused the query. |
| `POST /api/v1/delivery/explorer/dimension/compose?partition=` | operate | A draft written as the item a dimension flow lists, and checked. Body: `draft` (`name`, `description`, `kind`, `query`, `path`, `label` (paths), `unlabelled`, `attributes` (`name`, `steps` or `collect`), `clean` (`step`, and a replace's `pattern` and `with`), `keyColumn`, `valueColumn`, `countRecords`, `maxValues`), and `example`, a key to make into its row. Answers `yaml`, `item` (its lines and the line each part is on), `dimension` and `blueprint` once it loads, `table`, `issues` (`severity`, `message`, `target`, `code`), `valid` (it loads, with no error), and the `example` row or `exampleProblem`. |

Every read answers `200` with what it found; there is nothing to poll. A request the read would refuse (a kind that is
not one, a filter value no exact match carries, a page past the window, text with a character that cannot be printed,
an id that is not one) is refused with 400 before anything is read, and a partition no flow reaches with 409. A read
OSDU refuses or fails answers 502, and one with no answer within 60 seconds 504, each with the reason, redacted. A
compose answers `200` with whatever is wrong with the draft among its `issues`, and an example that cannot be made (no
flow reaches the partition, OSDU refuses) as its `exampleProblem`; only a missing draft, or a part longer than 4,096
characters or a list longer than 64 entries, is refused with 400. A check answers `200` with a record it could not
check and why (no schema of its kind, a template not saved); a `schema` other than `osdu` or `saved`, a template
version longer than 64 characters, or a `max` outside 1 to 1,000 is refused with 400. Referenced by answers `200` while
the partition's schemas are read, with where the reading stands, and when the reading failed, with why; a `type` that
names no type is refused with 400. The MCP server offers none of these routes:
it answers from metadata alone, and the explorer reads OSDU's data.
