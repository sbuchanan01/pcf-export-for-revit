namespace PcfExport.Models
{
    /// <summary>
    /// Intermediate representation of a single PCF component,
    /// independent of both Revit API types and PCF file format details.
    /// </summary>
    public class PcfComponent
    {
        /// <summary>PCF keyword, e.g. "PIPE", "ELBOW", "TEE", "WELD".</summary>
        public string PcfType { get; set; } = string.Empty;

        /// <summary>ISOGEN shape key from the "PCF SKEY" fabrication parameter, e.g. "ELBW", "FLWN".</summary>
        public string Skey { get; set; } = string.Empty;

        /// <summary>Revit UniqueId — written as UNIQUE-COMPONENT-IDENTIFIER.</summary>
        public string RevitUniqueId { get; set; } = string.Empty;

        /// <summary>FabricationPart.ItemCustomId — used for ITEM-CODE grouping and CID attribute.</summary>
        public int FabricationCid { get; set; }

        /// <summary>Sequential item code assigned during export, grouping by FabricationCid.</summary>
        public int ItemCode { get; set; }

        /// <summary>Full catalog description from ProductLongDescription — written as ITEM-DESCRIPTION.</summary>
        public string ItemDescription { get; set; } = string.Empty;

        /// <summary>Per-component piping specification from FabricationPart.Specification.</summary>
        public string PipingSpec { get; set; } = string.Empty;

        /// <summary>Service abbreviation — written as SERVICE if present.</summary>
        public string? ServiceAbbreviation { get; set; }

        /// <summary>Service/system name — written as SYSTEM-NAME if present.</summary>
        public string? SystemName { get; set; }

        /// <summary>Product size description — written as SIZE if present.</summary>
        public string? Size { get; set; }

        /// <summary>Product material description (full name, e.g. "Carbon Steel").</summary>
        public string? Material { get; set; }

        /// <summary>Material abbreviation (e.g. "CS") — written in MATERIALS section.</summary>
        public string? MaterialAbbreviation { get; set; }

        /// <summary>Pipe schedule (e.g. "40", "80", "STD"). Written in MATERIALS section.</summary>
        public string? Schedule { get; set; }

        /// <summary>
        /// Pressure class as digits only (e.g. "300" parsed from "CS300").
        /// Written in MATERIALS section.
        /// </summary>
        public string? PressureClass { get; set; }

        /// <summary>Product specification description — written as SPECIFICATION if present.</summary>
        public string? ProductSpecification { get; set; }

        /// <summary>Spool identifier stamped on the component.</summary>
        public string SpoolIdentifier { get; set; } = string.Empty;

        /// <summary>Weight in the target unit (lbs or kg).</summary>
        public double Weight { get; set; }

        /// <summary>
        /// Connection endpoints in the target unit (mm or inches).
        /// Each entry is [X, Y, Z].
        /// </summary>
        public List<double[]> EndPoints { get; set; } = new();

        /// <summary>
        /// Connection end type per endpoint, e.g. "BW", "FL", "PL", "SW".
        /// Parallel to EndPoints.
        /// </summary>
        public List<string> EndTypes { get; set; } = new();

        /// <summary>
        /// Per-endpoint bore values for components with mixed bores (reducing tees, reducers).
        /// Parallel to EndPoints. Empty list means use component.Bore for all endpoints.
        /// </summary>
        public List<double> EndPointBores { get; set; } = new();

        /// <summary>Centre point for elbows, valves, and olets [X, Y, Z], null for other types.</summary>
        public double[]? CentrePoint { get; set; }

        /// <summary>Branch point for olets [X, Y, Z, Bore], null for non-olets.</summary>
        public double[]? BranchPoint { get; set; }

        /// <summary>Branch bore for olets/reducing components. Null when not applicable.</summary>
        public double? BranchBore { get; set; }

        /// <summary>Nominal bore/diameter in the target unit (inlet bore).</summary>
        public double Bore { get; set; }

        /// <summary>Outlet bore for reducers when it differs from the inlet bore.</summary>
        public double? OutletBore { get; set; }

        /// <summary>Elbow angle in hundredths of a degree, e.g. 9000 = 90.00°.</summary>
        public int? Angle { get; set; }

        /// <summary>Valve spindle direction string, e.g. "EAST". Null for non-valves.</summary>
        public string? SpindleDirection { get; set; }

        /// <summary>Valve tag from the "Tag" shared parameter. Null for non-valves.</summary>
        public string? Tag { get; set; }

        /// <summary>Flow direction: 0=unset, 1=EP1→EP2, 2=EP2→EP1.</summary>
        public int FlowDirection { get; set; }

        /// <summary>Whether insulation is applied to this component.</summary>
        public bool HasInsulation { get; set; }

        /// <summary>Insulation thickness in target units (inches or mm). 0 if no insulation.</summary>
        public double InsulationThickness { get; set; }

        /// <summary>Support direction (UP, DOWN, etc.). Null for non-supports.</summary>
        public string? SupportDirection { get; set; }

        /// <summary>Bolt diameter string (e.g. "7/8"). Null for non-bolts.</summary>
        public string? BoltDiameter { get; set; }

        /// <summary>Bolt quantity per connection. 0 for non-bolts.</summary>
        public int BoltQuantity { get; set; }

        /// <summary>Individual bolt length in target units. 0 for non-bolts.</summary>
        public double BoltLength { get; set; }

        /// <summary>Support attachment coordinate [X, Y, Z, Bore]. Null for non-supports.</summary>
        public double[]? SupportCoords { get; set; }
    }
}
