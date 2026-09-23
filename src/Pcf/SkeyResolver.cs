using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using PcfExport.Models;
using System.Collections.Generic;
using System.IO;

namespace PcfExport.Pcf
{
    /// <summary>
    /// Uses Revit's built-in FabricationUtils.ExportToPCF() to resolve SKEY values
    /// directly from the fabrication ITM database — the only way to read that field.
    /// Parses the temp output and matches by endpoint position (since FabricationUtils
    /// uses a different GUID than Element.UniqueId for UNIQUE-COMPONENT-IDENTIFIER).
    /// </summary>
    internal static class SkeyResolver
    {
        /// <summary>
        /// Overwrites component.Skey with the value Revit resolves from the ITM database.
        /// Components that cannot be matched are left unchanged (keeps derived value).
        /// </summary>
        public static void Apply(
            Document doc,
            List<FabricationPart> parts,
            List<PcfComponent> components)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"pcf_skey_resolve_{Guid.NewGuid():N}.pcf");

            try
            {
                var ids = parts.Select(p => p.Id).ToList();
                FabricationUtils.ExportToPCF(doc, ids, tempPath);

                if (!File.Exists(tempPath)) return;

                var skeyEntries = ParseSkeyEntries(tempPath);
                if (skeyEntries.Count == 0) return;

                foreach (var component in components)
                {
                    if (component.EndPoints.Count == 0 && component.CentrePoint == null) continue;

                    // Skip welds — DeriveWeldSkey is more reliable for distinguishing
                    // shop (WW) vs field (WS) vs fit-up (WF) welds from the description.
                    // FabricationUtils.ExportToPCF() may output WW for all weld types.
                    if (component.PcfType.Equals("WELD", StringComparison.OrdinalIgnoreCase)) continue;

                    // For OLETs, use CentrePoint (run position) since FabricationUtils
                    // exports the stab-in at the centre/run position, not the branch position.
                    double[] pos = (component.PcfType == "OLET" && component.CentrePoint != null)
                        ? component.CentrePoint
                        : component.EndPoints.Count > 0
                            ? component.EndPoints[0]
                            : component.CentrePoint!;

                    // Normalize our component type for matching against temp PCF types
                    string matchType = component.PcfType;
                    // REDUCER maps to REDUCER-CONCENTRIC or REDUCER-ECCENTRIC in temp PCF
                    bool isReducer = matchType.StartsWith("REDUCER", StringComparison.OrdinalIgnoreCase);

                    string? bestSkey = null;
                    int bestIdx = -1;
                    double bestDist = double.MaxValue;

                    for (int i = 0; i < skeyEntries.Count; i++)
                    {
                        var (skey, entryType, ex, ey, ez) = skeyEntries[i];

                        // Only match same component type to prevent cross-contamination
                        bool typeMatch = string.Equals(matchType, entryType, StringComparison.OrdinalIgnoreCase)
                            || (isReducer && entryType.StartsWith("REDUCER", StringComparison.OrdinalIgnoreCase));
                        if (!typeMatch) continue;

                        double dist = Math.Sqrt(
                            Math.Pow(ex - pos[0], 2) +
                            Math.Pow(ey - pos[1], 2) +
                            Math.Pow(ez - pos[2], 2));

                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestSkey = skey;
                            bestIdx = i;
                        }
                    }

                    if (bestSkey != null && bestDist < 1.0 && !string.IsNullOrWhiteSpace(bestSkey))
                    {
                        // Olet-specific guard: don't let Revit's generic "OL??" SKEY
                        // overwrite a derived-specific SKEY (WTBW / SKSW / THSC etc.).
                        // Plant 3D's iso config does not recognize "OL??" prefixes.
                        bool incomingIsGenericOlet =
                            component.PcfType == "OLET"
                            && bestSkey.StartsWith("OL", StringComparison.OrdinalIgnoreCase);
                        bool existingIsSpecificOlet =
                            component.PcfType == "OLET"
                            && !string.IsNullOrEmpty(component.Skey)
                            && !component.Skey.StartsWith("OL", StringComparison.OrdinalIgnoreCase);

                        if (incomingIsGenericOlet && existingIsSpecificOlet)
                        {
                            // Keep the better derived SKEY, but consume the entry
                            // so it's not re-matched to another component.
                            skeyEntries.RemoveAt(bestIdx);
                        }
                        else
                        {
                            component.Skey = bestSkey;
                            skeyEntries.RemoveAt(bestIdx);
                        }
                    }
                }
            }
            catch
            {
                // If Revit's export fails for any reason, silently fall back
                // to the derived SKEY values already on each component.
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

            UpgradeGenericOletSkeys(components);
        }

        /// <summary>Public wrapper for the RFA export path, which skips FabricationUtils.ExportToPCF entirely.</summary>
        public static void UpgradeGenericOletSkeysPublic(List<PcfComponent> components)
            => UpgradeGenericOletSkeys(components);

        /// <summary>
        /// Final pass: any OLET component whose SKEY still starts with "OL"
        /// (generic olet, unsupported by Plant 3D's iso config) gets remapped
        /// to the ISOGEN-canonical subtype inferred from its end connection:
        /// BW → weldolet (WT), SW → sockolet (SK), SC → threadolet (TH).
        /// Runs after catalog/FabricationUtils resolution so it cleans up
        /// both derived-generic values and catalog-generic values.
        /// </summary>
        private static void UpgradeGenericOletSkeys(List<PcfComponent> components)
        {
            foreach (var comp in components)
            {
                if (comp.PcfType != "OLET") continue;
                if (string.IsNullOrEmpty(comp.Skey)) continue;
                if (!comp.Skey.StartsWith("OL", StringComparison.OrdinalIgnoreCase)) continue;
                if (comp.Skey.Length < 4) continue;

                string suffix = comp.Skey.Substring(comp.Skey.Length - 2).ToUpperInvariant();
                string? newPrefix = suffix switch
                {
                    "BW" => "WT",
                    "SW" => "SK",
                    "SC" => "TH",
                    _    => null,
                };
                if (newPrefix != null)
                    comp.Skey = newPrefix + suffix;
            }
        }

        /// <summary>
        /// Parses a PCF file and returns a list of (SKEY, X, Y, Z) entries.
        /// Position is from the first END-POINT or CENTRE-POINT in each component block.
        /// </summary>
        private static List<(string skey, string pcfType, double x, double y, double z)> ParseSkeyEntries(string path)
        {
            var result = new List<(string skey, string pcfType, double x, double y, double z)>();

            string? currentSkey = null;
            string? currentType = null;
            double? cx = null, cy = null, cz = null;

            void FlushCurrent()
            {
                if (!string.IsNullOrWhiteSpace(currentSkey) && currentType != null && cx.HasValue && cy.HasValue && cz.HasValue)
                    result.Add((currentSkey!, currentType, cx.Value, cy.Value, cz.Value));
            }

            // Known PCF component type headers
            var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PIPE", "ELBOW", "TEE", "REDUCER-CONCENTRIC", "REDUCER-ECCENTRIC",
                "VALVE", "FLANGE", "WELD", "OLET", "CAP", "BEND",
                "INSTRUMENT", "FILTER", "MISC-COMPONENT", "COUPLING", "SUPPORT"
            };

            foreach (string rawLine in File.ReadLines(path))
            {
                if (rawLine.Length == 0) continue;

                bool isIndented = rawLine[0] == ' ' || rawLine[0] == '\t';

                if (!isIndented)
                {
                    FlushCurrent();
                    currentSkey = null;
                    currentType = null;
                    cx = cy = cz = null;
                    // Check if this is a component type header
                    string header = rawLine.Trim();
                    if (knownTypes.Contains(header))
                        currentType = header;
                    continue;
                }

                string trimmed = rawLine.TrimStart();

                if (trimmed.StartsWith("SKEY ", StringComparison.OrdinalIgnoreCase))
                    currentSkey = trimmed.Substring(5).Trim();
                else if (!cx.HasValue &&
                         (trimmed.StartsWith("END-POINT ", StringComparison.OrdinalIgnoreCase) ||
                          trimmed.StartsWith("CENTRE-POINT ", StringComparison.OrdinalIgnoreCase)))
                {
                    // Parse position from first endpoint/centrepoint
                    int prefixLen = trimmed.StartsWith("END-POINT ", StringComparison.OrdinalIgnoreCase) ? 10 : 13;
                    var tokens = trimmed.Substring(prefixLen).Trim()
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 3 &&
                        double.TryParse(tokens[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double px) &&
                        double.TryParse(tokens[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double py) &&
                        double.TryParse(tokens[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double pz))
                    {
                        cx = px; cy = py; cz = pz;
                    }
                }
            }

            FlushCurrent();
            return result;
        }
    }
}
