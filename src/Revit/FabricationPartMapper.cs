using Autodesk.Revit.DB;
using PcfExport.Models;

namespace PcfExport.Revit
{
    /// <summary>
    /// Converts a Revit FabricationPart into a PCF-neutral PcfComponent model
    /// using the property mappings defined in pcfmapping.xlsx.
    /// </summary>
    internal class FabricationPartMapper
    {
        private readonly ExportOptions _options;
        private readonly ForgeTypeId _lengthUnit;

        public FabricationPartMapper(ExportOptions options)
        {
            _options    = options;
            _lengthUnit = options.Units == PcfUnits.MM ? UnitTypeId.Millimeters : UnitTypeId.Inches;
        }

        public PcfComponent? Map(FabricationPart part)
        {
            try { return MapInternal(part); }
            catch { return null; }
        }

        private PcfComponent MapInternal(FabricationPart part)
        {
            var connectors = ConnectorHelper.GetPhysicalConnectors(part);
            string pcfType = PartTypeClassifier.GetPcfType(part);
            string alias   = part.Alias ?? string.Empty;

            var component = new PcfComponent
            {
                PcfType            = pcfType,

                // SKEY → "Fabrication Item PCF SKEY" parameter (exact name from pcfmapping.xlsx)
                Skey               = PartTypeClassifier.GetSkey(part),

                RevitUniqueId      = part.UniqueId,

                // ITEM-CODE grouping key and CID attribute → FabricationPart.ItemCustomId
                FabricationCid     = part.ItemCustomId,

                // ITEM-DESCRIPTION → ProductLongDescription parameter
                ItemDescription    = GetParam(part, "Long Description", "Product Long Description", "Description") ?? alias,

                // PIPING-SPEC → Pipe Spec parameter (shared parameter on fabrication parts)
                PipingSpec         = GetParam(part, "Pipe Spec", "Fabrication Service Abbreviation", "Specification Description") ?? string.Empty,

                // SERVICE → ServiceAbbreviation parameter (written only if non-empty)
                ServiceAbbreviation= NullIfEmpty(GetParam(part, "Service Abbreviation", "Service")),

                // SYSTEM-NAME → service name parameter
                SystemName         = NullIfEmpty(GetParam(part, "System Name", "Service Name")),

                // SIZE → ProductSizeDescription parameter
                Size               = NullIfEmpty(GetParam(part, "Size Description", "Product Size Description")),

                // MATERIAL → ProductMaterialDescription parameter
                Material           = NullIfEmpty(GetParam(part, "Material Description", "Product Material Description")),

                // Material Abbreviation (e.g. "CS") — for MATERIALS section
                MaterialAbbreviation = NullIfEmpty(GetParam(part, "Material Abbreviation")),

                // SPECIFICATION (alt) → ProductSpecificationDescription parameter
                ProductSpecification = NullIfEmpty(GetParam(part, "Spec Description", "Product Specification Description")),

                SpoolIdentifier    = string.IsNullOrWhiteSpace(_options.SpoolIdentifier)
                                     ? _options.PipelineReference
                                     : _options.SpoolIdentifier,

                // Insulation detection
                HasInsulation      = HasInsulationApplied(part),
                InsulationThickness = GetInsulationThickness(part),

                // WEIGHT → FabricationPart.Weight (returns kg internally)
                Weight             = GetWeight(part),

                Bore               = GetBore(connectors.FirstOrDefault()),
            };

            // For pipes with taps (olets produce a mid-pipe Curve connector in
            // addition to the two End connectors), filter to End connectors only
            // before picking the pipe-end pair. A Curve connector's BasisZ is
            // tangent to the pipe, which makes it anti-parallel to one of the
            // Ends — the earlier anti-parallel-dot search would sometimes pick
            // a (Curve, End) pair instead of (End, End) depending on Revit's
            // ConnectorSet iteration order, truncating the exported pipe at the
            // tap point.
            var exportConns = connectors;
            if (pcfType == "PIPE")
            {
                var endConns = connectors
                    .Where(c => c.ConnectorType == ConnectorType.End)
                    .ToList();
                if (endConns.Count >= 2)
                {
                    // Pick the anti-parallel pair among Ends. For a well-formed
                    // straight pipe there are exactly 2 Ends and this trivially
                    // picks them both.
                    int bestI = 0, bestJ = 1;
                    double bestDot = 1;
                    for (int i = 0; i < endConns.Count; i++)
                    for (int j = i + 1; j < endConns.Count; j++)
                    {
                        double d = endConns[i].CoordinateSystem.BasisZ
                            .DotProduct(endConns[j].CoordinateSystem.BasisZ);
                        if (d < bestDot) { bestDot = d; bestI = i; bestJ = j; }
                    }
                    exportConns = new List<Connector> { endConns[bestI], endConns[bestJ] };
                }
            }
            // For flanges, ensure FL endpoint comes first (ISOGEN convention).
            // The connector with GasketLength > 0 is the FL (flanged) face.
            if (pcfType == "FLANGE" && exportConns.Count == 2)
            {
                try
                {
                    double g0 = exportConns[0].GasketLength;
                    double g1 = exportConns[1].GasketLength;
                    // FL face has gasket; if connector[0] has no gasket but connector[1] does, swap
                    if (g0 < 1e-9 && g1 > 1e-9)
                        exportConns = new List<Connector> { exportConns[1], exportConns[0] };
                }
                catch { }
            }

            component.EndPoints = exportConns.Select(c => ConvertXyz(c.Origin)).ToList();

            // Determine end types — use SKEY suffix when available for accurate end connection
            string skeySuffix = (component.Skey?.Length >= 4)
                ? component.Skey.Substring(component.Skey.Length - 2).ToUpperInvariant()
                : "";
            // Map SKEY suffix to PCF end type
            string? skeyEndType = skeySuffix switch
            {
                "BW" => "BW", "SW" => "SW", "FL" => "FL", "SC" => "SC",
                "PL" => "PL", "CP" => "CP", "GL" => "GL", "CL" => "CL",
                _ => null
            };

            component.EndTypes = exportConns.Select((c, i) =>
            {
                // For flanges, use GasketLength to determine FL vs BW end type
                if (pcfType == "FLANGE")
                {
                    try { return c.GasketLength > 1e-9 ? "FL" : "BW"; }
                    catch { }
                }
                // For valves, elbows, tees, etc. — use SKEY suffix if available
                if (skeyEndType != null && pcfType != "PIPE" && pcfType != "WELD")
                    return skeyEndType;
                return PartTypeClassifier.GetEndType(pcfType, alias, i);
            }).ToList();

            // Outlet bore + CENTRE-POINT for reducers. Plant 3D's iso format
            // places REDUCER-CONCENTRIC/ECCENTRIC in the "2 EP + CP" category;
            // emitting the reducer without a CENTRE-POINT makes the iso
            // generator treat the reducer as malformed and fail to connect the
            // two adjacent pipes, splitting the drawing at the reducer.
            if (pcfType == "REDUCER" && connectors.Count >= 2)
            {
                double bore0 = GetBore(connectors[0]);
                double bore1 = GetBore(connectors[1]);
                if (Math.Abs(bore1 - bore0) > 0.01)
                {
                    component.OutletBore = bore1;
                    component.EndPointBores = new List<double> { bore0, bore1 };
                }
                component.CentrePoint = MidPoint(connectors[0].Origin, connectors[1].Origin);
            }

            // Centre point, branch point for tees
            if (pcfType == "TEE" && connectors.Count >= 3)
            {
                // Identify run pair (anti-parallel BasisZ) vs branch connector
                int runA = 0, runB = 1, branch = 2;
                double bestDot = 0;
                for (int i = 0; i < connectors.Count; i++)
                for (int j = i + 1; j < connectors.Count; j++)
                {
                    double d = connectors[i].CoordinateSystem.BasisZ.DotProduct(
                        connectors[j].CoordinateSystem.BasisZ);
                    if (d < bestDot) { bestDot = d; runA = i; runB = j; }
                }
                branch = Enumerable.Range(0, connectors.Count).First(k => k != runA && k != runB);

                // CENTRE-POINT = midpoint of run connectors
                component.CentrePoint = MidPoint(connectors[runA].Origin, connectors[runB].Origin);

                // Reorder endpoints: run connectors first (EP[0], EP[1]), branch last (EP[2])
                var orderedConns = new List<Connector> { connectors[runA], connectors[runB], connectors[branch] };
                component.EndPoints = orderedConns.Select(c => ConvertXyz(c.Origin)).ToList();
                component.EndTypes = orderedConns.Select((_, i) => PartTypeClassifier.GetEndType(pcfType, alias, i)).ToList();

                // Check if reducing tee (branch bore differs from run bore)
                double runBore = GetBore(connectors[runA]);
                double branchBore = GetBore(connectors[branch]);
                component.Bore = runBore;

                if (Math.Abs(branchBore - runBore) > 0.01)
                {
                    // BRANCH1-POINT with branch bore
                    component.BranchPoint = ConvertXyz(connectors[branch].Origin);
                    component.BranchBore = branchBore;

                    // SIZE = "12''x12''x8''" format — only construct if not already multi-bore
                    string currentSize = component.Size ?? "";
                    if (!currentSize.Contains("x", StringComparison.OrdinalIgnoreCase))
                    {
                        string branchSizeInches = $"{branchBore:F0}''";
                        component.Size = $"{currentSize}x{currentSize}x{branchSizeInches}";
                    }

                    // Differentiate description for BOM separation
                    component.ItemDescription = "Reducing " + component.ItemDescription;
                }

                // Set per-endpoint bores (run bore for EP[0] and EP[1], branch bore for EP[2])
                component.EndPointBores = new List<double> { runBore, runBore, branchBore };
            }

            // Centre point and angle for elbows
            if (pcfType == "ELBOW" && connectors.Count >= 2)
            {
                component.CentrePoint = ConvertXyz(
                    ConnectorHelper.ComputeElbowCenter(connectors[0], connectors[1]));
                component.Angle = ComputeAngle(connectors[0], connectors[1]);
            }

            // Centre point, spindle direction, and tag for valves
            if (pcfType == "VALVE" && connectors.Count >= 2)
            {
                component.CentrePoint      = MidPoint(connectors[0].Origin, connectors[1].Origin);

                // Check valves (VC, NV, CK) don't have operators/spindles — skip spindle direction
                string skey = component.Skey ?? "";
                bool isCheckValve = skey.StartsWith("VC", StringComparison.OrdinalIgnoreCase)
                    || skey.StartsWith("NV", StringComparison.OrdinalIgnoreCase)
                    || skey.StartsWith("CK", StringComparison.OrdinalIgnoreCase);

                if (!isCheckValve)
                    component.SpindleDirection = NullIfEmpty(GetParam(part, "Spindle Direction", "Valve Direction"));

                // If no parameter found, compute from bounding box offset
                // (handwheel/operator makes the bbox asymmetric in the spindle direction)
                if (!isCheckValve && string.IsNullOrWhiteSpace(component.SpindleDirection) && connectors.Count >= 2)
                {
                    try
                    {
                        var bbox = part.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            XYZ connCenter = (connectors[0].Origin + connectors[1].Origin) * 0.5;
                            XYZ pipeAxis = (connectors[1].Origin - connectors[0].Origin);
                            if (pipeAxis.GetLength() > 1e-9)
                            {
                                pipeAxis = pipeAxis.Normalize();
                                XYZ bboxCenter = (bbox.Min + bbox.Max) * 0.5;
                                XYZ offset = bboxCenter - connCenter;
                                XYZ spindleVec = offset - pipeAxis * offset.DotProduct(pipeAxis);
                                if (spindleVec.GetLength() > 1e-6)
                                {
                                    XYZ dir = spindleVec.Normalize();
                                    component.SpindleDirection = VectorToNamedDirection(dir);
                                }
                            }
                        }
                    }
                    catch { }
                }

                component.Tag              = NullIfEmpty(GetParam(part, "Tag", "TAG", "tag"));
            }

            // Centre point and branch point for olets
            // The run-side connector (larger bore) = CENTRE-POINT
            // The branch connector (smaller bore) = BRANCH1-POINT
            if (pcfType == "OLET" && connectors.Count >= 2)
            {
                int runIdx    = connectors[0].Radius >= connectors[1].Radius ? 0 : 1;
                int branchIdx = 1 - runIdx;
                component.CentrePoint = ConvertXyz(connectors[runIdx].Origin);
                component.BranchPoint = ConvertXyz(connectors[branchIdx].Origin);
                component.BranchBore  = UnitUtils.ConvertFromInternalUnits(
                    connectors[branchIdx].Radius * 2, _lengthUnit);
            }

            // Support: single CO-ORDS point + SUPPORT-DIRECTION
            if (pcfType == "SUPPORT")
            {
                // Use the pipe-attachment connector (not the structure-side connector)
                // For hangers (UP): pipe attachment is the LOWEST connector
                // For supports (DOWN): pipe attachment is the HIGHEST connector
                if (connectors.Count > 0)
                {
                    // Determine direction first to pick the right connector
                    string descText = (alias + " " + (component.ItemDescription ?? "")).ToUpperInvariant();
                    bool isHanger = ContainsAny(descText, "HANGER", "HANG", "CLEVIS", "ROD", "TRAPEZE", "BEAM CLAMP");

                    Connector attachConn;
                    if (connectors.Count == 1)
                    {
                        attachConn = connectors[0];
                    }
                    else
                    {
                        // Pick pipe-side connector: lowest Z for hangers, highest Z for floor supports
                        attachConn = isHanger
                            ? connectors.OrderBy(c => c.Origin.Z).First()
                            : connectors.OrderByDescending(c => c.Origin.Z).First();
                    }

                    var coords = ConvertXyz(attachConn.Origin);
                    double bore = GetBore(attachConn);
                    component.SupportCoords = new[] { coords[0], coords[1], coords[2], bore };
                    component.Bore = bore;
                }

                // Determine support direction from description/SKEY/geometry
                // Hangers extend UP (rod to structure above), supports extend DOWN (to floor)
                string supportText = (alias + " " + (component.ItemDescription ?? "") + " " + (component.Skey ?? "")).ToUpperInvariant();
                if (ContainsAny(supportText, "HANGER", "HANG", "CLEVIS", "ROD", "TRAPEZE", "BEAM CLAMP"))
                    component.SupportDirection = "UP";
                else if (ContainsAny(supportText, "DUCK", "SKID", "SHOE", "STANCHION", "STAND", "SADDLE"))
                    component.SupportDirection = "DOWN";
                else
                {
                    // Unknown type — use geometry: if bbox center is above pipe connector, it's a hanger (UP)
                    try
                    {
                        var bbox = part.get_BoundingBox(null);
                        if (bbox != null && connectors.Count > 0)
                        {
                            XYZ bboxCenter = (bbox.Min + bbox.Max) * 0.5;
                            component.SupportDirection = bboxCenter.Z > connectors[0].Origin.Z ? "UP" : "DOWN";
                        }
                        else
                            component.SupportDirection = "DOWN";
                    }
                    catch { component.SupportDirection = "DOWN"; }
                }

                // Clear endpoints — supports use CO-ORDS, not END-POINT
                component.EndPoints.Clear();
                component.EndTypes.Clear();
            }

            return component;
        }

        /// <summary>
        /// Generates GASKET and BOLT PcfComponents from the ancillaries on a flanged part.
        /// Returns empty list for parts with no gasket/bolt ancillaries.
        /// </summary>
        public List<PcfComponent> MapAncillaries(FabricationPart part, PcfComponent parent)
        {
            var result = new List<PcfComponent>();
            // Only extract ancillaries from FLANGE components — valves have built-in flanges
            // whose bolts/gaskets are already covered by the adjacent weld neck flanges
            if (parent.PcfType != "FLANGE") return result;

            try
            {
                var ancillaries = part.GetPartAncillaryUsage();
                if (ancillaries == null || ancillaries.Count == 0) return result;

                var doc = part.Document;
                var config = FabricationConfiguration.GetFabricationConfiguration(doc);

                // Find FL endpoints on the parent for gasket/bolt positioning
                var flPositions = new List<double[]>();
                for (int i = 0; i < parent.EndPoints.Count; i++)
                {
                    string endType = i < parent.EndTypes.Count ? parent.EndTypes[i] : "";
                    if (endType.Equals("FL", StringComparison.OrdinalIgnoreCase))
                        flPositions.Add(parent.EndPoints[i]);
                }
                if (flPositions.Count == 0 && parent.EndPoints.Count > 0)
                    flPositions.Add(parent.EndPoints[0]); // fallback

                // Process gasket ancillaries — coincident endpoints (BOM only, no geometry)
                foreach (var anc in ancillaries)
                {
                    if (anc.Type.ToString().Contains("Gasket", StringComparison.OrdinalIgnoreCase))
                    {
                        string ancName = "(unknown)";
                        try { ancName = config?.GetAncillaryName(anc.AncillaryId) ?? ancName; } catch { }

                        int gasketCount = Math.Min((int)anc.Quantity, flPositions.Count);
                        for (int g = 0; g < gasketCount; g++)
                        {
                            var pos = flPositions[g];

                            var gasket = new PcfComponent
                            {
                                PcfType = "GASKET",
                                RevitUniqueId = parent.RevitUniqueId,
                                EndPoints = new List<double[]> { pos, pos },
                                EndTypes = new List<string> { "", "" },
                                Bore = parent.Bore,
                                ItemDescription = ancName,
                                PipingSpec = parent.PipingSpec,
                                SpoolIdentifier = parent.SpoolIdentifier,
                            };
                            result.Add(gasket);
                        }
                    }
                }

                // Determine pressure class from PIPING-SPEC (e.g., "CS300" → "300")
                // or from connector ancillary name (e.g., "Flange 300")
                string pressureClass = "";
                // Try PIPING-SPEC first — works for both flanges and valves
                string pipingSpec = parent.PipingSpec ?? "";
                var specMatch = System.Text.RegularExpressions.Regex.Match(pipingSpec, @"(\d{3,4})");
                if (specMatch.Success) pressureClass = specMatch.Groups[1].Value;
                // Fallback: check connector ancillary names
                if (string.IsNullOrWhiteSpace(pressureClass))
                {
                    foreach (var anc in ancillaries)
                    {
                        string ancNameCheck = "(unknown)";
                        try { ancNameCheck = config?.GetAncillaryName(anc.AncillaryId) ?? ancNameCheck; } catch { }
                        if (ancNameCheck.StartsWith("Flange", StringComparison.OrdinalIgnoreCase))
                        {
                            var classMatch = System.Text.RegularExpressions.Regex.Match(ancNameCheck, @"(\d+)");
                            if (classMatch.Success) pressureClass = classMatch.Groups[1].Value;
                            break;
                        }
                    }
                }

                // Check if both bolts and nuts exist (= bolt set)
                bool hasBolts = false, hasNuts = false;
                foreach (var anc in ancillaries)
                {
                    if (!anc.Type.ToString().Contains("Fixing", StringComparison.OrdinalIgnoreCase)) continue;
                    string n = "(unknown)";
                    try { n = config?.GetAncillaryName(anc.AncillaryId) ?? n; } catch { }
                    if (n.Contains("Bolt", StringComparison.OrdinalIgnoreCase)) hasBolts = true;
                    if (n.Contains("Nut", StringComparison.OrdinalIgnoreCase)) hasNuts = true;
                }
                bool isBoltSet = hasBolts && hasNuts;

                // Process bolt ancillaries only (nuts are included in the bolt set description)
                foreach (var anc in ancillaries)
                {
                    if (!anc.Type.ToString().Contains("Fixing", StringComparison.OrdinalIgnoreCase)) continue;
                    string ancName = "(unknown)";
                    try { ancName = config?.GetAncillaryName(anc.AncillaryId) ?? ancName; } catch { }
                    if (!ancName.Contains("Bolt", StringComparison.OrdinalIgnoreCase)) continue;

                    // Parse diameter from name (e.g., "7/8'' Bolt (ASME B18.2.1)")
                    string dia = "";
                    var diaMatch = System.Text.RegularExpressions.Regex.Match(ancName, @"^([\d/\-]+)[''\""]");
                    if (diaMatch.Success) dia = diaMatch.Groups[1].Value;

                    // Per-bolt length
                    double boltLength = 0;
                    if (anc.Quantity > 0 && anc.Length > 0)
                        boltLength = anc.Length / anc.Quantity;

                    // Bolt quantity per FL endpoint
                    int qtyPerEnd = flPositions.Count > 0 ? (int)anc.Quantity / flPositions.Count : (int)anc.Quantity;

                    // Build description: use "BOLT SET, 300 LB, STUD BOLT" only for Plant 3D compatibility
                    string boltDesc;
                    if (_options.UseStandardDescriptions && isBoltSet && !string.IsNullOrWhiteSpace(pressureClass))
                        boltDesc = $"BOLT SET, {pressureClass} LB, STUD BOLT";
                    else if (_options.UseStandardDescriptions && isBoltSet)
                        boltDesc = "BOLT SET, STUD BOLT";
                    else
                        boltDesc = ancName;

                    for (int b = 0; b < flPositions.Count; b++)
                    {
                        var pos = flPositions[b];
                        result.Add(new PcfComponent
                        {
                            PcfType = "BOLT",
                            RevitUniqueId = parent.RevitUniqueId,
                            SupportCoords = new[] { pos[0], pos[1], pos[2], parent.Bore },
                            Bore = parent.Bore,
                            ItemDescription = boltDesc,
                            PipingSpec = parent.PipingSpec,
                            SpoolIdentifier = parent.SpoolIdentifier,
                            BoltDiameter = dia,
                            BoltQuantity = qtyPerEnd,
                            BoltLength = boltLength,
                        });
                    }
                }
            }
            catch { }

            return result;
        }

        // ── Parameter lookup helpers ─────────────────────────────────────────

        /// <summary>
        /// Tries each parameter name in order and returns the first non-empty string value found.
        /// </summary>
        private static string? GetParam(FabricationPart part, params string[] names)
        {
            foreach (string name in names)
            {
                string? val = part.LookupParameter(name)?.AsString();
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
            return null;
        }

        private static bool HasInsulationApplied(FabricationPart part)
        {
            try
            {
                string? insType = part.LookupParameter("Insulation Type")?.AsString();
                if (!string.IsNullOrWhiteSpace(insType)) return true;
                double? insThick = part.LookupParameter("Insulation Thickness")?.AsDouble();
                return insThick.HasValue && insThick.Value > 0.001;
            }
            catch { return false; }
        }

        private double GetInsulationThickness(FabricationPart part)
        {
            try
            {
                double? thickFeet = part.LookupParameter("Insulation Thickness")?.AsDouble();
                if (!thickFeet.HasValue || thickFeet.Value < 0.001) return 0;
                return UnitUtils.ConvertFromInternalUnits(thickFeet.Value, _lengthUnit);
            }
            catch { return 0; }
        }

        /// <summary>
        /// Converts a unit vector to the nearest named direction (UP, DOWN, NORTH, SOUTH, EAST, WEST).
        /// Revit: X=East/West, Y=North/South, Z=Up/Down.
        /// </summary>
        private static string VectorToNamedDirection(XYZ dir)
        {
            double absX = Math.Abs(dir.X), absY = Math.Abs(dir.Y), absZ = Math.Abs(dir.Z);

            if (absZ >= absX && absZ >= absY)
                return dir.Z > 0 ? "UP" : "DOWN";
            if (absX >= absY)
                return dir.X > 0 ? "EAST" : "WEST";
            return dir.Y > 0 ? "NORTH" : "SOUTH";
        }

        private static string? NullIfEmpty(string? s)
            => string.IsNullOrWhiteSpace(s) ? null : s;

        private static bool ContainsAny(string source, params string[] tokens)
            => tokens.Any(t => source.Contains(t, StringComparison.OrdinalIgnoreCase));

        // ── Data extraction ──────────────────────────────────────────────────

        private double GetWeight(FabricationPart part)
        {
            try
            {
                double weightKg = part.Weight;
                if (_options.Units == PcfUnits.Inch)
                    return weightKg * 2.20462; // kg → lbs
                return weightKg;
            }
            catch
            {
                double? raw = part.LookupParameter("Weight")?.AsDouble();
                return raw.HasValue
                    ? UnitUtils.ConvertFromInternalUnits(raw.Value, _lengthUnit)
                    : 0.0;
            }
        }

        // ── Coordinate / geometry helpers ────────────────────────────────────

        private double[] ConvertXyz(XYZ xyz) => new[]
        {
            UnitUtils.ConvertFromInternalUnits(xyz.X, _lengthUnit),
            UnitUtils.ConvertFromInternalUnits(xyz.Y, _lengthUnit),
            UnitUtils.ConvertFromInternalUnits(xyz.Z, _lengthUnit),
        };

        private double[] MidPoint(XYZ a, XYZ b)
            => ConvertXyz(new XYZ((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2));

        private double GetBore(Connector? connector)
        {
            if (connector == null) return 0.0;
            double diameterFeet = connector.Shape == ConnectorProfileType.Round
                ? connector.Radius * 2.0
                : connector.Width;
            return UnitUtils.ConvertFromInternalUnits(diameterFeet, _lengthUnit);
        }

        private static int ComputeAngle(Connector c0, Connector c1)
        {
            var    d0      = c0.CoordinateSystem.BasisZ;
            var    d1      = c1.CoordinateSystem.BasisZ;
            double dot     = Math.Max(-1.0, Math.Min(1.0, d0.DotProduct(d1)));
            double degrees = Math.Acos(dot) * 180.0 / Math.PI;
            return (int)Math.Round(degrees * 100);
        }
    }
}
