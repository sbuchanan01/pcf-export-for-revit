# PCF Export for Revit

A Revit add-in (builds for **Revit 2025** and **Revit 2026**) that exports
selected **Autodesk MEP Fabrication parts** (ITMs) to the **PCF** (Piping
Component File) format used by ISOGEN, Plant 3D and other downstream
piping tools. Ships with a companion **Assign Tag** command for
interactively assigning Tag values to fabrication valves before export.

The exporter walks pipes, fittings, valves, supports and welds on the
selected fabrication parts, derives the correct ISOGEN SKEY for each
component, and writes a PCF file that downstream tools can consume
directly. The Assign Tag command opens a modeless dialog so you can
tag valves one-at-a-time (with auto-incrementing numbering) or all at
once, without leaving Revit.

---

## ⚠ Disclaimer

This code is provided by Autodesk for evaluation purposes only, as an example
of what is possible with the Autodesk platform and APIs. **THIS CODE IS NOT
INTENDED FOR USE IN PRODUCTION.** Autodesk makes no representations,
warranties, or commitments about the code. This code is not fully tested
and may include errors or faults that may cause total data loss or system
failure. No further updates to this tool are promised or implied — the
version published here may be the last, and may never be revised after the
posting date.

The MIT license applies to the source — see [LICENSE](LICENSE) — but the
evaluation-only nature above takes precedence over any "use however you
like" reading of the MIT terms.

---

## What it does

- **Export selected Fabrication parts to a PCF file** — pipes, elbows,
  tees, reducers, olets, valves, welds, caps, couplings, unions, flanges,
  end connections, and pipe supports. Correct ISOGEN SKEY per component,
  correct end treatments (BW / SW / TH / PL / FL / GR), correct bore /
  size / thickness metadata, correct centre-of-gravity for iso balancing.
- **Configurable per-run** — pick the output path, target PCF version,
  material overrides, tag / SKEY parameter mappings, insulation handling,
  and whether to include supports.
- **Valve tag validation** — before writing the PCF, the exporter checks
  that every valve has a unique Tag value. Missing / duplicate tags open
  the tag-validation dialog so you can fix them inline (with a **Next**
  button that auto-increments numbers using the pattern of the prior
  entry) before continuing.
- **Assign Tag command** — separate ribbon button for interactively
  tagging fabrication valves. Modeless dialog: tag one at a time, pick
  the next valve to auto-increment, or highlight every untagged valve
  in the active view.

---

## Install (no compiling required)

1. **Download the ZIP that matches your Revit version** from
   <https://github.com/sbuchanan01/pcf-export-for-revit/releases>:
   - `PcfExport-Revit2025-v1.0.0.zip` for Revit 2025
   - `PcfExport-Revit2026-v1.0.0.zip` for Revit 2026
2. Extract `PcfExport.dll` and `PcfExport.addin`.
3. Drop **both files** into your version-matched Revit add-ins folder:
   - Revit 2025 → `%APPDATA%\Autodesk\Revit\Addins\2025\`
   - Revit 2026 → `%APPDATA%\Autodesk\Revit\Addins\2026\`

   (paste either path into File Explorer's address bar — it expands to
   your user folder.)
4. Restart Revit. You'll see a new **PCF Export** ribbon tab with an
   **Export** panel hosting two buttons: **ITMs to PCF** and **Assign Tag**.

If Revit blocks the DLL on first launch with a security warning, right-click
`PcfExport.dll` → **Properties** → tick **Unblock** at the bottom → OK.
That's a one-time Windows quirk for DLLs downloaded from the internet.

Full step-by-step: [docs/installation.md](docs/installation.md).

---

## Quick start

1. Open a Revit model that contains Fabrication parts (pipes, fittings, valves).
2. Select the parts you want to export.
3. On the **PCF Export** tab, click **ITMs to PCF**.
4. Pick the output `.pcf` path, review the options, click **Export**.
5. If any valves are missing tags, the validator opens — fill them in
   (use **Next** to auto-increment) and click **Export** again.

Full user guide: [docs/user-guide.md](docs/user-guide.md).

---

## Modify the code

The repo is a standard .NET 8 / C# 12 project. Any compatible toolchain
works:

- **Visual Studio 2022 / 2026 Community** (free) — open `src/PcfExport.csproj`.
- **JetBrains Rider** — open the same csproj.
- **VS Code + C# Dev Kit** — same.
- **Claude Code** — open the repo root; the included `CLAUDE.md` orients
  it to the project layout.
- **Anything else that speaks `dotnet build`** — `cd src && dotnet build -c Debug`.

Full build / debug / deploy guide: [docs/developer-guide.md](docs/developer-guide.md).

A code-structure tour for people modifying it:
[docs/architecture.md](docs/architecture.md).

---

## License

[MIT](LICENSE) — modify, redistribute, fork freely, just keep the copyright
notice and disclaimer. See the LICENSE file for the full text including the
Autodesk evaluation disclaimer.

---

## Acknowledgements

Built against the **Revit 2025 / 2026** and **Autodesk Fabrication MEP** APIs.
