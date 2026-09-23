namespace PcfExport.Models
{
    public enum PcfUnits { MM, Inch }

    public enum SelectionMode
    {
        CurrentSelection,
        PickElements,
        AllInActiveView
    }

    public class ExportOptions
    {
        public string OutputFilePath   { get; set; } = string.Empty;

        /// <summary>
        /// Output folder used when exporting multiple line numbers.
        /// Each line number is written to {OutputFolderPath}\{lineNumber}.pcf.
        /// </summary>
        public string OutputFolderPath { get; set; } = string.Empty;

        /// <summary>Coordinate and bore units written to the PCF file.</summary>
        public PcfUnits Units { get; set; } = PcfUnits.Inch;

        // ── ISOGEN-FILES header ──────────────────────────────────────────────
        /// <summary>ISOGEN .FLS filename (e.g. "ISOGEN.FLS"). Written inline on ISOGEN-FILES line.</summary>
        public string IsogenFlsFile { get; set; } = "ISOGEN.FLS";

        // ── Pipeline header fields ───────────────────────────────────────────
        /// <summary>Pipeline reference / line number used in PCF header.</summary>
        public string PipelineReference { get; set; } = string.Empty;

        public string Revision { get; set; } = string.Empty;
        public string ProjectIdentifier { get; set; } = string.Empty;
        public string Area { get; set; } = string.Empty;

        /// <summary>Piping specification written in the pipeline header block (e.g. "ASME B16.5").</summary>
        public string PipingSpec { get; set; } = string.Empty;

        /// <summary>Insulation specification written in the pipeline header block.</summary>
        public string InsulationSpec { get; set; } = string.Empty;

        // ── Pipeline attributes (ATTRIBUTE1-9 under PIPELINE-REFERENCE) ─────
        public string Attribute1 { get; set; } = string.Empty; // Fabrication Service Abbreviation
        public string Attribute2 { get; set; } = string.Empty; // Fabrication Material
        public string Attribute3 { get; set; } = string.Empty; // Fabrication Insulation Spec Name
        public string Attribute4 { get; set; } = string.Empty; // Fabrication Insulation Spec Size
        public string Attribute5 { get; set; } = string.Empty;
        public string Attribute6 { get; set; } = string.Empty;
        public string Attribute7 { get; set; } = string.Empty; // Line Number
        public string Attribute8 { get; set; } = string.Empty;
        public string Attribute9 { get; set; } = string.Empty; // Project file name

        // ── Per-component fields ─────────────────────────────────────────────
        /// <summary>Spool identifier stamped on every component (often matches the pipeline reference).</summary>
        public string SpoolIdentifier { get; set; } = string.Empty;

        // ── Selection / filter ───────────────────────────────────────────────
        /// <summary>How to gather elements for export.</summary>
        public SelectionMode SelectionMode { get; set; } = SelectionMode.CurrentSelection;

        public bool IncludePipes { get; set; } = true;
        public bool IncludeFittings { get; set; } = true;
        public bool IncludeWelds { get; set; } = true;
        public bool IncludeHangers { get; set; } = false;

        /// <summary>
        /// When true, ITEM-DESCRIPTION is reformatted to follow standard naming conventions
        /// compatible with Plant 3D's PCF to Pipe tool (e.g., "FLANGE WN, RF, ASME B16.5").
        /// When false (default), uses Revit fabrication part names as-is.
        /// </summary>
        public bool UseStandardDescriptions { get; set; } = false;

        // ── Connection endpoints (flow direction) ────────────────────────
        /// <summary>Upstream connection info. Null = open end.</summary>
        public PcfConnection? ConnectedFrom { get; set; }

        /// <summary>Downstream connection info. Null = open end.</summary>
        public PcfConnection? ConnectedTo { get; set; }
    }

    /// <summary>
    /// Represents a connection at the start or end of a pipe run —
    /// either to equipment (pump, vessel) or to another pipeline.
    /// </summary>
    public class PcfConnection
    {
        public enum ConnectionType { Equipment, Pipeline }

        /// <summary>Equipment or Pipeline connection.</summary>
        public ConnectionType Type { get; set; }

        /// <summary>
        /// For Equipment: tag + nozzle (e.g. "P-999-N-1 300").
        /// For Pipeline: line number (e.g. "888").
        /// </summary>
        public string Reference { get; set; } = string.Empty;

        /// <summary>Display text for the UI (e.g. "P-999 (Equipment)" or "Line 888 (Pipeline)").</summary>
        public string DisplayText { get; set; } = string.Empty;

        /// <summary>Position of the connected element (used to match to the nearest open end).</summary>
        public double[] Position { get; set; } = Array.Empty<double>();
    }
}
