# PCF Export for Revit — Claude Code orientation

A standalone Revit add-in (builds for Revit 2025 and Revit 2026 from one
source tree via `-p:RevitVersion=...`) that exports Autodesk MEP
Fabrication parts to the PCF (Piping Component File) format used by
ISOGEN, Plant 3D, and other downstream piping tools. Ships with a
companion Assign Tag command for interactively tagging fabrication
valves before export.

## Project layout

```
src/
├── PcfExport.csproj                    ← .NET 8 + Revit refs (RevitVersion-parameterized, defaults 2026), AfterBuild deploy target
├── PcfExport.addin                     ← Revit add-in manifest (drops into Addins\<version>\ matching build)
├── PcfExportApp.cs                     ← IExternalApplication: ribbon tab + 2 buttons + ExternalEvent pairs
├── ExportCommand.cs                    ← IExternalCommand: ITMs to PCF (uses ExportDialog, TagValidationDialog)
├── AssignTagCommand.cs                 ← IExternalCommand: opens the modeless Assign Tag dialog
├── Models/
│   ├── PcfComponent.cs                 ← in-memory PCF component record types
│   ├── ExportOptions.cs                ← Export dialog's option payload
│   └── SkeyCatalog.cs                  ← ISOGEN SKEY reference catalog (~280+ codes)
├── Pcf/
│   ├── PcfWriter.cs                    ← Emits the PCF text file from PcfComponent list
│   └── SkeyResolver.cs                 ← Per-component SKEY derivation
├── Revit/
│   ├── FabricationPartMapper.cs        ← Core: FabricationPart → PcfComponent translation
│   ├── ConnectorHelper.cs              ← GetPhysicalConnectors, end-point/branch-point analysis
│   ├── PartTypeClassifier.cs           ← PCF component-type detection (straight vs elbow vs olet vs ...)
│   ├── ServiceToSpecMapper.cs          ← Reads saved Service→PIPING-SPECIFICATION mappings from ExtensibleStorage
│   ├── SelectionFilter.cs              ← ISelectionFilter for the Export dialog's picker
│   ├── MapFileHelper.cs                ← zlib MAP envelope decoder (unused in export path, kept for completeness)
│   ├── RevitEventHandler.cs            ← Generic IExternalEventHandler for modeless dialogs
│   └── RibbonIconFactory.cs            ← Tag + Upload ribbon glyphs (Segoe MDL2)
└── UI/
    ├── ExportDialog.xaml(.cs)          ← Modal WPF: export options
    ├── AssignTagDialog.xaml(.cs)       ← Modeless WPF: interactive valve tagging
    └── TagValidationDialog.xaml(.cs)   ← Modal WPF: fix missing / duplicate tags before export

docs/                                   ← User + developer documentation
releases/                               ← Per-version release zips (git-ignored, published via GH Releases)
```

## Build and deploy

```
cd src
dotnet build -c Debug                       # default = Revit 2026
dotnet build -c Debug -p:RevitVersion=2025  # Revit 2025
```

Debug builds auto-deploy `PcfExport.dll` + `PcfExport.addin` to
`%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\`. Output goes into
`bin/Debug-Revit<RevitVersion>/` so the two builds don't overwrite
each other. A `REVIT<version>` compile-time symbol (e.g. `REVIT2026`)
is also defined for any source that needs to branch on the API surface.

If Revit is open, the DLL is locked — close Revit and rebuild, or skip
the deploy step and copy manually.

Override the Revit install path at the command line if your install
isn't the default:

```
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

Note: `dotnet build` requires the `-p:` syntax (single dash) for
property overrides. The `/p:` (forward slash) form is treated as a
project-file argument and doesn't propagate.

## Two ExternalEvent pairs in the app class

`PcfExportApp.cs` declares two static `RevitEventHandler` +
`ExternalEvent` pairs, one per modeless surface:

- `ExportHandler` / `ExportEvent` — used by the Export flow's dialogs
  (`ExportDialog`, `TagValidationDialog`) to write back into the Revit
  document from WPF button-click handlers.
- `TagHandler` / `TagEvent` — used by `AssignTagDialog` for its "pick
  next valve, write its Tag" loop.

Keep the two pairs separate — collapsing them into one causes action
collisions when both dialogs are (theoretically) open at once.

## Critical Revit API gotchas

These have bitten the original author multiple times.

- **`ConnectorManager.Connectors` enumeration is unstable across calls.**
  Snapshot to a list before iterating multiple times.
- **`Connector` wrappers are never `ReferenceEquals` between calls** — even
  for the "same" physical connector. Compare by Origin (with tolerance) or
  by `ConnectorManager.Owner.Id` + index.
- **`Parameter.Set` in a modeless dialog's button-click** — never do it
  directly. Route through the ExternalEvent so it runs on Revit's API
  thread inside a Transaction. See the AssignTagDialog code-behind for
  the working pattern.
- **`WPF Topmost=True` hides `MessageBox.Show`** — pass the dialog itself
  as owner (`MessageBox.Show(this, ...)`) when the modeless dialog is
  Topmost, otherwise the message box gets hidden behind Revit.
- **Duplicate Tag validation must guard hard in `Export_Click`** — never
  rely solely on the button being disabled by binding. HashSet-based
  detection inside the click handler is the belt-and-suspenders.

## PCF format gotchas (encoded in FabricationPartMapper / PcfWriter)

Every entry here has been validated against real Plant 3D behaviour.
Don't casually change:

- **Open-ended pipes** — `END-POSITION-OPEN` / `END-CONNECTION-*` lines
  need a 4th bore token or Plant 3D reads Size=0.
- **Olets** — emit `CENTRE-POINT` + `BRANCH1-POINT` only. No
  `END-POINT` lines. Olet is a side-tap, NOT a pipe splitter — main run
  continues past.
- **Reducers** — `REDUCER-CONCENTRIC` / `REDUCER-ECCENTRIC` with
  `CENTRE-POINT`, not bare `REDUCER`.
- **Olet SKEYs** — Plant 3D rejects the generic `OL??` prefixes. Emit
  specific subtypes (`WT`/`SK`/`TH`/`SW`/`LA`) inferred from the end
  connection.
- **SKEY derivation** — `SKEY = 2-letter component prefix + 2-letter
  end suffix`. Never use aliases directly.

## ExtensibleStorage schema

- `ServiceToSpecMapper` schema GUID `22D348A8-B6B9-4A09-9E7C-E3D87398D29B`,
  schema name `PcfExport_ServiceToSpec`. Fresh values — do NOT reuse
  elsewhere.

If you add a new per-project setting, **generate a fresh GUID** for it.
Two schemas with the same GUID collide on the same project.
