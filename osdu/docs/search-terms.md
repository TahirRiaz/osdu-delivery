# Search terms

The people who search OSDU for delivered data know it by the columns of the systems it came from: a Recall well log by
its `wellbore_uwi`, its `log_source` and its curves' `curve_unit`. The records hold other names and often other values:
the log names its wellbore by a record id in `data.WellboreID`, its source after a `trim` in `data.Name`, its curves' units
as references to `UnitOfMeasure` records. The delivery flows say exactly how one becomes the other (the tables each reads,
and the mapping it renders them with), so the module reads them for the way back: a **search term** is a column of a
source table an active delivery flow reads, named as the source names it (`WellLog.wellbore_uwi`), with every route by
which a value of it reaches the record.

The explorer offers the terms beside the record's own properties ([explorer.md](explorer.md#searching-a-property)). A
value typed for a term is the source's own (`NO 34/10-A-30`); the control plane carries it to what the record holds the
way a delivery would, and asks OSDU's own search service, as it asks any condition. Nothing is searched in the ledger or
the cache: the mappings and the cache flows' declarations (metadata, never a cached value) only write the query, and
what it finds is what OSDU holds.

The **Search terms** page (`/delivery/search-terms`, in the OSDU navigation group) lists the terms and refines them:
renamed to what the people searching call them, left out of the explorer, searched through another of their routes, or
given a note.

## Where the terms come from

The terms come from the pipelines. Every active delivery flow (every interface of a source) reads a record table
(`source.record.object`, `OsduData.arc.WellLog`) and the tables of its child datasets (`source.datasets.curves.object`,
`OsduData.arc.WellLogCurve`), and renders them with a mapping; every column of those tables the mapping reads is a term.
Every repository sync writes the terms of the repository, in the transaction that writes its mappings, cache declarations
and interfaces, from the flows it parsed, the valid mappings their interfaces pin and the cached types its cache flows
declare (`DeliverySearchTermCatalog`). The control plane writes them again once when it starts, from its copies of the
flows, so a module upgraded since a repository's last sync describes its terms as the new version does without waiting
for the next sync, and a repository with no delivery flow any longer loses its terms.

For every column a mapping reads (`$from`, the columns an `$expr` reads, the columns of a `$lookup`'s or a `$search`'s
`findBy`, the columns of the dataset's key, under `$forEach` the child dataset's columns), the compiler
(`SearchTermCompiler`) follows the value to each place the mapping writes it, and says how it gets there. A term is
identified by the table the column is read from (its name with the brackets taken off, `OsduData.arc.WellLog`) and the
column: `osdudata.arc.welllog/wellbore_uwi`, in lower case as a database names them. Its id is a UUIDv5 of that text, so
a term is the same whichever pipeline reads the table, whichever mapping (and mapping version) renders it, whichever
repository holds them, and on every host. Its name, until a person gives it another, is the table's own name and the
column: `WellLog.wellbore_uwi`, `WellLogCurve.curve_unit`.

Two flows reading one table give one term. The Recall estate delivers its well logs twice, rendered by WellLog 1.4.0 and
by 1.5.0, each mapping under a source system of its own (`recall`, `recall-welllog-1.5.0`) so their records are records
of their own; both flows read `OsduData.arc.WellLog`, so `wellbore_uwi` is one term, its routes read by both mappings. A
route both versions write alike is one route listing both mappings, both kinds and both source systems, as the newest
writes it; where they write it differently, each version's route is listed, and the explorer searches the one of the
kind in view ([The route a term is searched by](#the-route-a-term-is-searched-by)). A term is kept per entity type it
reaches (a table rendered into two types is one term, with routes into each), and the flows that read it are listed
with it. The access list and the legal block are not routes: they say who may read a record, not what it holds.

A term no pipeline gives any longer is removed at the next sync. What a person made of it is kept apart and stays (see
[Refining a term](#refining-a-term)). A mapping that no longer compiles, a mapping reading a dataset its flow does not
declare, a cached type whose fields do not read, or a term whose key is longer than the table keeps, is left out with a
warning of the sync.

## Routes

A route is one way a column reaches the record: the template variable it fills (`osdu.data.WellboreID`), its path in
the record (`data.WellboreID`), the mapping node that fills it and its place in the mapping, the modifiers on the way,
and how a value becomes the value written:

| Route | The mapping | A search by the term |
| --- | --- | --- |
| **copy** | writes the value as it stands (`LogRun: { $from: log_run }`) | compares the property with the value typed |
| **steps** | puts the value through modifiers first (`trim`, `upper`, `split`, `replace` with a literal map, `date`, `number`, `ref`, `id`) | puts the value typed through the same modifiers, by the renderer's own code, then compares |
| **lookup** | finds a record of a cached type by the value, and writes its id or a property of it (`$lookup: UnitOfMeasure`) | finds those records in OSDU itself, then compares the property with what they hold |
| **search** | finds a record by searching the platform, and writes its id (`$search: Wellbore`) | runs the same search in OSDU, then compares the property with the ids found |
| **key** | makes the record's id from the dataset's key, of which the column is a part | makes the id from the value typed, as a delivery makes it, and compares the record's id |
| **expression** | computes the value in an expression (`$expr`) | none: an expression cannot be run backwards |

A **steps** route keeps something of the value, which decides what may be asked of it: its text (its words and its
start, the case changed alike for every value), its order (read as a number or a date, so a range of values is a range
of what is written), or only the value itself, compared whole. A route says why no value can be carried through it,
and is then not searchable: a value translated through a cached table (`replace: $cache.RecallUnits`; only a delivery
reads the cache), an id built from a cached table, from another column or from a flow parameter other than the
partition, or an expression.

A **lookup** route finds its records where the cached type comes from. The cache flow's declaration of the type names
the kind it captures and the fields it keeps by their paths in the record (`osdu.CacheDefinition`); a lookup's `findBy`
names those fields, so the explorer searches OSDU for the records of that kind whose properties hold the value, as the
type's records were captured from OSDU in the first place. A type whose rows are not OSDU records (a table, a
dictionary) gives no searchable route. A **search** route runs the mapping's own search (`searches:` names the kind
and pins the schema of the records found), each line of its `findBy` a clause. Of the records found, the first 50 are
compared: the ids, in the reference form a relationship holds (`dev:master-data--Wellbore:abc:`), or the property the
route reads of them. A value that finds more says so, and to narrow it.

A **key** route makes the record's id as a delivery makes it: a UUIDv5 over the system and the key's values, or the
value itself for a key taken as the id (`idFrom`). A key of one column makes one id. For a key of several columns the
other columns' values are needed too: each is read from the property of the record it fills, grouped by one search
over the records in view, and an id is made for every combination, the first 50 compared.

### How a route is compared

Whether a route can be searched, and how, is decided when the terms are read, against the saved templates
([mapping-templates.md](mapping-templates.md)): the property the route writes is indexed as the template the mapping
pins says (text, a keyword, a number, a boolean or a date, inside a nested list through it), and for a lookup or a
search, the properties the records are found by as the schema of those records says (the search's pinned schema, or
the newest saved template of the cached type's kind). A template not saved, or a property it does not index, makes the
route not searchable, with why. Saving a template makes the routes it describes searchable at the next read, with no
sync.

The conditions a route takes:

| Route | Conditions |
| --- | --- |
| copy, steps keeping text, to text | contains, is, is not, is one of, is none of, starts with (not inside a nested list), has a value, has no value |
| copy, steps keeping text, to a keyword | is, is not, is one of, is none of, starts with (not inside a nested list), has a value, has no value |
| copy, steps keeping order, to a number or a date | is in a range, is, is not, is one of, is none of, has a value, has no value |
| lookup, search | is, contains (when every property found by is text), starts with (when every one is text or a keyword, outside a nested list), is not, is one of, is none of, has a value, has no value |
| key | is, is not, is one of, is none of |
| any other | is, is not, is one of, is none of, has a value, has no value |

Whether a property holds a value is not asked inside a nested list, as for any property. A condition that only excludes
starts from every record, as for any property ([explorer.md](explorer.md#searching-a-property)).

### The route a term is searched by

A term reaches the record by as many routes as its mappings write it: Recall's `curve_unit` is a lookup of the unit
records the partition holds, and also a translation through the cached table `RecallUnits` that cannot be searched. A
term is searched through the route a person picked while it can be searched, else the plainest that can: a copy before
steps, before a lookup or a search, before a key; the record's content before its tags; a value of its own before one
inside a list; then the shorter path. A term none of whose routes can be searched is listed with why, and not offered.

Each route lists the kinds it fills, one per version of the mapping that writes it. With one kind in view
(`osdu:wks:work-product-component--WellLog:1.5.0`), the routes that fill it come first, so a column the versions write
into different properties is searched where the version in view writes it, and its values are carried through that
version's mapping; a route picked holds for the kinds it fills. With every version of the type in view, the choice is
made among all the routes. A key's record ids are made as each source system's delivery makes them, so a log id finds
the log however many versions deliver it.

## Refining a term

What a person makes of a term is kept apart from the terms a sync writes, in `osdu.SearchTermRefinement`, by the term's
id: it holds across every sync, every pipeline and every mapping version that keeps the column, and applies again if a
term that was gone comes back. A refinement whose term no pipeline gives any longer is listed as **No longer found** until
it is removed.

Terms were first keyed by their mapping's source system, so one table read by two flows under two systems gave two
terms. Each sync moves what was made of such a term to the term its column is now, the table's: a name, a note, leaving
it out, and a route picked where the term still has it. Of two made for what is now one term, the newer moves; the other
stays, listed as **No longer found**, for a person to remove. Nothing else is written: the terms themselves are a read
model the control plane writes again from the pipelines.

- **Name**: the name the explorer offers the term by (at most 100 characters, on one line, unique among the terms of
  its entity type in any case). Empty, or the column's own label, is the column's label: `WellLogCurve.curve_unit`.
- **Note**: what the term means to the people searching by it (at most 1,000 characters), on hover wherever the term
  is offered.
- **Offered in the explorer**: off, the term is left out. It stays listed here and is never offered; a condition on it
  that a link still carries is refused with why.
- **Searched as**: one of the routes that can be searched. Left to the term, it is searched by the plainest one.

A refinement that keeps none of these is removed, and **Reset** removes it at once: the term is searched as its
pipelines give it again. Every change records who made it and when.

### The Search terms page

The page lists the terms of one entity type (picked at its head, each with how many terms it has), found by any part of
a name, a column or a path, and shown by state: **Every term**, **Searched**, **Left out**, **Not searchable** and **No
longer found**, each with its count. A row says the term's name (a note on hover), the source column in full (the table's
whole name and the column, cut from its start when long, so the column stays in view), the property it is searched in
with how it gets there (`steps`, `lookup`, `search`), its state (why it cannot be searched on hover) and when it was last
changed. The grid fits the page and scrolls in place. A row opens the term in a panel: its name and note, whether the
explorer offers it, every route with how it reaches the record (its steps, the records a lookup or a search finds and by
what) and the versions of the type that write it so, why one cannot be searched, the mappings and flows that read it,
and who changed it last. **Search WellLog** opens the explorer on the type.

Reading the terms takes the read scope; refining one takes the author scope.

## Searching by a term

In the explorer, for a type or a kind picked, every term offered is listed first in the condition editor, as **Source
columns**, by its name with the property it is searched in, found by any part of its name, its column, its table or
that path; the record's properties follow as ever. The terms are read for the kind in view, so each is searched through
its route for that kind. A term picked takes the conditions its route allows, the likeliest
first, and says that its value is typed as the source holds it. The values listed under it are those of the property
the route writes, for a copy or steps that keep the text, or those of the property its records are found by, for a
lookup or a search (`Values of Wellbore FacilityName`): what the source column holds, in the records OSDU has. A key
lists none.

A condition on a term is a chip that names the term (`Wellbore UWI is NO 34/10-A-30`), marked as a source column, with
the column, the property and the route on hover; it opens in the term's editor again. The property it compares is a
column of the grid, its header naming the columns searched in it on hover. The search field searches one column when it
is picked at the field's start (**In Wellbore UWI**): as a value is typed, the values listed under the field are those of
the records the column's route finds (the wellbores' names), and a value searched there replaces the column's condition.
With no choice made, the terms searched in lately for the type are offered under the properties as text is typed
(`Wellbore UWI is NO 34/10-A-12`), and **Pick another property or source column** opens the field's choice with the
text kept. Each condition names its term by id in
the address (`st`), so a link brings it back. A term no longer offered (left out, or no mapping reads it) is marked on
its chip, and the search says why it was refused.

The control plane resolves every condition naming a term before the search is checked or run, in the partition the
search reads (`SearchTermDirectory.ResolveAsync`): the term, its route for the kind searched, the mapping (the version
that renders that kind) and the template it pins, and each value carried through the mapping with the partition as the
flow's parameter. A search of many types takes the term's one type; a term that reaches several asks for one of them. A copy or steps become a condition on the
property, as any is. A lookup, a search or a key becomes a condition read through other records first
(`ExplorerVia`): the explorer runs the search of the records found, or of the key's other values, through the same
connection, compares with what it found, and says so in the notes of the answer (`Wellbore UWI is NO 34/10-A-30: 1
Wellbore record found by data.FacilityName.keyword:"NO 34/10-A-30" ..., compared by id with data.WellboreID`). A value
that finds nothing finds no record (or, for a condition that excludes, leaves every record). A value the mapping cannot
carry (a word where the mapping reads a number, an empty value) is refused with why, before anything is read.

## Tables

| Table | What it holds |
| --- | --- |
| `osdu.SearchTerm` | One row per repository, term and entity type: the term's id (`TermId`) and key (`TermKey`, the table as `Source`, `Column`), the entity type its routes fill (`EntityType`), its routes as JSON (`RoutesJson`: each with the kinds, mappings and source systems that read it), the active delivery flows that read its table (`FlowsJson`), and when the sync first and last found it. Unique on `(RepoId, TermId, EntityType)`; indexed on `(EntityType, TermId)`, which the explorer and the page read by. Written by the sync and the control plane's start alone. |
| `osdu.SearchTermRefinement` | One row per term a person refined, keyed by `TermId`: the `TermKey` and `EntityType` it was made for (so it is listed when no sync gives the term), `Name`, `Note`, `Excluded`, `Route`, `UpdatedBy` and `UpdatedUtc`. Indexed on `(EntityType, Name)`. Never written by a sync. |

The tables came with `20261008170839_SearchTerms` (module version 1.32.0), in the `osdu` schema
([ledger.md](ledger.md#osdusearchterm-and-osdusearchtermrefinement-search-terms)); `20261008204608_SearchTermSources`
(module version 1.33.0) keyed the terms by their table, emptying the read model for the control plane to write again and
keeping every refinement.

## The API

| Route | Scope | What it does |
| --- | --- | --- |
| `GET /api/v1/delivery/search-terms?entityType=&kind=&orphans=` | read | The terms of an entity type, or of the one a kind or kind pattern names (`*:*:work-product-component--WellLog:*`; a pattern of many types has none), or of every type, a term once per type it reaches; named by a kind, each is searched through its route for that kind. With `orphans=true`, the refinements whose terms no pipeline gives any longer too. Each term: `id`, `key`, `source` (the table), `table`, `entityType`, `column`, `columnLabel` (`WellLog.wellbore_uwi`), `systems`, `name`, `renamed`, `excluded`, `note`, `pickedRoute`, `route` (the one searched by), `routes` (each `id`, `entityType`, `kinds`, `dataset`, `target`, `path`, `kind`, `how`, `steps`, `mappings`, `location`, `find`, `keyColumns`, `when`, `description`, `index`, `nested`, `conditions`, `problem`), `flows`, `mappings`, `problem`, `suggest` (where its values are listed from: `kind`, `path`, `index`, `nested`), `orphan`, `updatedBy`, `updatedUtc`. |
| `GET /api/v1/delivery/search-terms/entity-types` | read | The entity types terms are extracted for, each with how many terms it has. |
| `GET /api/v1/delivery/search-terms/{termId}?entityType=` | read | One term as it reaches `entityType` (the first type it reaches when left out), or its refinement while no pipeline gives it; 404 for neither. |
| `PUT /api/v1/delivery/search-terms/{termId}?entityType=` | author | Refines a term, as shown for `entityType`. Body: `name`, `excluded`, `route` (a route's `id`), `note`. A body that keeps nothing removes the refinement. A name another term of the type has, a route the term does not have or that cannot be searched, or a name or note too long is refused with 400; a term no pipeline gives with 400 too, since only its refinement can be removed. |
| `DELETE /api/v1/delivery/search-terms/{termId}/refinement` | author | Removes what people made of the term: 204, or 404 when there was nothing. |
| `POST /api/v1/delivery/explorer/search`, `/explorer/types`, `/explorer/validate-list` | operate | A condition of `filters` names a term by `term` (its id) in place of a property: its `condition`, `value`, `values` and `to` are the source's own. The `path` and `index` it carries are the property the route compares, which the page shows as a column; the control plane resolves the condition from the term, through its route for the search's `kind`. |

The MCP server offers none of these routes.
