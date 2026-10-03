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
protocol, so both pages read a record the same way.

## Browsing

The page opens on a welcome that reads nothing from OSDU, so it shows at once however large the partition: the search
field, the records opened lately and the types browsed lately (both kept in the browser), what the field takes, and
**Browse types**. OSDU is read only once the reader asks: Browse types reads the types alone and waits for one to be
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
  arrow keys, Page Up and Down, Home and End move through the rows, and Enter opens one.
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

One field takes what the reader holds. Enter reads it, and a hint at the end of the field says what Enter will do:

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

## A record

A record opens in the record inspector the record pages use, filling the page, with the place it sits in leading its
location (`dev > master-data > Wellbore > NO 33/9-C-28 B > data > ...`), the way back to the list as it was left
before it:

- every view of the inspector: the full document, the system fields, access and legal, the content branch by branch
  as fields, tables or JSON, and the linked records, each opened after it on the trail;
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
| `GET /api/v1/delivery/explorer/dimension/candidates?kind=` | operate | The keys the saved template of a `kind` (wildcards per segment) suggests for a dimension ([Building a dimension](#building-a-dimension)), the likeliest first: `path`, the entity types it `names`, whether it is `repeated` in a record, and the template's `title` and `description`; with the `template` read, or why nothing is suggested (`missing`). Read from the saved templates alone. |
| `POST /api/v1/delivery/explorer/dimension/keys?partition=` | operate | The commonest keys of a drafted dimension's path, which its example steps through. Body: `kind` (wildcards per segment), `query` (as long as a search's text), `path` (a property path, no filter). Answers the records the kind (and the query) holds, the keys with their records (up to 25, `moreKeys` when there are more), how the path is indexed (`keyFieldGuessed` when no saved template says, read as text), notes, and the service's words when it refused the query. |
| `POST /api/v1/delivery/explorer/dimension/compose?partition=` | operate | A draft written as the item a dimension flow lists, and checked. Body: `draft` (`name`, `description`, `kind`, `query`, `path`, `label` (paths), `unlabelled`, `attributes` (`name`, `steps` or `collect`), `clean` (`step`, and a replace's `pattern` and `with`), `keyColumn`, `valueColumn`, `countRecords`, `maxValues`), and `example`, a key to make into its row. Answers `yaml`, `item` (its lines and the line each part is on), `dimension` and `blueprint` once it loads, `table`, `issues` (`severity`, `message`, `target`, `code`), `valid` (it loads, with no error), and the `example` row or `exampleProblem`. |

Every read answers `200` with what it found; there is nothing to poll. A request the read would refuse (a kind that is
not one, a filter value no exact match carries, a page past the window, text with a character that cannot be printed,
an id that is not one) is refused with 400 before anything is read, and a partition no flow reaches with 409. A read
OSDU refuses or fails answers 502, and one with no answer within 60 seconds 504, each with the reason, redacted. A
compose answers `200` with whatever is wrong with the draft among its `issues`, and an example that cannot be made (no
flow reaches the partition, OSDU refuses) as its `exampleProblem`; only a missing draft, or a part longer than 4,096
characters or a list longer than 64 entries, is refused with 400. The MCP server offers none of these routes:
it answers from metadata alone, and the explorer reads OSDU's data.
