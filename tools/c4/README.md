# C4 diagrams from one model

`render.py` draws a C4 architecture model, one YAML file, into one SVG per view, a Markdown page per view and a
PowerPoint deck. Every element is described once in the model and every view that places it draws the same thing, so a
system context, its containers, their components, the dynamic views and the deployment cannot disagree.

The product's model is [osdu/docs/reference/architecture/c4-model.yaml](../../osdu/docs/reference/architecture/c4-model.yaml),
and its pages start at [c4-model.md](../../osdu/docs/reference/architecture/c4-model.md). An estate repository can keep a
model of its own and render it with this script.

```bash
python tools/c4/render.py osdu/docs/reference/architecture/c4-model.yaml            # SVGs, pages and the deck
python tools/c4/render.py osdu/docs/reference/architecture/c4-model.yaml --no-pptx  # without the deck
python tools/c4/render.py osdu/docs/reference/architecture/c4-model.yaml --check    # exit 1 when an SVG or page is stale
python tools/c4/render.py osdu/docs/reference/architecture/c4-model.yaml --strict   # fail on any layout warning
```

It needs Python 3.11 or later with PyYAML, Pillow and python-pptx. Text is measured with Segoe UI, the font both
outputs name, so render on Windows (or a machine with Segoe UI installed) to reproduce the committed files exactly;
elsewhere it measures with Arial or DejaVu Sans and wraps a little differently. After rendering a reference page, rebuild
the manifest and run the MCP server's tests ([osdu/docs/reference/README.md](../../osdu/docs/reference/README.md#maintenance)).

## The model

| Key | What it holds |
| --- | --- |
| `elements` | Every person, software system, container, component and infrastructure node, by id: `kind` (`person`, `system`, `external`, `container`, `component`, `infra`), `name`, `tech`, `description` (what the diagrams show, two or three lines), `brief` (one line, for dense views), `detail` (the full text, for the pages and the speaker notes), `shape` (`box`, `cylinder`, `folder`, `browser`, `terminal`), `icon`, `tags` (`ai` marks what calls or serves a model), `zoom` (the view of its inside) and `ref` (the page that documents it) |
| `views` | Each view: its `tag`, `title` (the diagram's type and scope, as C4 asks), `description`, the `page` frontmatter, the `elements` it places, its `boundaries` and its `relationships`. `up` names the view it zooms out to |

A view places each element by its centre on a 1600 x 900 canvas, `[x, y]` or `[x, y, width, height]`, or as
`{at, size, desc, badge, brief}` to give it this view's own text or badge. `compact: true` draws cards (the icon beside
left-aligned text, in smaller type) for dense views, and `brief` lists the elements that show their one-line text.

A relationship names `from`, `to`, `label` (what it does) and `tech` (how). It routes orthogonally on its own: straight
when the two boxes face each other, otherwise an L or Z. `exit` and `entry` (`[side, offset]`), `route` (`hv`, `vh`,
`hvh`, `vhv`), `mid` and `via` (waypoints) steer it; `shift` moves a straight line sideways; `seg`, `t`, `dx` and `dy` place
its label; `width` wraps the label. In a dynamic view, `n` numbers the step and `explain` is the sentence its page and
notes give it. `dashed: true` draws what is optional or on request.

Every run checks each view and prints a warning for text that overflows its box, an element outside the drawing area,
two elements that overlap, a line that crosses a box or another line, and a label that covers a box, a line or another
label. Fix every warning before committing: the diagrams are only as readable as their worst crossing.

## Notation

Every diagram follows the [C4 notation checklist](https://c4model.com/diagrams/checklist): a title naming the diagram's
type and scope; every element with its name, `[type: technology]` and what it does; every line labelled with what it
does and its technology; and a key under the diagram for every colour, shape, icon, border and line style it uses. The
deck has one slide a view with the same geometry in native, editable shapes; an element with a view of its inside links
to that slide, and every slide links back to the map of views.

## Icons

| Folder | Source | Terms |
| --- | --- | --- |
| `icons/lucide` | [Lucide](https://lucide.dev), taken from the `lucide-react` the GUI installs | ISC, [icons/lucide/LICENSE](icons/lucide/LICENSE) |
| `icons/azure` | Microsoft's [Azure architecture icons](https://learn.microsoft.com/azure/architecture/icons/) (V23) | Microsoft permits their use in architecture diagrams and documentation, [icons/azure/NOTICE.md](icons/azure/NOTICE.md) |
| `icons/png` | A 96 px PNG of each, the fallback a deck shows where a viewer cannot draw the SVG it carries | as their source |

A model names an icon as `lucide:<name>` or `azure:<name>`, the file `icons/<family>/<name>.svg`. To add one, put its SVG
there (a Lucide icon as a 24 x 24 stroke icon drawn in `currentColor`) and draw its PNG:

```bash
node tools/c4/rasterize-icons.mjs
```

which uses the Playwright the GUI installs (`npm ci` in `osdu/gui`).
