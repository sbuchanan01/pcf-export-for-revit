# Developer guide

How to set up your development environment, build, debug, and ship changes.

---

## Prerequisites

- **Windows 10/11** — Revit only runs on Windows.
- **Revit 2025 or 2026** — full install. The add-in references DLLs from your Revit install folder (default `C:\Program Files\Autodesk\Revit <version>\`). Build defaults to Revit 2026; pass `-p:RevitVersion=2025` to target 2025 instead.
- **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download). The csproj targets `net8.0-windows`.
- **A C# IDE** (any will do):
  - Visual Studio 2022 / 2026 Community — open `src/PcfExport.csproj`.
  - JetBrains Rider — open the same file.
  - VS Code + C# Dev Kit — same.
  - Claude Code — open the repo root; the included `CLAUDE.md` orients it.

---

## Clone and build

```powershell
git clone https://github.com/sbuchanan01/pcf-export-for-revit.git
cd pcf-export-for-revit/src
dotnet build -c Debug
```

Successful output ends with:

```
Build succeeded.
    0 Error(s)
Time Elapsed 00:00:0X.XX
```

(Some `MSB3277` warnings about RevitAPIUI re-references are normal and harmless.)

Debug builds auto-deploy to `%APPDATA%\Autodesk\Revit\Addins\<version>\` via the `DeployToRevitAddins` MSBuild target — the `<version>` matches whatever `-p:RevitVersion=...` you passed (default 2026). **If Revit is open, the DLL is locked** — the copy step is skipped (the build itself still succeeds).

If your Revit is installed somewhere non-default:

```powershell
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

To build for **both** Revit 2025 and 2026 in one go (typical for releases):

```powershell
dotnet build -c Release -p:RevitVersion=2025
dotnet build -c Release -p:RevitVersion=2026
```

Outputs land in `bin/Release-Revit2025/` and `bin/Release-Revit2026/` respectively.

> **Property syntax.** Use `-p:Name=Value` (single dash). The `/p:Name=Value` form (forward slash) is treated as a project-file argument by `dotnet build` and doesn't propagate to MSBuild.

---

## Debug-and-iterate cycle

The typical inner loop:

1. **Close Revit.** This releases the DLL lock so the post-build copy can land.
2. Make code changes in your IDE.
3. `dotnet build -c Debug` (or hit Build in your IDE).
4. **Open Revit**, open a model that contains Fabrication parts.
5. Click **ITMs to PCF** (or **Assign Tag**) to exercise your change.
6. Loop.

If you want to **attach a debugger**, the standard Revit add-in debug workflow is:

1. Open the csproj in Visual Studio.
2. Open **Properties** on the project → Debug → "Launch Profile" → set the executable to your Revit binary (`C:\Program Files\Autodesk\Revit <version>\Revit.exe`).
3. Hit F5 — Visual Studio starts Revit with the debugger attached.
4. Set breakpoints in source; they hit when the relevant code path runs in Revit.

For Rider, it's the same idea via Run/Debug configurations → .NET Executable.

---

## Code layout

See [architecture.md](architecture.md) for a full file-by-file tour. The short version:

- **`src/PcfExportApp.cs`** — Revit's entry point. Registers the ribbon tab + two push buttons and the ExternalEvent pair that the Assign Tag modeless dialog uses to call back into the Revit API thread.
- **`src/ExportCommand.cs`** — the ITMs-to-PCF command. Opens the export options dialog, runs tag validation, walks the selected fab parts, hands off to the mapper stack, writes the PCF file.
- **`src/AssignTagCommand.cs`** — the Assign Tag command. Opens the modeless dialog and wires up the pick / write flow.
- **`src/Revit/FabricationPartMapper.cs`** — core translation from `FabricationPart` → PCF component. Handles SKEY derivation, end treatments, bore / size, centre-of-gravity.
- **`src/Pcf/PcfWriter.cs`** — the actual PCF text emitter.
- **`src/UI/`** — WPF dialogs: `ExportDialog`, `AssignTagDialog`, `TagValidationDialog`.

---

## Making changes safely

A few rules of thumb.

### Every command is transaction-scoped

`ExportCommand` runs in a read-only transaction (it reads the model, writes a file — no model mutation). `AssignTagCommand` opens a modeless dialog and writes Tag values through a separate transaction started inside the ExternalEvent handler. Don't try to short-circuit the ExternalEvent — Revit's API is single-threaded, and touching the doc from a WPF button-click handler crashes Revit.

### The modeless dialog pattern

`AssignTagDialog` is a modeless WPF window. It calls back into Revit via the static `PcfExportApp.TagHandler` + `PcfExportApp.TagEvent`. Pattern:

```csharp
PcfExportApp.TagHandler.Action = () => {
    // runs on Revit's API thread inside a Transaction
    using var tx = new Transaction(doc, "Assign Tag");
    tx.Start();
    // ... do the write ...
    tx.Commit();
};
PcfExportApp.TagEvent.Raise();
```

Keep the action delegate small and self-contained. Don't capture the WPF dialog's UI elements — they may not exist by the time the callback fires.

### Snapshot `ConnectorManager.Connectors` before iterating

The lazy enumeration isn't stable across calls — converting to a list once avoids surprises:

```csharp
var conns = part.ConnectorManager.Connectors.Cast<Connector>().ToList();
```

### Don't touch the SKEY catalog casually

`Revit/PartTypeClassifier.cs` (or equivalent) holds the mapping from fabrication part properties → ISOGEN SKEY code. Every entry has been validated against real Plant 3D / ISOGEN behaviour. If you're tempted to change a SKEY, double-check against the ISOGEN SKEY reference documentation first.

### End-connection quirks

Open-ended pipes (`END-POSITION-OPEN`) need a 4th bore token in the PCF or Plant 3D reads Size=0. Olets emit `CENTRE-POINT` + `BRANCH1-POINT` only (no `END-POINT` lines). Reducers use `REDUCER-CONCENTRIC` / `REDUCER-ECCENTRIC` with a `CENTRE-POINT`, not a bare `REDUCER`. These are load-bearing in the exporter — the tests around them exist for good reason.

---

## Ship a new release

For binary distribution:

### 1. Bump the version

Update `<Version>` in `src/PcfExport.csproj`.

### 2. Release build (both Revit versions)

```powershell
cd src
dotnet build -c Release -p:RevitVersion=2025
dotnet build -c Release -p:RevitVersion=2026
```

### 3. Stage + package

For each Revit version, make a ZIP containing:
- `PcfExport.dll` (from `src/bin/Release-Revit<version>/`)
- `PcfExport.addin` (from `src/`)
- `LICENSE` (from repo root)
- `README.md` (from repo root)

Name them `PcfExport-Revit2025-v{version}.zip` and `PcfExport-Revit2026-v{version}.zip`.

### 4. Tag and push

```powershell
git tag v{version}
git push origin v{version}
```

### 5. Create the GitHub Release

```powershell
gh release create v{version} `
  releases/PcfExport-Revit2025-v{version}.zip `
  releases/PcfExport-Revit2026-v{version}.zip `
  --title "v{version}" `
  --notes "Release notes..."
```

---

## Code style

- **No comments unless the WHY is non-obvious.** Identifier names should carry the WHAT.
- **`/// <summary>` XML docs** on public types and members that aren't self-explanatory.

---

## Reporting issues

[File an issue](https://github.com/sbuchanan01/pcf-export-for-revit/issues) with:
- Revit version + build number (Help → About Revit IDE)
- What you did, what you expected, what happened
- Stack trace if there was a TaskDialog
- A minimal sample model if the bug is data-dependent
- The generated PCF file if the issue is about export output
