using System;
using System.Reflection;
using Autodesk.Revit.UI;
using PcfExport.Revit;

namespace PcfExport
{
    /// <summary>
    /// Revit IExternalApplication entry point. Creates a "PCF Export" ribbon
    /// tab with an "Export" panel carrying two flat push buttons — one for
    /// exporting selected FabricationParts to PCF, one for interactively
    /// assigning Tag values to valves. Registers the shared ExternalEvent /
    /// handler pairs the modeless dialogs use to call back into the Revit
    /// API thread.
    /// </summary>
    public class PcfExportApp : IExternalApplication
    {
        // Used by ExportDialog (and its child TagValidationDialog) to post
        // export work onto the Revit API thread from the WPF UI thread.
        public static RevitEventHandler? ExportHandler { get; private set; }
        public static ExternalEvent?     ExportEvent   { get; private set; }

        // Used by AssignTagDialog for its modeless tag-picking workflow.
        public static RevitEventHandler? TagHandler { get; private set; }
        public static ExternalEvent?     TagEvent   { get; private set; }

        private const string RibbonTabName   = "PCF Export";
        private const string RibbonPanelName = "Export";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                ExportHandler = new RevitEventHandler();
                ExportEvent   = ExternalEvent.Create(ExportHandler);
                TagHandler    = new RevitEventHandler();
                TagEvent      = ExternalEvent.Create(TagHandler);

                try { app.CreateRibbonTab(RibbonTabName); }
                catch (Exception) { /* tab already exists */ }

                var panel = app.CreateRibbonPanel(RibbonTabName, RibbonPanelName);

                string asm = Assembly.GetExecutingAssembly().Location;

                var exportButton = new PushButtonData(
                    name: "ExportPCF",
                    text: "ITMs to PCF",
                    assemblyName: asm,
                    className: "PcfExport.ExportCommand")
                {
                    ToolTip = "Export selected Fabrication Parts (ITMs) to PCF format.",
                    LongDescription = "Opens a dialog to configure export options, then writes a PCF file " +
                                      "from the selected fabrication parts. Supports pipes, fittings, and supports.",
                    LargeImage = RibbonIconFactory.Upload(32),
                    Image      = RibbonIconFactory.Upload(16),
                };

                var tagButton = new PushButtonData(
                    name: "AssignTag",
                    text: "Assign\nTag",
                    assemblyName: asm,
                    className: "PcfExport.AssignTagCommand")
                {
                    ToolTip = "Interactively assign Tag values to FabricationPart valves.",
                    LongDescription = "Opens a modeless dialog to tag valves one at a time, " +
                                      "pick them in sequence for auto-incrementing tags, " +
                                      "or highlight all untagged valves in the active view.",
                    LargeImage = RibbonIconFactory.Tag(32),
                    Image      = RibbonIconFactory.Tag(16),
                };

                panel.AddItem(exportButton);
                panel.AddItem(tagButton);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("PCF Export — startup error", ex.ToString());
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
