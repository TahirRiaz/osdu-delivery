# Editor key census

The files here describe every key of the documents OSDU Delivery adds, in the key census format SQLFlow's editor
tooling reads (`sqlflow/tools/README.md`, Module census files). They drive hover documentation, colouring and key
checks for these documents in the GUI's YAML editor, and in the language server when its client names this folder in
`initializationOptions.censusDirectories`.

| File | Describes |
| --- | --- |
| `keys.delivery.json` | A delivery flow, `flowType: delivery`, in the single form and as a source with interfaces. |
| `keys.retrieval.json` | A retrieval flow, `flowType: retrieval`. |
| `keys.cache.json` | A cache flow, `flowType: cache`: OSDU types, ingestion table types and dictionary types. |
| `keys.mapping.json` | A mapping, `documentType: mapping`, with every modifier and the settings a replace takes. |
| `keys.dictionary.json` | A dictionary, `documentType: dictionary`. |

Every loader of the module refuses a key it does not know, so each file says `strictKeys: true`, and the flow kinds
take the platform envelope (`includeEnvelope: true`) as SQLFlow documents it. The descriptions are written from
[documents.md](../documents.md) and the loaders.

`EditorCensusTests` checks the files against the code: every key a flow kind's strict loader accepts is documented and
nothing is documented that it would refuse (read off the YAML models by reflection), and the mapping's modifiers and the
dictionary's keys match the names `MappingMapper` and `DictionaryMapper` accept. A key added to a loader without its
entry here fails that suite, so the editor never stops documenting a key an author can write.

The GUI module registers the files (`osdu/gui/src/module.tsx`), and each one loads when the editor first starts.
