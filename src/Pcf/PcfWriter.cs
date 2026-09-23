using System.IO;
using PcfExport.Models;

namespace PcfExport.Pcf
{
    /// <summary>
    /// Writes PCF (Piping Component File) format compatible with ISOGEN,
    /// using property mappings from pcfmapping.xlsx.
    /// </summary>
    internal sealed class PcfWriter : IDisposable
    {
        private const string I = "    "; // 4-space indent for sub-fields
        private readonly StreamWriter _writer;

        // Groups unique (FabricationCid + Description) pairs to sequential ITEM-CODEs.
        // Different component types can share a CID (e.g., flanges and welds both CID=2522),
        // so we key by CID+Description to keep them as separate BOM line items.
        private readonly Dictionary<string, int>  _cidDescToItemCode = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string>  _itemCodeToDesc    = new();
        private readonly Dictionary<int, string>  _itemCodeToMaterial = new();
        private readonly Dictionary<int, string>  _itemCodeToSchedule = new();
        private readonly Dictionary<int, string>  _itemCodeToPressureClass = new();

        public PcfWriter(string filePath)
        {
            _writer = new StreamWriter(filePath, append: false, encoding: System.Text.Encoding.ASCII);
        }

        public void WriteHeader(ExportOptions options)
        {
            string unit   = options.Units == PcfUnits.MM ? "MM"   : "INCH";
            string wtUnit = options.Units == PcfUnits.MM ? "KGS"  : "LBS";
            string wtLen  = options.Units == PcfUnits.MM ? "MM"   : "FEET";
            string date   = DateTime.Now.ToString("dd/MM/yyyy");

            if (!string.IsNullOrWhiteSpace(options.IsogenFlsFile))
                _writer.WriteLine($"ISOGEN-FILES {options.IsogenFlsFile}");
            else
                _writer.WriteLine("ISOGEN-FILES");

            _writer.WriteLine($"UNITS-BORE {unit}");
            _writer.WriteLine($"UNITS-CO-ORDS {unit}");
            _writer.WriteLine($"UNITS-BOLT-LENGTH {unit}");
            _writer.WriteLine($"UNITS-BOLT-DIA {unit}");
            _writer.WriteLine($"UNITS-WEIGHT {wtUnit}");
            _writer.WriteLine($"UNITS-WEIGHT-LENGTH {wtLen}");
            _writer.WriteLine();

            _writer.WriteLine($"PIPELINE-REFERENCE {options.PipelineReference}");
            _writer.WriteLine($"{I}DATE-DMY  {date}");
            _writer.WriteLine($"{I}ATTRIBUTE1  {options.Attribute1}");
            _writer.WriteLine($"{I}ATTRIBUTE2  {options.Attribute2}");
            _writer.WriteLine($"{I}ATTRIBUTE3  {options.Attribute3}");
            _writer.WriteLine($"{I}ATTRIBUTE4  {options.Attribute4}");
            _writer.WriteLine($"{I}ATTRIBUTE5  {options.Attribute5}");
            _writer.WriteLine($"{I}ATTRIBUTE6  {options.Attribute6}");
            _writer.WriteLine($"{I}ATTRIBUTE7  {options.Attribute7}");
            _writer.WriteLine($"{I}ATTRIBUTE8  {options.Attribute8}");
            _writer.WriteLine($"{I}ATTRIBUTE9  {options.Attribute9}");
            _writer.WriteLine($"{I}PROJECT-IDENTIFIER  {options.ProjectIdentifier}");
            _writer.WriteLine($"{I}REVISION  {options.Revision}");
            _writer.WriteLine($"{I}AREA  {options.Area}");
            if (!string.IsNullOrWhiteSpace(options.PipingSpec))
                _writer.WriteLine($"{I}PIPING-SPEC  {options.PipingSpec}");
            if (!string.IsNullOrWhiteSpace(options.InsulationSpec))
                _writer.WriteLine($"{I}INSULATION-SPEC  {options.InsulationSpec}");
            _writer.WriteLine();
        }

        public void WriteOpenEnd(double[] coords)
        {
            _writer.WriteLine("END-POSITION-OPEN");
            _writer.WriteLine($"{I}CO-ORDS {FormatCoords(coords)}");
        }

        /// <summary>
        /// Formats 3-or-4 element coord arrays as "X Y Z [Bore]". Bore is required
        /// by Plant 3D's PCF-to-Iso tool at open ends; omitting it is read as
        /// Size=0 and causes a size mismatch against the adjacent pipe.
        /// </summary>
        private string FormatCoords(double[] coords)
        {
            if (coords.Length >= 4)
                return $"{F(coords[0])} {F(coords[1])} {F(coords[2])} {F(coords[3])}";
            return $"{F(coords[0])} {F(coords[1])} {F(coords[2])}";
        }

        /// <summary>
        /// Writes all open ends, assigning connections by proximity to the selected element's position.
        /// </summary>
        public void WriteOpenEndsWithConnections(List<double[]> openEnds, PcfConnection? connFrom, PcfConnection? connTo)
        {
            // Build list of connections to match
            var connections = new List<PcfConnection>();
            if (connFrom != null) connections.Add(connFrom);
            if (connTo != null) connections.Add(connTo);

            // Match each connection to its closest open end
            var matched = new Dictionary<int, PcfConnection>(); // openEnd index → connection
            var usedConns = new HashSet<PcfConnection>();

            foreach (var conn in connections)
            {
                if (conn.Position == null || conn.Position.Length < 3) continue;

                int bestIdx = -1;
                double bestDist = double.MaxValue;
                for (int i = 0; i < openEnds.Count; i++)
                {
                    if (matched.ContainsKey(i)) continue; // already assigned
                    var oe = openEnds[i];
                    double dist = Math.Sqrt(
                        Math.Pow(oe[0] - conn.Position[0], 2) +
                        Math.Pow(oe[1] - conn.Position[1], 2) +
                        Math.Pow(oe[2] - conn.Position[2], 2));
                    if (dist < bestDist) { bestDist = dist; bestIdx = i; }
                }
                if (bestIdx >= 0)
                    matched[bestIdx] = conn;
            }

            // Write each open end
            for (int i = 0; i < openEnds.Count; i++)
            {
                var coords = openEnds[i];

                if (matched.TryGetValue(i, out var match))
                {
                    if (match.Type == PcfConnection.ConnectionType.Equipment)
                    {
                        _writer.WriteLine("END-CONNECTION-EQUIPMENT");
                        _writer.WriteLine($"{I}CO-ORDS          {FormatCoords(coords)}");
                        _writer.WriteLine($"{I}CONNECTION-REFERENCE     {match.Reference}");
                    }
                    else
                    {
                        _writer.WriteLine("END-CONNECTION-PIPELINE");
                        _writer.WriteLine($"{I}CO-ORDS          {FormatCoords(coords)}");
                        _writer.WriteLine($"{I}PIPELINE-REFERENCE    {match.Reference}");
                    }
                }
                else
                {
                    _writer.WriteLine("END-POSITION-OPEN");
                    _writer.WriteLine($"{I}CO-ORDS {FormatCoords(coords)}");
                }
            }
        }

        public void WriteBlankLine() => _writer.WriteLine();

        public void WriteComponent(PcfComponent component, int sequence)
        {
            // Assign ITEM-CODE by FabricationCid grouping, store material/schedule/pressure-class.
            // Welds are excluded from BOM (weld list only) — no ITEM-CODE needed.
            // Material preference: MaterialAbbreviation (e.g. "CS") > Material (full name).
            if (!component.PcfType.Equals("WELD", StringComparison.OrdinalIgnoreCase))
            {
                string? materialForMatSection = !string.IsNullOrWhiteSpace(component.MaterialAbbreviation)
                    ? component.MaterialAbbreviation
                    : component.Material;
                component.ItemCode = GetOrAddItemCode(component.FabricationCid, component.ItemDescription,
                    materialForMatSection, component.Schedule, component.PressureClass);
            }

            bool isSupport = component.PcfType.Equals("SUPPORT", StringComparison.OrdinalIgnoreCase);
            bool isGasket = component.PcfType.Equals("GASKET", StringComparison.OrdinalIgnoreCase);
            bool isBolt = component.PcfType.Equals("BOLT", StringComparison.OrdinalIgnoreCase);
            // NUT type is not used — nuts are included in BOLT as "BOLT SET"

            // REDUCER needs to be written as REDUCER-CONCENTRIC or REDUCER-ECCENTRIC
            // (per PCF format spec); bare "REDUCER" is unrecognized by Plant 3D's iso tool
            // and the fitting fails to connect its two adjacent pipes, splitting the drawing.
            string keyword = component.PcfType;
            if (keyword.Equals("REDUCER", StringComparison.OrdinalIgnoreCase))
            {
                string skeyPrefix = (component.Skey ?? "").Length >= 2
                    ? component.Skey.Substring(0, 2).ToUpperInvariant()
                    : "";
                keyword = skeyPrefix == "RE" ? "REDUCER-ECCENTRIC" : "REDUCER-CONCENTRIC";
            }
            _writer.WriteLine(keyword);

            // ── GASKET format ────────────────────────────────────────────────
            if (isGasket)
            {
                for (int i = 0; i < component.EndPoints.Count; i++)
                {
                    var pt = component.EndPoints[i];
                    _writer.WriteLine($"{I}END-POINT        {F(pt[0])}    {F(pt[1])}    {F(pt[2])}    {F(component.Bore)}");
                }
                component.ItemCode = GetOrAddItemCode(0, component.ItemDescription ?? "Gasket");
                _writer.WriteLine($"{I}ITEM-CODE  {component.ItemCode}");
                if (!string.IsNullOrWhiteSpace(component.ItemDescription))
                    _writer.WriteLine($"{I}ITEM-DESCRIPTION  {component.ItemDescription}");
                _writer.WriteLine($"{I}ERECTION-ITEM");
                _writer.WriteLine($"{I}STATUS UNDIMENSIONED");
                if (!string.IsNullOrWhiteSpace(component.PipingSpec))
                    _writer.WriteLine($"{I}PIPING-SPEC  {component.PipingSpec}");
                _writer.WriteLine();
                return;
            }

            // ── BOLT format ──────────────────────────────────────────────────
            if (isBolt)
            {
                if (component.SupportCoords is not null)
                    _writer.WriteLine($"{I}CO-ORDS          {F(component.SupportCoords[0])}    {F(component.SupportCoords[1])}    {F(component.SupportCoords[2])}    {F(component.SupportCoords[3])}");
                if (!string.IsNullOrWhiteSpace(component.BoltDiameter))
                    _writer.WriteLine($"{I}BOLT-DIA  {component.BoltDiameter}");
                string boltDesc = component.ItemDescription ?? "Bolt Set";
                component.ItemCode = GetOrAddItemCode(0, boltDesc);
                _writer.WriteLine($"{I}BOLT-ITEM-CODE  {component.ItemCode}");
                if (!string.IsNullOrWhiteSpace(component.ItemDescription))
                    _writer.WriteLine($"{I}ITEM-DESCRIPTION  {component.ItemDescription}");
                _writer.WriteLine($"{I}ERECTION-ITEM");
                _writer.WriteLine($"{I}STATUS UNDIMENSIONED");
                if (component.BoltQuantity > 0)
                    _writer.WriteLine($"{I}BOLT-QUANTITY  {component.BoltQuantity}");
                if (component.BoltLength > 0)
                    _writer.WriteLine($"{I}BOLT-LENGTH  {component.BoltLength:F4}");
                if (!string.IsNullOrWhiteSpace(component.PipingSpec))
                    _writer.WriteLine($"{I}PIPING-SPEC  {component.PipingSpec}");
                _writer.WriteLine();
                return;
            }


            // ── SUPPORT uses CO-ORDS format ──────────────────────────────────
            if (isSupport)
            {
                if (component.SupportCoords is not null)
                    _writer.WriteLine($"{I}CO-ORDS          {F(component.SupportCoords[0])}    {F(component.SupportCoords[1])}    {F(component.SupportCoords[2])}    {F(component.SupportCoords[3])}");

                if (!string.IsNullOrWhiteSpace(component.Skey))
                    _writer.WriteLine($"{I}SKEY  {component.Skey}");

                _writer.WriteLine($"{I}ITEM-CODE  {component.ItemCode}");
                _writer.WriteLine($"{I}ITEM-DESCRIPTION  {component.ItemDescription}");
                _writer.WriteLine($"{I}FABRICATION-ITEM");

                if (!string.IsNullOrWhiteSpace(component.SupportDirection))
                    _writer.WriteLine($"{I}SUPPORT-DIRECTION  {component.SupportDirection}");

                if (!string.IsNullOrWhiteSpace(component.PipingSpec))
                    _writer.WriteLine($"{I}PIPING-SPEC  {component.PipingSpec}");

                _writer.WriteLine();
                return;
            }

            // ── Standard component format (PIPE, ELBOW, TEE, etc.) ───────────

            // OLET per PCF format spec belongs in the "CP + BRANCH1-POINT only,
            // no run EPs" category — writing END-POINT lines makes Plant 3D read
            // the olet as an inline 2-ended fitting (both ends inheriting the
            // run bore), which disconnects the branch pipe on bore mismatch and
            // fragments the iso into multiple drawings.
            bool skipEndPoints =
                component.PcfType.Equals("OLET", StringComparison.OrdinalIgnoreCase);

            // END-POINT lines: x y z bore end-type
            if (!skipEndPoints)
            {
                for (int i = 0; i < component.EndPoints.Count; i++)
                {
                    var    pt      = component.EndPoints[i];
                    string endType = i < component.EndTypes.Count ? component.EndTypes[i] : "BW";
                    double bore = (i < component.EndPointBores.Count && component.EndPointBores[i] > 0)
                        ? component.EndPointBores[i]
                        : component.Bore;
                    _writer.WriteLine($"{I}END-POINT  {F(pt[0])}  {F(pt[1])}  {F(pt[2])}  {F(bore)}  {endType}");
                }
            }

            // CENTRE-POINT (elbows, valves, olets)
            if (component.CentrePoint is not null)
                _writer.WriteLine($"{I}CENTRE-POINT  {F(component.CentrePoint[0])} {F(component.CentrePoint[1])} {F(component.CentrePoint[2])}");

            // BRANCH1-POINT (olets: branch endpoint with bore)
            if (component.BranchPoint is not null)
            {
                double branchBore = component.BranchBore ?? component.Bore;
                string branchEndType = component.EndTypes.Count > 0 ? component.EndTypes[0] : "BW";
                _writer.WriteLine($"{I}BRANCH1-POINT  {F(component.BranchPoint[0])} {F(component.BranchPoint[1])} {F(component.BranchPoint[2])} {F(branchBore)} {branchEndType}");
            }

            // SKEY — from "PCF SKEY" fabrication parameter (pipes don't have SKEY per ISOGEN standard)
            if (!string.IsNullOrWhiteSpace(component.Skey) &&
                !component.PcfType.Equals("PIPE", StringComparison.OrdinalIgnoreCase))
                _writer.WriteLine($"{I}SKEY {component.Skey}");

            // Valve spindle / direction / tag
            if (!string.IsNullOrWhiteSpace(component.SpindleDirection))
            {
                _writer.WriteLine($"{I}SPINDLE-DIRECTION  {component.SpindleDirection}");
                _writer.WriteLine($"{I}DIRECTION  {component.SpindleDirection}");
            }
            if (!string.IsNullOrWhiteSpace(component.Tag))
                _writer.WriteLine($"{I}TAG  {component.Tag}");

            // Flow direction (check valves)
            if (component.FlowDirection > 0)
                _writer.WriteLine($"{I}FLOW  {component.FlowDirection}");

            // Elbow angle (hundredths of a degree)
            if (component.Angle.HasValue)
                _writer.WriteLine($"{I}ANGLE  {component.Angle.Value}");

            // Material / identity fields — welds skip ITEM-CODE (keeps them out of BOM)
            bool isWeld = component.PcfType.Equals("WELD", StringComparison.OrdinalIgnoreCase);
            if (!isWeld)
                _writer.WriteLine($"{I}ITEM-CODE  {component.ItemCode}");
            _writer.WriteLine($"{I}ITEM-DESCRIPTION  {component.ItemDescription}");
            // Field/site welds are ERECTION-ITEM; all others are FABRICATION-ITEM
            bool isFieldWeld = isWeld && (component.Skey?.Equals("WS", StringComparison.OrdinalIgnoreCase) == true
                || component.Skey?.Equals("WF", StringComparison.OrdinalIgnoreCase) == true);
            _writer.WriteLine(isFieldWeld ? $"{I}ERECTION-ITEM" : $"{I}FABRICATION-ITEM");

            // PIPING-SPEC — from FabricationPart.Specification
            if (!string.IsNullOrWhiteSpace(component.PipingSpec))
                _writer.WriteLine($"{I}PIPING-SPEC  {component.PipingSpec}");

            // COMPONENT-ATTRIBUTE2/3 — Plant 3D's PCF-to-Pipe matcher reads these to
            // disambiguate among multiple catalog candidates that share a SKEY (e.g.
            // ELL 45 LR vs ELL 90 LR under ELBW, or Gate Valve Solid Wedge vs Double
            // Disc under VTFL). Without them, ambiguous components fall through to
            // "Part not found, centerline used" even when the description matches.
            // Convention: ATTRIBUTE1 = service (skipped — we don't map it),
            //             ATTRIBUTE2 = BOMCOLUMN_Material_{abbrev},
            //             ATTRIBUTE3 = BOMCOLUMN_SCHClass_{schedule or pressure class}.
            if (!isWeld && !string.IsNullOrWhiteSpace(component.MaterialAbbreviation))
                _writer.WriteLine($"{I}COMPONENT-ATTRIBUTE2   BOMCOLUMN_Material_{component.MaterialAbbreviation}");
            if (!isWeld)
            {
                // Flanges and valves use pressure class; pipes and BW/SW fittings use
                // schedule. Fall back to whichever is populated.
                bool isFlange = component.PcfType.Equals("FLANGE", StringComparison.OrdinalIgnoreCase);
                bool isValveType = component.PcfType.Equals("VALVE", StringComparison.OrdinalIgnoreCase);
                string? schClassValue = (isFlange || isValveType)
                    ? (component.PressureClass ?? component.Schedule)
                    : (component.Schedule ?? component.PressureClass);
                if (!string.IsNullOrWhiteSpace(schClassValue))
                    _writer.WriteLine($"{I}COMPONENT-ATTRIBUTE3   BOMCOLUMN_SCHClass_{schClassValue}");
            }

            // SIZE — from ProductSizeDescription (suppress for welds)
            if (!isWeld && !string.IsNullOrWhiteSpace(component.Size))
                _writer.WriteLine($"{I}SIZE  {component.Size}");

            // SPECIFICATION — suppress for welds and valves
            bool isValve = component.PcfType.Equals("VALVE", StringComparison.OrdinalIgnoreCase);
            if (!isWeld && !isValve && !string.IsNullOrWhiteSpace(component.ProductSpecification))
                _writer.WriteLine($"{I}SPECIFICATION  {component.ProductSpecification}");

            if (!string.IsNullOrWhiteSpace(component.SpoolIdentifier))
                _writer.WriteLine($"{I}SPOOL-IDENTIFIER  {component.SpoolIdentifier}");

            _writer.WriteLine($"{I}UNIQUE-COMPONENT-IDENTIFIER  {component.RevitUniqueId}");

            if (component.FabricationCid != 0)
                _writer.WriteLine($"{I}ITEM-ATTRIBUTE0 CID=[{component.FabricationCid}]");

            // Insulation
            if (component.HasInsulation)
            {
                _writer.WriteLine($"{I}INSULATION-ON");
                if (component.InsulationThickness > 0)
                    _writer.WriteLine($"{I}COMPONENT-ATTRIBUTE1   INSULATIONTHICKNESS_{component.InsulationThickness:F1}");
            }

            _writer.WriteLine($"{I}WEIGHT  {W(component.Weight)}");

            _writer.WriteLine();
        }

        /// <summary>
        /// Adds ancillary components (gaskets, bolts) to the MATERIALS section only,
        /// without writing component blocks. This puts them in the BOM/FASTENERS
        /// without adding geometry that causes overdimensioning in iso drawings.
        /// </summary>
        public void AddMaterialsOnly(List<PcfComponent> ancillaries)
        {
            foreach (var anc in ancillaries)
            {
                string desc = anc.ItemDescription ?? "(unknown)";
                // Build a unique key including quantity for bolts
                string key = anc.PcfType == "BOLT" && anc.BoltQuantity > 0
                    ? $"{desc} (qty {anc.BoltQuantity})"
                    : desc;
                GetOrAddItemCode(0, key);
            }
        }

        /// <summary>Writes the MATERIALS section at the end of the file.</summary>
        public void WriteMaterials()
        {
            if (_itemCodeToDesc.Count == 0) return;

            _writer.WriteLine("MATERIALS");
            foreach (var (code, description) in _itemCodeToDesc.OrderBy(kv => kv.Key))
            {
                _writer.WriteLine($"ITEM-CODE  {code}");
                _writer.WriteLine($"{I}DESCRIPTION  {description}");
                if (_itemCodeToMaterial.TryGetValue(code, out string? mat) && !string.IsNullOrWhiteSpace(mat))
                    _writer.WriteLine($"{I}Material  {mat}");
                if (_itemCodeToPressureClass.TryGetValue(code, out string? pc) && !string.IsNullOrWhiteSpace(pc))
                    _writer.WriteLine($"{I}PressureClass  {pc}");
                if (_itemCodeToSchedule.TryGetValue(code, out string? sch) && !string.IsNullOrWhiteSpace(sch))
                    _writer.WriteLine($"{I}Schedule  {sch}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Groups components by FabricationCid + Description.
        /// Each unique combination gets one sequential ITEM-CODE number.
        /// This prevents different component types (e.g., Slip-On Flange vs Weld Neck Flange)
        /// from merging into the same BOM row when they share a FabricationCid.
        /// </summary>
        private int GetOrAddItemCode(int cid, string description,
            string? material = null, string? schedule = null, string? pressureClass = null)
        {
            string desc = string.IsNullOrWhiteSpace(description) ? "(unknown)" : description;
            string key = $"{cid}|{desc}";
            if (!_cidDescToItemCode.TryGetValue(key, out int code))
            {
                code = _cidDescToItemCode.Count + 1;
                _cidDescToItemCode[key] = code;
                _itemCodeToDesc[code]   = desc;
                if (!string.IsNullOrWhiteSpace(material))
                    _itemCodeToMaterial[code] = material;
                if (!string.IsNullOrWhiteSpace(schedule))
                    _itemCodeToSchedule[code] = schedule;
                if (!string.IsNullOrWhiteSpace(pressureClass))
                    _itemCodeToPressureClass[code] = pressureClass;
            }
            return code;
        }

        private static string F(double value) => value.ToString("F4");
        private static string W(double value)  => value.ToString("F3");

        public void Dispose() => _writer.Dispose();
    }
}
