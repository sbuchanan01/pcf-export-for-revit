using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PcfExport.Models;
using PcfExport.Pcf;
using PcfExport.Revit;
using PcfExport.UI;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PcfExport
{
    [Transaction(TransactionMode.Manual)]
    public class ExportCommand : IExternalCommand
    {
        private static ExportDialog? _instance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // If the dialog is already open, bring it to the front
            if (_instance != null)
            {
                _instance.Activate();
                return Result.Succeeded;
            }

            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            var    tokens          = ReadLineNumberTokens(doc, uiDoc);
            string detectedDisplay = string.Join(", ", tokens.Select(t => t.Display));

            // Dim non-exported relevant-category elements in the active
            // view so the user can see visually what's queued for export.
            // Scope's lifetime tied to the dialog — disposed on Closed.
            var visScope = new PcfExport.Revit.ExportVisibilityScope(
                doc, doc.ActiveView, uiDoc.Selection.GetElementIds());

            var dialog = new ExportDialog(uiDoc, tokens, visScope);
            dialog.ViewModel.PipelineReference = detectedDisplay;
            _instance = dialog;
            dialog.Closed += (_, _) =>
            {
                _instance = null;
                visScope.Dispose();
            };
            dialog.Show(); // Modeless — returns immediately; export runs via ExternalEvent

            return Result.Succeeded;
        }

        // ── Public static helpers (also called from ExportDialog event handlers) ──────

        /// <summary>
        /// Reads "Line Number" from every FabricationPart in the current Revit selection
        /// and groups elements by distinct value. "&lt;BLANK&gt;" is used for parts with no value.
        /// </summary>
        public static IReadOnlyList<LineNumberToken> ReadLineNumberTokens(Document doc, UIDocument uiDoc)
        {
            var order  = new List<string>();
            var groups = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);

            foreach (ElementId id in uiDoc.Selection.GetElementIds())
            {
                if (doc.GetElement(id) is not FabricationPart part) continue;

                var    param   = FindParameter(part, "Line Number");
                string value   = string.Empty;

                if (param != null)
                {
                    value = param.StorageType == StorageType.String
                        ? param.AsString() ?? string.Empty
                        : param.AsInteger().ToString();
                }

                string display = string.IsNullOrWhiteSpace(value) ? "<BLANK>" : value.Trim();

                if (!groups.ContainsKey(display))
                {
                    groups[display] = new List<ElementId>();
                    order.Add(display);
                }
                groups[display].Add(id);
            }

            return order
                .Select(d => new LineNumberToken { Display = d, ElementIds = groups[d] })
                .ToList();
        }

        /// <summary>
        /// Phase 1 — collect parts, validate valve tags.
        /// If tags are missing, opens TagValidationDialog modlessly and returns immediately;
        /// FinishExport will be called via a second ExternalEvent once the user confirms.
        /// If no tags are missing, calls FinishExport inline and returns its result.
        /// </summary>
        public static bool RunExport(
            UIApplication                 uiApp,
            IReadOnlyList<LineNumberToken> tokens,
            ExportOptions                 options,
            string                        originalLineNumber,
            ExportDialog                  dialog)
        {
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            // ── Collect all FabricationParts from token element IDs ────────────
            var allIds = new HashSet<ElementId>(tokens.SelectMany(t => t.ElementIds));

            var allParts = allIds
                .Select(id => doc.GetElement(id) as FabricationPart)
                .Where(p => p != null)
                .Cast<FabricationPart>()
                .Where(p => ShouldInclude(p, options))
                .ToList();

            if (allParts.Count == 0)
            {
                TaskDialog.Show("PCF Export",
                    "No fabrication parts were found in the current selection after applying filters.");
                return false;
            }

            // ── Map all parts to PCF components ───────────────────────────────
            var mapper = new FabricationPartMapper(options);

            var mappedComponents = new List<PcfComponent>();
            var ancillaryComponents = new List<PcfComponent>();

            foreach (var part in allParts)
            {
                var comp = mapper.Map(part);
                if (comp == null) continue;
                mappedComponents.Add(comp);

                // Generate GASKET and BOLT components from fabrication ancillaries
                var ancillaries = mapper.MapAncillaries(part, comp);
                ancillaryComponents.AddRange(ancillaries);
            }

            var allComponents = mappedComponents;

            // ── Resolve SKEYs from fabrication ITM database ───────────────────
            // Overwrites derived SKEYs with the actual values from the catalog.
            // This ensures correct SKEYs for components like stab-ins (SKSW vs OLBW).
            Pcf.SkeyResolver.Apply(doc, allParts, allComponents);

            // ── Populate pipeline attributes from first fabrication part ──────
            if (allParts.Count > 0)
            {
                var firstPart = allParts[0];
                // Read parameter values — try AsString first, fall back to AsValueString
                string ReadParam(Element el, params string[] names)
                {
                    var p = FindParameter(el, names);
                    if (p == null) return "";
                    if (p.StorageType == StorageType.String) return p.AsString() ?? "";
                    if (p.StorageType == StorageType.Integer) return p.AsInteger().ToString();
                    return p.AsValueString() ?? "";
                }

                options.Attribute1 = ReadParam(firstPart, "Fabrication Service Abbreviation");
                options.Attribute2 = ReadParam(firstPart, "Material Abbreviation");

                // Insulation Type — strip leading "Mechanical: " prefix
                string insType = ReadParam(firstPart, "Insulation Type");
                if (insType.StartsWith("Mechanical: ", StringComparison.OrdinalIgnoreCase))
                    insType = insType.Substring("Mechanical: ".Length);
                options.Attribute3 = insType;

                // Insulation Thickness — internal units (feet) → inches → fractional string
                var insThickParam = FindParameter(firstPart, "Insulation Thickness");
                if (insThickParam != null)
                {
                    double thickFeet = insThickParam.AsDouble();
                    double thickInches = thickFeet * 12.0;
                    options.Attribute4 = thickInches > 0.01
                        ? InchesToFractionString(thickInches)
                        : "";
                }
                else
                {
                    options.Attribute4 = "";
                }
                // Attribute5, Attribute6 — reserved
                options.Attribute7 = ReadParam(firstPart, "Line Number");
                // Attribute8 — reserved
                options.Attribute9 = System.IO.Path.GetFileNameWithoutExtension(doc.PathName ?? doc.Title ?? "");

                // Also set PIPING-SPEC from Pipe Spec if not already set from component mapping
                if (string.IsNullOrWhiteSpace(options.PipingSpec))
                {
                    string? pipeSpec = FindParameter(firstPart, "Pipe Spec")?.AsString();
                    if (!string.IsNullOrWhiteSpace(pipeSpec))
                        options.PipingSpec = pipeSpec;
                }

                // Insulation spec — reuse insType from Attribute3 if already read
                if (!string.IsNullOrWhiteSpace(options.Attribute3))
                    options.InsulationSpec = options.Attribute3;
                else
                {
                    string insSpec = ReadParam(firstPart, "Insulation Type");
                    if (!string.IsNullOrWhiteSpace(insSpec))
                    {
                        if (insSpec.StartsWith("Mechanical: ", StringComparison.OrdinalIgnoreCase))
                            insSpec = insSpec.Substring(12);
                        options.InsulationSpec = insSpec;
                    }
                }
            }

            // ── Build lookup dictionaries ──────────────────────────────────────
            var partById      = allParts.ToDictionary(p => p.Id);
            var componentById = allComponents.ToDictionary(c =>
                allParts.First(p => p.UniqueId == c.RevitUniqueId).Id);

            // ── Identify valves with missing tags ──────────────────────────────
            var missingTags = allComponents
                .Where(c => c.PcfType == "VALVE" && string.IsNullOrWhiteSpace(c.Tag))
                .Select(c =>
                {
                    var part = allParts.First(p => p.UniqueId == c.RevitUniqueId);
                    return new ValveTagItem
                    {
                        ElementId   = part.Id,
                        UniqueId    = part.UniqueId,
                        Description = c.ItemDescription,
                        Component   = c,
                    };
                })
                .ToList();

            var state = new ExportState
            {
                Tokens             = tokens,
                Options            = options,
                OriginalLineNumber  = originalLineNumber,
                Dialog             = dialog,
                AllParts           = allParts,
                AllComponents      = allComponents,
                MissingTags        = missingTags,
                PartById           = partById,
                ComponentById      = componentById,
                AncillaryComponents = ancillaryComponents,
            };

            if (missingTags.Count > 0)
            {
                var existingTags = BuildExistingTags(doc, allComponents);

                var tagDialog = new TagValidationDialog(missingTags, uiDoc, existingTags);

                tagDialog.OnConfirmed = () =>
                {
                    // Back on Revit main thread via ExternalEvent to commit tags + write files
                    PcfExportApp.ExportHandler!.SetAction(uiApp2 => FinishExport(uiApp2, state));
                    PcfExportApp.ExportEvent!.Raise();
                };

                tagDialog.OnCancelled = () =>
                {
                    // Restore the export dialog so the user can try again
                    dialog.Show();
                };

                // Hide the export dialog while the user is assigning tags so they
                // have a clear view of the model for zooming and panning.
                dialog.Hide();
                tagDialog.Show();
                return true; // export continues asynchronously
            }

            // No missing tags — finish inline on the same ExternalEvent call
            return FinishExport(uiApp, state);
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Phase 2 — commit tags (if any), write PCF files, mark/pin elements, show summary.
        /// Always runs on the Revit main thread (either inline or via a second ExternalEvent).
        /// Closes the ExportDialog on success.
        /// </summary>
        private static bool FinishExport(UIApplication uiApp, ExportState state)
        {
            Document doc = uiApp.ActiveUIDocument.Document;

            // ── Commit valve tags ──────────────────────────────────────────────
            if (state.MissingTags.Count > 0)
            {
                using var tagTx = new Transaction(doc, "Set Valve Tags");
                tagTx.Start();
                foreach (var item in state.MissingTags)
                {
                    item.Component.Tag = item.Tag;
                    var param = FindParameter(doc.GetElement(item.ElementId), "Tag");
                    if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                        param.Set(item.Tag);
                }
                tagTx.Commit();
            }

            bool   isMulti       = state.Tokens.Count > 1;
            var    summaryLines  = new List<string>();
            int    totalExported = 0;
            var    options       = state.Options;

            IEnumerable<(string pipelineRef, string outputPath, IReadOnlyList<ElementId> ids)> exportJobs;

            if (isMulti)
            {
                exportJobs = state.Tokens.Select(token =>
                {
                    string safeName = token.Display == "<BLANK>" ? "_BLANK" : token.Display;
                    safeName = string.Concat(safeName.Split(Path.GetInvalidFileNameChars()));
                    string filePath = Path.Combine(options.OutputFolderPath, safeName + ".pcf");
                    string pipeRef  = token.Display == "<BLANK>" ? string.Empty : token.Display;
                    return (pipeRef, filePath, token.ElementIds);
                });
            }
            else
            {
                exportJobs = new[] { (options.PipelineReference, options.OutputFilePath,
                    (IReadOnlyList<ElementId>)state.Tokens.SelectMany(t => t.ElementIds).ToList()) };
            }

            foreach (var (pipelineRef, outputPath, ids) in exportJobs)
            {
                var jobOptions = new ExportOptions
                {
                    OutputFilePath    = outputPath,
                    OutputFolderPath  = options.OutputFolderPath,
                    PipelineReference = pipelineRef,
                    Units             = options.Units,
                    IsogenFlsFile     = options.IsogenFlsFile,
                    SpoolIdentifier   = options.SpoolIdentifier,
                    ProjectIdentifier = options.ProjectIdentifier,
                    Revision          = options.Revision,
                    Area              = options.Area,
                    PipingSpec        = options.PipingSpec,
                    InsulationSpec    = options.InsulationSpec,
                    Attribute1        = options.Attribute1,
                    Attribute2        = options.Attribute2,
                    Attribute3        = options.Attribute3,
                    Attribute4        = options.Attribute4,
                    Attribute5        = options.Attribute5,
                    Attribute6        = options.Attribute6,
                    Attribute7        = options.Attribute7,
                    Attribute8        = options.Attribute8,
                    Attribute9        = options.Attribute9,
                    SelectionMode     = options.SelectionMode,
                    IncludePipes      = options.IncludePipes,
                    IncludeFittings   = options.IncludeFittings,
                    IncludeWelds      = options.IncludeWelds,
                    IncludeHangers    = options.IncludeHangers,
                    UseStandardDescriptions = options.UseStandardDescriptions,
                    ConnectedFrom = options.ConnectedFrom,
                    ConnectedTo   = options.ConnectedTo,
                };

                var jobParts      = ids.Where(id => state.PartById.ContainsKey(id))
                                       .Select(id => state.PartById[id]).ToList();
                var jobComponents = ids.Where(id => state.ComponentById.ContainsKey(id))
                                       .Select(id => state.ComponentById[id]).ToList();

                if (jobParts.Count == 0) continue;

                var exportedIds = new HashSet<ElementId>(jobParts.Select(p => p.Id));
                var openEnds    = FindOpenEnds(jobParts, exportedIds, jobOptions);
                int skipped     = jobParts.Count - jobComponents.Count;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    using var writer = new PcfWriter(outputPath);

                    writer.WriteHeader(jobOptions);

                    writer.WriteOpenEndsWithConnections(openEnds, jobOptions.ConnectedFrom, jobOptions.ConnectedTo);

                    if (openEnds.Count > 0)
                        writer.WriteBlankLine();

                    // Apply standardized descriptions if Plant 3D compatibility is enabled
                    if (jobOptions.UseStandardDescriptions)
                        StandardizeDescriptions(jobComponents);

                    // ── Offset gasket endpoints and adjust adjacent valve FL positions ──
                    // In standard PCF, gaskets bridge the gap between flange and valve.
                    // Gasket EP1 = flange FL face, Gasket EP2 = offset outward by gasket thickness.
                    // The adjacent valve's FL endpoint moves to match gasket EP2.
                    double gasketOffset = jobOptions.Units == PcfUnits.MM ? 3.175 : 0.125;
                    foreach (var anc in state.AncillaryComponents)
                    {
                        if (anc.PcfType != "GASKET" || anc.EndPoints.Count < 2) continue;

                        // Find the parent flange to get the offset direction
                        var parentFlange = jobComponents.FirstOrDefault(c =>
                            c.RevitUniqueId == anc.RevitUniqueId && c.PcfType == "FLANGE");
                        if (parentFlange == null || parentFlange.EndPoints.Count < 2) continue;

                        // Direction: FL face (EP[0]) away from BW (EP[1])
                        var fl = parentFlange.EndPoints[0];
                        var bw = parentFlange.EndPoints[1];
                        double dx = fl[0] - bw[0], dy = fl[1] - bw[1], dz = fl[2] - bw[2];
                        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (len < 0.001) continue;

                        // Offset gasket EP2 outward
                        anc.EndPoints[1] = new[]
                        {
                            fl[0] + (dx / len) * gasketOffset,
                            fl[1] + (dy / len) * gasketOffset,
                            fl[2] + (dz / len) * gasketOffset,
                        };

                        // Find adjacent valve with FL endpoint matching the gasket EP1 position
                        var gasketFl = anc.EndPoints[0]; // = flange FL face
                        foreach (var valve in jobComponents)
                        {
                            if (valve.PcfType != "VALVE") continue;
                            for (int vi = 0; vi < valve.EndPoints.Count; vi++)
                            {
                                var vep = valve.EndPoints[vi];
                                double dist = Math.Sqrt(
                                    Math.Pow(vep[0] - gasketFl[0], 2) +
                                    Math.Pow(vep[1] - gasketFl[1], 2) +
                                    Math.Pow(vep[2] - gasketFl[2], 2));
                                if (dist < 0.01) // within 0.01" tolerance
                                {
                                    // Move valve endpoint to gasket EP2 (offset position)
                                    valve.EndPoints[vi] = new[]
                                    {
                                        anc.EndPoints[1][0],
                                        anc.EndPoints[1][1],
                                        anc.EndPoints[1][2],
                                    };
                                    break;
                                }
                            }
                        }
                    }

                    // Build lookup: parent UniqueId → ancillary components
                    var ancByParent = new Dictionary<string, List<PcfComponent>>();
                    foreach (var anc in state.AncillaryComponents)
                    {
                        if (string.IsNullOrEmpty(anc.RevitUniqueId)) continue;
                        if (!ancByParent.TryGetValue(anc.RevitUniqueId, out var list))
                        {
                            list = new List<PcfComponent>();
                            ancByParent[anc.RevitUniqueId] = list;
                        }
                        list.Add(anc);
                    }

                    // Set FLOW direction on check valves based on Connected From/To
                    // Must run AFTER gasket offset adjustment (which moves valve endpoints)
                    if (jobOptions.ConnectedFrom?.Position != null && jobOptions.ConnectedTo?.Position != null)
                    {
                        var fromPos = jobOptions.ConnectedFrom.Position;
                        var toPos = jobOptions.ConnectedTo.Position;

                        foreach (var comp in jobComponents)
                        {
                            bool isCheckValve = comp.PcfType == "VALVE" &&
                                (comp.Skey?.StartsWith("VC", StringComparison.OrdinalIgnoreCase) == true ||
                                 comp.Skey?.StartsWith("NV", StringComparison.OrdinalIgnoreCase) == true ||
                                 comp.Skey?.StartsWith("CK", StringComparison.OrdinalIgnoreCase) == true);

                            if (isCheckValve && comp.EndPoints.Count >= 2)
                            {
                                // Which valve endpoint is closer to the From equipment.
                                // Comparing EP[0]→From vs EP[0]→To is unreliable because
                                // From and To can both lie on the same side of the valve
                                // (e.g. the To pipe's midpoint sits closer to the valve
                                // than the From equipment does). Compare EP[0]→From to
                                // EP[1]→From instead; whichever is smaller is upstream.
                                var ep0 = comp.EndPoints[0];
                                var ep1 = comp.EndPoints[1];
                                double dist0 = Math.Sqrt(
                                    Math.Pow(ep0[0] - fromPos[0], 2) +
                                    Math.Pow(ep0[1] - fromPos[1], 2) +
                                    Math.Pow(ep0[2] - fromPos[2], 2));
                                double dist1 = Math.Sqrt(
                                    Math.Pow(ep1[0] - fromPos[0], 2) +
                                    Math.Pow(ep1[1] - fromPos[1], 2) +
                                    Math.Pow(ep1[2] - fromPos[2], 2));

                                // EP[0] should end up upstream (closer to From). Swap when
                                // EP[0] is farther from From than EP[1].
                                if (dist0 > dist1)
                                {
                                    var temp = comp.EndPoints[0];
                                    comp.EndPoints[0] = comp.EndPoints[1];
                                    comp.EndPoints[1] = temp;
                                    if (comp.EndTypes.Count >= 2)
                                    {
                                        var tempType = comp.EndTypes[0];
                                        comp.EndTypes[0] = comp.EndTypes[1];
                                        comp.EndTypes[1] = tempType;
                                    }
                                }
                                comp.FlowDirection = 1;
                            }
                        }
                    }

                    int seq = 1;
                    foreach (var component in jobComponents)
                    {
                        writer.WriteComponent(component, seq++);

                        // Write ancillaries (GASKET, BOLT) immediately after parent
                        if (ancByParent.TryGetValue(component.RevitUniqueId, out var ancillaries))
                        {
                            foreach (var anc in ancillaries)
                                writer.WriteComponent(anc, seq++);
                        }
                    }

                    writer.WriteMaterials();
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("PCF Export Error",
                        $"Failed to write {Path.GetFileName(outputPath)}:\n{ex.Message}");
                    continue;
                }

                MarkAndPin(doc, jobParts);

                totalExported += jobComponents.Count;
                string line = $"{Path.GetFileName(outputPath)}: {jobComponents.Count} component(s)";
                if (openEnds.Count > 0) line += $", {openEnds.Count} open end(s)";
                if (skipped > 0)        line += $", {skipped} skipped";
                summaryLines.Add(line);
            }

            // ── Push back Pipeline Reference (single-value mode only) ─────────
            if (!isMulti)
            {
                string newLineNumber = options.PipelineReference;
                if (!string.Equals(newLineNumber, state.OriginalLineNumber, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(newLineNumber))
                {
                    string pushSummary = string.Empty;
                    PushLineNumber(doc, uiApp.ActiveUIDocument, newLineNumber, ref pushSummary);
                    if (!string.IsNullOrWhiteSpace(pushSummary))
                        summaryLines.Add(pushSummary.TrimStart('\n'));
                }
            }

            string header = isMulti
                ? $"Exported {totalExported} total component(s) across {state.Tokens.Count} PCF file(s):"
                : $"Exported {totalExported} component(s) to:";

            state.Dialog.Close();

            TaskDialog.Show("PCF Export Complete",
                header + "\n" + string.Join("\n", summaryLines));

            return true;
        }

        private static HashSet<string> BuildExistingTags(Document doc, List<PcfComponent> allComponents)
        {
            var existingTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in allComponents.Where(c => c.PcfType == "VALVE"
                                                     && !string.IsNullOrWhiteSpace(c.Tag)))
                existingTags.Add(c.Tag!);

            foreach (var element in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .WhereElementIsNotElementType()
                .Cast<Element>())
            {
                string? tag = FindParameter(element, "Tag")?.AsString();
                if (!string.IsNullOrWhiteSpace(tag))
                    existingTags.Add(tag);
            }

            return existingTags;
        }

        private static bool ShouldInclude(FabricationPart part, ExportOptions options)
        {
            if (part.IsAHanger()) return options.IncludeHangers;
            string pcfType = PartTypeClassifier.GetPcfType(part);
            if (pcfType == "WELD") return options.IncludeWelds;
            if (PartTypeClassifier.IsStraightPipe(part)) return options.IncludePipes;
            return options.IncludeFittings;
        }

        /// <summary>Public wrapper for the RFA export path, which reuses the same formatter.</summary>
        public static void StandardizeDescriptionsPublic(List<PcfComponent> components)
            => StandardizeDescriptions(components);

        private static bool ContainsAnyText(string source, params string[] tokens)
            => tokens.Any(t => source.Contains(t, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Reformats ITEM-DESCRIPTION on each component to follow standard naming conventions
        /// compatible with Plant 3D's PCF to Pipe catalog matching.
        /// Uses SKEY prefix → component type keyword, SKEY suffix → end connection,
        /// plus Material and Specification from the component data.
        /// </summary>
        private static void StandardizeDescriptions(List<PcfComponent> components)
        {
            // SKEY prefix → standard component type name
            var typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Valves
                { "VT", "GATE VALVE" }, { "VB", "BALL VALVE" }, { "VY", "BUTTERFLY VALVE" },
                { "VG", "GLOBE VALVE" }, { "VC", "CHECK VALVE" }, { "CK", "CHECK VALVE" },
                { "VN", "NEEDLE VALVE" }, { "VD", "DIAPHRAGM VALVE" }, { "VP", "PLUG VALVE" },
                { "VR", "RELIEF VALVE" }, { "VS", "SLIDE VALVE" }, { "VK", "COCK VALVE" },
                { "VX", "PRESSURE REDUCING VALVE" }, { "VV", "VALVE" },
                { "AV", "ANGLE VALVE" }, { "NV", "NON RETURN VALVE" },
                { "V3", "3-WAY VALVE" }, { "V4", "4-WAY VALVE" },
                // Elbows
                { "EL", "ELL" }, { "ER", "REDUCING ELL" },
                // Tees
                { "TE", "TEE" }, { "TY", "Y-TEE" }, { "TS", "STUB-IN TEE" },
                // Reducers
                { "RC", "REDUCER CONC" }, { "RE", "REDUCER ECC" },
                // Flanges
                { "FL", "FLANGE" },
                // Caps
                { "KA", "CAP" },
                // Olets
                { "WT", "WELDOLET" }, { "SK", "SOCKOLET" }, { "TH", "THREADOLET" },
                { "OL", "OLET" }, { "SW", "SWEEPOLET" }, { "LA", "LATROLET" },
                // Couplings
                { "CO", "COUPLING" }, { "NB", "NIPPLE" },
            };

            // SKEY suffix → end connection description.
            // "FL" → "RF" (Raised Face) matches ASME B16.5 flange-face notation used by
            // Plant 3D's default CS150/CS300 spec catalogs (e.g. "FLANGE WN, 300 LB, RF"
            // and "Gate Valve, Solid Wedge, 300 LB, RF"). "FLG" is not recognized.
            var endConnNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "BW", "BW" }, { "SW", "SW" }, { "FL", "RF" }, { "SC", "SCREWED" },
                { "PL", "PE" }, { "CP", "COMP" }, { "CL", "CLAMP" },
            };

            // Flange SKEY → flange type keyword
            var flangeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "FLWN", "WN" }, { "FLSO", "SO" }, { "FLSW", "SW" }, { "FLSC", "SCREWED" },
                { "FLBL", "BLIND" }, { "FLSJ", "SJ" }, { "FLLB", "LAP JOINT" },
                { "FLRC", "REDUCING CONC" }, { "FLRE", "REDUCING ECC" },
                { "FLSE", "STUB END" }, { "FLRG", "LAP JOINT RING" },
            };

            foreach (var comp in components)
            {
                string pcfType = comp.PcfType;
                string skey = comp.Skey ?? "";

                // Skip pipes (no SKEY) and welds (simple description is fine)
                if (pcfType.Equals("PIPE", StringComparison.OrdinalIgnoreCase)) continue;
                if (pcfType.Equals("WELD", StringComparison.OrdinalIgnoreCase)) continue;
                if (skey.Length < 2) continue;

                string prefix = skey.Length >= 4 ? skey.Substring(0, 2) : skey;
                string suffix = skey.Length >= 4 ? skey.Substring(skey.Length - 2) : "";

                // Build the standardized description
                var parts = new List<string>();

                // 1. Component type from SKEY prefix
                if (pcfType.Equals("FLANGE", StringComparison.OrdinalIgnoreCase) && flangeTypes.TryGetValue(skey, out string? flangeType))
                {
                    parts.Add($"FLANGE {flangeType}");
                }
                else if (pcfType.Equals("ELBOW", StringComparison.OrdinalIgnoreCase) || pcfType.Equals("BEND", StringComparison.OrdinalIgnoreCase))
                {
                    string angle = comp.Angle.HasValue ? $"{comp.Angle.Value / 100}" : "90";
                    string elbType = typeNames.TryGetValue(prefix, out string? et) ? et : "ELL";
                    parts.Add($"{elbType} {angle}");
                    // LR (long radius) is the default for butt-weld elbows in CS150/CS300
                    // and most Plant 3D spec catalogs (e.g. "ELL 90 LR, BW, ASME B16.9").
                    // Socket-weld and screwed elbows don't carry the LR qualifier.
                    // Preserve any existing "SR" (short radius) indicator from source text;
                    // otherwise default BW elbows to LR so they match the catalog.
                    bool isShortRadius = comp.ItemDescription?.Contains("SR", StringComparison.OrdinalIgnoreCase) == true
                                         || comp.ItemDescription?.Contains("SHORT", StringComparison.OrdinalIgnoreCase) == true;
                    bool isLongRadius  = comp.ItemDescription?.Contains("LR", StringComparison.OrdinalIgnoreCase) == true
                                         || comp.ItemDescription?.Contains("LONG", StringComparison.OrdinalIgnoreCase) == true;
                    bool isBw = suffix.Equals("BW", StringComparison.OrdinalIgnoreCase);
                    if (isLongRadius || (isBw && !isShortRadius))
                        parts[0] += " LR";
                    else if (isShortRadius)
                        parts[0] += " SR";
                }
                else if (pcfType.Equals("TEE", StringComparison.OrdinalIgnoreCase))
                {
                    // Distinguish reducing tee by BranchBore or "Reducing" in description
                    bool isReducing = comp.BranchBore.HasValue ||
                        (comp.ItemDescription?.Contains("Reducing", StringComparison.OrdinalIgnoreCase) == true);
                    parts.Add(isReducing ? "TEE REDUCING" : "TEE");
                }
                else if (typeNames.TryGetValue(prefix, out string? typeName))
                {
                    parts.Add(typeName);
                }
                else
                {
                    continue; // Unknown type, keep original description
                }

                // 1a. Valve subtype default — CS150/CS300 gate-valve entries always
                // carry a subtype (Solid Wedge or Double Disc); matcher won't pick
                // between them without one. "Solid Wedge" covers 1/2"+ and is the
                // more universal default. Only applied when the source description
                // doesn't already carry a subtype keyword.
                if (pcfType.Equals("VALVE", StringComparison.OrdinalIgnoreCase))
                {
                    string srcDesc = comp.ItemDescription ?? "";
                    bool hasSubtype = ContainsAnyText(srcDesc,
                        "SOLID WEDGE", "DOUBLE DISC", "FULL BORE", "SHORT PATTERN",
                        "LONG PATTERN", "SWING", "VENTURI", "OFFSET", "LUG", "WFR");
                    if (!hasSubtype)
                    {
                        string? subtype = prefix.ToUpperInvariant() switch
                        {
                            "VT" => "Solid Wedge",     // Gate valve — standard default
                            "VC" => "Swing",            // Check valve
                            "VY" => "Offset",           // Butterfly
                            _    => null
                        };
                        if (!string.IsNullOrWhiteSpace(subtype)) parts.Add(subtype);
                    }
                }

                // 1b. Pressure class — parsed from PIPING-SPEC for flanges and valves
                // (catalog pattern: "FLANGE WN, 300 LB, RF" / "Gate Valve, ..., 300 LB, RF").
                // Socket-weld fittings use the ASME B16.11 convention "3000 LB" regardless
                // of spec class; hardcode that rather than reading spec.
                bool isFlangeOrValve =
                    pcfType.Equals("FLANGE", StringComparison.OrdinalIgnoreCase) ||
                    pcfType.Equals("VALVE",  StringComparison.OrdinalIgnoreCase);
                bool isSwFitting =
                    suffix.Equals("SW", StringComparison.OrdinalIgnoreCase) &&
                    (pcfType.Equals("ELBOW",    StringComparison.OrdinalIgnoreCase) ||
                     pcfType.Equals("TEE",      StringComparison.OrdinalIgnoreCase) ||
                     pcfType.Equals("CROSS",    StringComparison.OrdinalIgnoreCase) ||
                     pcfType.Equals("COUPLING", StringComparison.OrdinalIgnoreCase) ||
                     pcfType.Equals("CAP",      StringComparison.OrdinalIgnoreCase));

                string? pressureClass = null;
                if (isFlangeOrValve && !string.IsNullOrWhiteSpace(comp.PipingSpec))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        comp.PipingSpec, @"(\d{3,4})");
                    if (m.Success) pressureClass = $"{m.Groups[1].Value} LB";
                }
                else if (isSwFitting)
                {
                    pressureClass = "3000 LB";
                }
                if (!string.IsNullOrWhiteSpace(pressureClass))
                    parts.Add(pressureClass);

                // 2. End connection from SKEY suffix
                if (suffix.Length == 2 && endConnNames.TryGetValue(suffix, out string? endConn))
                    parts.Add(endConn);

                // 3. Specification — use ProductSpecification when present, otherwise
                // fall back to the ASME standard for this component type. RFAs rarely
                // carry a Specification parameter; without an ASME default, the catalog
                // matcher can't disambiguate among multiple spec-class entries.
                string? specText = comp.ProductSpecification;
                if (string.IsNullOrWhiteSpace(specText))
                {
                    specText = pcfType.ToUpperInvariant() switch
                    {
                        "FLANGE"  => "ASME B16.5",
                        "VALVE"   => "ASME B16.10",
                        "OLET"    => "ASME B16.11",
                        _ when isSwFitting => "ASME B16.11",
                        _         => "ASME B16.9",  // BW fittings (elbow/tee/reducer/cap/cross)
                    };
                }
                if (!string.IsNullOrWhiteSpace(specText))
                    parts.Add(specText);

                // 4. Material (e.g., "ASTM A234 Gr WPB", "ASTM A105")
                if (!string.IsNullOrWhiteSpace(comp.Material))
                    parts.Add(comp.Material);

                if (parts.Count > 0)
                    comp.ItemDescription = string.Join(", ", parts);
            }
        }

        private static void MarkAndPin(Document doc, List<FabricationPart> parts)
        {
            using var tx = new Transaction(doc, "Mark PCF Exported + Pin");
            tx.Start();
            foreach (var part in parts)
            {
                var param = FindParameter(part, "Fabrication Status");
                if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                    param.Set("Exported to PCF");

                if (!part.Pinned) part.Pinned = true;
            }
            tx.Commit();
        }

        private static void PushLineNumber(Document doc, UIDocument uiDoc, string value, ref string summary)
        {
            var targets = uiDoc.Selection
                .GetElementIds()
                .Select(id => doc.GetElement(id) as FabricationPart)
                .Where(p => p != null)
                .Cast<FabricationPart>()
                .ToList();

            if (targets.Count == 0) return;

            using var tx = new Transaction(doc, "Set Line Number");
            tx.Start();
            int updated = 0;
            foreach (var part in targets)
            {
                var param = FindParameter(part, "Line Number");
                if (param == null || param.IsReadOnly) continue;

                if (param.StorageType == StorageType.String)
                    param.Set(value);
                else if (param.StorageType == StorageType.Integer
                         && int.TryParse(value, out int intVal))
                    param.Set(intVal);

                updated++;
            }
            tx.Commit();
            summary += updated > 0
                ? $"\n\"Line Number\" updated to \"{value}\" on {updated} element(s)."
                : "\nCould not update \"Line Number\" — parameter not found or is read-only.";
        }

        /// <summary>
        /// Converts decimal inches to a fractional pipe size string.
        /// e.g. 0.5 → "1/2''", 1.25 → "1-1/4''", 10.0 → "10''".
        /// </summary>
        private static string InchesToFractionString(double inches)
        {
            int whole = (int)Math.Floor(inches);
            double frac = inches - whole;

            string fracStr = "";
            if (Math.Abs(frac - 0.125) < 0.01) fracStr = "1/8";
            else if (Math.Abs(frac - 0.25) < 0.01) fracStr = "1/4";
            else if (Math.Abs(frac - 0.375) < 0.01) fracStr = "3/8";
            else if (Math.Abs(frac - 0.5) < 0.01) fracStr = "1/2";
            else if (Math.Abs(frac - 0.625) < 0.01) fracStr = "5/8";
            else if (Math.Abs(frac - 0.75) < 0.01) fracStr = "3/4";
            else if (Math.Abs(frac - 0.875) < 0.01) fracStr = "7/8";
            else if (frac > 0.01) return $"{inches:F1}''";

            if (whole == 0 && fracStr.Length > 0) return $"{fracStr}''";
            if (fracStr.Length > 0) return $"{whole}-{fracStr}''";
            return $"{whole}''";
        }

        /// <summary>Case-insensitive parameter lookup — tries multiple names, returns first found.</summary>
        public static Parameter? FindParameter(Element element, params string[] names)
        {
            foreach (string name in names)
            {
                var p = element.LookupParameter(name);
                if (p != null) return p;
            }

            // Slow fallback: case-insensitive scan for any of the names
            return element.Parameters
                .Cast<Parameter>()
                .FirstOrDefault(p => names.Any(n => string.Equals(
                    p.Definition.Name, n, StringComparison.OrdinalIgnoreCase)));
        }

        private static List<double[]> FindOpenEnds(
            List<FabricationPart> parts,
            HashSet<ElementId>    exportedIds,
            ExportOptions         options)
        {
            var openEnds   = new List<double[]>();
            var lengthUnit = options.Units == PcfUnits.MM
                ? UnitTypeId.Millimeters : UnitTypeId.Inches;

            foreach (var part in parts)
            {
                // Supports don't contribute to open ends — they're auxiliary, not part of the pipe run
                if (part.IsAHanger()) continue;

                var connMgr = part.ConnectorManager;
                if (connMgr == null) continue;

                foreach (Connector connector in connMgr.Connectors)
                {
                    if (connector.ConnectorType != ConnectorType.End &&
                        connector.ConnectorType != ConnectorType.Curve)
                        continue;

                    bool hasInternalConnection = false;
                    if (connector.IsConnected)
                    {
                        foreach (Connector other in connector.AllRefs)
                        {
                            if (other.Owner is FabricationPart fp && exportedIds.Contains(fp.Id))
                            {
                                hasInternalConnection = true;
                                break;
                            }
                        }
                    }

                    if (!hasInternalConnection)
                    {
                        XYZ origin = connector.Origin;
                        // Connector.Radius is in internal units (feet); diameter = 2 * radius.
                        // Plant 3D's ISO generator requires the bore on open-end CO-ORDS;
                        // omitting it reads as Size=0 and triggers a size mismatch against
                        // the adjacent pipe, producing NaN cascades during canvas fitting.
                        double diameterInternal = connector.Radius * 2.0;
                        openEnds.Add(new[]
                        {
                            UnitUtils.ConvertFromInternalUnits(origin.X, lengthUnit),
                            UnitUtils.ConvertFromInternalUnits(origin.Y, lengthUnit),
                            UnitUtils.ConvertFromInternalUnits(origin.Z, lengthUnit),
                            UnitUtils.ConvertFromInternalUnits(diameterInternal, lengthUnit),
                        });
                    }
                }
            }
            return openEnds;
        }

        // ── State passed between the two export phases ───────────────────────────

        private sealed class ExportState
        {
            public required IReadOnlyList<LineNumberToken>        Tokens;
            public required ExportOptions                         Options;
            public required string                                OriginalLineNumber;
            public required ExportDialog                          Dialog;
            public required List<FabricationPart>                 AllParts;
            public required List<PcfComponent>                    AllComponents;
            public required List<ValveTagItem>                    MissingTags;
            public required Dictionary<ElementId, FabricationPart> PartById;
            public required Dictionary<ElementId, PcfComponent>   ComponentById;
            public required List<PcfComponent>                    AncillaryComponents;
        }
    }
}
