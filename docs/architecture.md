# Architecture

A file-by-file tour of the codebase, oriented at someone modifying it.

---

## High-level shape

The add-in follows the standard Revit pattern:

```
PcfExportApp (IExternalApplication)     ← Revit entry, registers the ribbon + two ExternalEvent pairs
        │
        │  user clicks one of two ribbon buttons
        ▼
ExportCommand or AssignTagCommand       ← per-tool entry
        │
        ▼
Export flow                              Assign Tag flow
────────────────                        ────────────────
ExportDialog (modal)                    AssignTagDialog (modeless)
   ├── FabricationPartMapper                ├── SelectionFilter (picker)
   │       ├── PartTypeClassifier           └── ExternalEvent → write Tag param
   │       ├── ConnectorHelper
   │       ├── ServiceToSpecMapper
   │       └── SkeyResolver
   ├── TagValidationDialog (modal on missing / dup tags)
   └── PcfWriter                            → PCF text file on disk
```

The Export flow is synchronous: user clicks Export → walk selection →
map each part → validate tags → write file. `TagValidationDialog` opens
in the middle of the flow if any valve is missing / duplicated a tag,
and the export blocks until the user resolves them or cancels.

The Assign Tag flow is modeless: dialog opens and stays open while the
user pans / picks / types in the Revit view. Each Set-Tag / Pick-Next
action posts through an ExternalEvent so the write happens on Revit's
API thread inside a Transaction.

---

## File-by-file

### `src/PcfExportApp.cs`

`IExternalApplication`. Runs when Revit starts.

- Creates the **PCF Export** ribbon tab and **Export** panel.
- Adds two push buttons — **ITMs to PCF** and **Assign Tag** — with
  ToolTip / LongDescription text.
- Constructs two static `RevitEventHandler` + `ExternalEvent` pairs:
  - `ExportHandler` / `ExportEvent` — for the Export flow's dialogs.
  - `TagHandler` / `TagEvent` — for the Assign Tag dialog.
- Wraps the whole startup in `try/catch` so any failure surfaces as a
  `TaskDialog` instead of a silent no-load.
- `OnShutdown` is a no-op.

### `src/ExportCommand.cs`

`IExternalCommand` for **ITMs to PCF**.

- Reads the current selection, filters to `FabricationPart` category members.
- Opens `ExportDialog`. On OK, gathers the `ExportOptions` payload.
- Delegates the walk to `FabricationPartMapper.MapSelection`, which
  returns a list of `PcfComponent`.
- Runs tag validation: every valve component must have a non-empty,
  unique Tag. Missing / duplicate → opens `TagValidationDialog` to
  collect fixes. Loops until every tag is valid.
- Hands the `PcfComponent` list to `PcfWriter.Write` and closes with a
  summary dialog.

Also holds a small `FindParameter` static helper and inline
`InchesToFractionString` used by size formatting.

### `src/AssignTagCommand.cs`

`IExternalCommand` for **Assign Tag**.

- Constructs `AssignTagDialog` (modeless), stores the doc / UI-app
  references it needs, and calls `Show()`.
- Returns immediately — the dialog stays open until the user closes it.

### `src/Models/PcfComponent.cs`

The in-memory PCF component types: `PcfComponent`, `PcfEndPoint`,
plus component-specific record types (Pipe, Elbow, Tee, Reducer, Valve,
Olet, Weld, Cap, Coupling, Union, Flange, EndConnection, Support).

### `src/Models/ExportOptions.cs`

Serialisable payload from the Export dialog: output path, PCF version,
material overrides, tag / SKEY parameter names, include-supports flag,
insulation-emit flag, etc.

### `src/Models/SkeyCatalog.cs`

The full ISOGEN SKEY reference catalog (~280+ codes). Used by
`SkeyResolver` to look up canonical SKEYs and by
`FabricationPartMapper` to validate derivations.

### `src/Pcf/PcfWriter.cs`

The text emitter. Takes an `ExportOptions` + a list of `PcfComponent`
and writes a single `.pcf` file. Handles:

- Header block (PROJECT-IDENTIFIER, PIPELINE-REFERENCE, UNITS)
- Optional MATERIALS block
- One component block per part, with the correct coordinate lines
  (`CENTRE-POINT` / `END-POINT` / `BRANCH1-POINT`) and attribute lines
  (`ITEM-CODE`, `ITEM-DESCRIPTION`, `PIPING-SPEC`, `SKEY`, `TAG`, `WEIGHT`,
  `INSULATION-THICKNESS`, etc.)
- Correct 4-token bore lines on open ends
- Component-type-specific quirks (Olets emit no END-POINT lines; Reducers
  use CONCENTRIC / ECCENTRIC subtypes; Welds emit WF / WW field-vs-shop
  variants)

### `src/Pcf/SkeyResolver.cs`

Per-component SKEY derivation. Encapsulates the rule "SKEY = 2-letter
component prefix + 2-letter end suffix" plus the exceptions (Olets
need specific subtypes; Welds use WW / WF; Caps / Couplings /
End-Connections have their own resolvers).

### `src/Revit/FabricationPartMapper.cs`

The heart of the exporter. ~1200 lines. Big methods:

- **`MapSelection(doc, ids, opts)`** — public entry. Iterates the
  selection, dispatches per `PartTypeClassifier` classification.
- **`MapPipe` / `MapElbow` / `MapTee` / `MapReducer` / `MapOlet` /
  `MapValve` / `MapWeld` / `MapCap` / `MapCoupling` / `MapUnion` /
  `MapFlange` / `MapEndConnection` / `MapSupport`** — one per
  component type. Reads geometry via connectors, derives bore /
  size / end treatment, resolves SKEY, builds a `PcfComponent`.

Uses `ConnectorHelper` for connector geometry, `ServiceToSpecMapper`
for PIPING-SPEC lookups, `SkeyResolver` for SKEY derivation.

### `src/Revit/ConnectorHelper.cs`

Physical-connector analysis:

- `GetPhysicalConnectors(part)` — snapshots the `ConnectorManager.Connectors`
  enumeration to a stable list, filters to `ConnectorType.End`.
- Anti-parallel `BasisZ` searches to identify pipe end pairs vs
  curve connectors.
- End-point / branch-point extraction used by the mapper.

### `src/Revit/PartTypeClassifier.cs`

Classifies a `FabricationPart` into one of the PCF component types:
pipe (straight), elbow, tee, reducer, olet, valve, weld, cap, coupling,
union, flange, end-connection, support. Uses fab CID lookups plus
geometric heuristics. Also includes `StraightPipeCids` and
`StraightDuctCids` fallback sets.

### `src/Revit/ServiceToSpecMapper.cs`

Reads saved Service → PIPING-SPECIFICATION mappings from
ExtensibleStorage on `ProjectInformation`. `LoadMappings(doc)`
returns a `Dictionary<string,string>`; the exporter looks up each
part's `ServiceName` to get the SPEC string that goes into the PCF.

Schema GUID `22D348A8-B6B9-4A09-9E7C-E3D87398D29B`, schema name
`PcfExport_ServiceToSpec`. Unique to this add-in.

Note: no UI is shipped in this bundle for editing the mappings — the
add-in only reads what's already stored. Projects with no saved
mappings get an empty dictionary, and the exporter falls back to the
service name / empty SPEC string.

### `src/Revit/SelectionFilter.cs`

`ISelectionFilter` implementations used by the Export dialog's Pick
button. Filters to `FabricationPart` category members.

### `src/Revit/MapFileHelper.cs`

zlib MAP envelope decoder. Not used by the export path directly —
carried over as part of the fabrication-domain toolset. Safe to remove
if a future maintainer confirms it's dead here.

### `src/Revit/RevitEventHandler.cs`

Generic `IExternalEventHandler` that runs a `Action` delegate on Revit's
API thread. The modeless dialogs post work through it instead of
touching the doc from their WPF click handlers directly. Two instances
live in `PcfExportApp.cs` (one per dialog surface).

### `src/Revit/RibbonIconFactory.cs`

Generates ribbon icons at runtime from Segoe MDL2 Assets glyphs.

- `Tag(int size)` — U+E8EC (a tag) in TagAmber `#D68A00`.
- `Upload(int size)` — U+E898 (an upload cloud) in ForestGreen `#2E7D32`.

Both freeze the ImageSource for cross-thread safety. Generated once
per `OnStartup`.

### `src/UI/ExportDialog.xaml(.cs)`

Modal WPF dialog with the export options. Sections: Output, Header,
Materials, Identity Data, Insulation, Supports, plus a live output
preview showing component counts. On OK, returns an `ExportOptions`
payload.

### `src/UI/TagValidationDialog.xaml(.cs)`

Modal WPF dialog opened by `ExportCommand` when tag validation fails.
Walks one valve at a time, shows Tag input with a **Next** helper
button that auto-increments (`V-001 → V-002 → V-003`) using the last
entered tag's pattern. Blocks the export until every tag is valid.

### `src/UI/AssignTagDialog.xaml(.cs)`

Modeless WPF window for the Assign Tag command. Three workflows:

- **Single tag** — select one valve, type a value, Set Tag.
- **Auto-incrementing sequence** — prefix + start # + digits width,
  Pick Next Valve.
- **Highlight untagged** — selects every untagged Fabrication valve in
  the active view.

All Revit-touching operations route through
`PcfExportApp.TagHandler` / `PcfExportApp.TagEvent`.

---

## Data flow at export time

```
1. User selects fab parts in the Revit view
2. Clicks ITMs to PCF
3. ExportDialog opens; user reviews options; clicks Export → ExportOptions payload
4. ExportCommand.Execute
   → FabricationPartMapper.MapSelection(doc, selectedIds, opts)
       → for each part, PartTypeClassifier → dispatch to Map<Type>
           → ConnectorHelper reads geometry
           → ServiceToSpecMapper looks up SPEC
           → SkeyResolver derives SKEY
           → returns PcfComponent
   → list of PcfComponent
5. Tag validation
   → find valves with missing / duplicate Tag
   → if any, open TagValidationDialog until user fixes or cancels
6. PcfWriter.Write(components, opts, outputPath)
   → text file on disk
7. Summary TaskDialog
```

## Data flow at assign-tag time

```
1. User clicks Assign Tag
2. AssignTagCommand opens AssignTagDialog (modeless), returns immediately
3. User interacts with the dialog:
   → Single: type tag, click Set → TagEvent.Raise → write Tag param via Transaction
   → Sequence: click Pick Next → PromptForObject → write auto-incremented tag via Transaction
   → Highlight: TagEvent.Raise → SetElementIds(untagged)
4. Dialog stays open until user closes
```

---

## Adding a new tool to this add-in

Not the intended growth path — this bundle is scoped to the two
commands. But if you did:

1. Add a `YourToolCommand.cs` at `src/` with `IExternalCommand` +
   `[Transaction(TransactionMode.ReadOnly)]` (or Manual if it writes).
2. Register a `PushButtonData` in `PcfExportApp.OnStartup` and add it
   to the panel.
3. If it needs a modeless dialog, add its own `Handler` / `Event`
   static pair in `PcfExportApp` — don't share the existing ones.
