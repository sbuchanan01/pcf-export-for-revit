using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PcfExport.Revit;
using PcfExport.UI;
using System.Collections.Generic;
using System.Linq;

namespace PcfExport
{
    [Transaction(TransactionMode.Manual)]
    public class AssignTagCommand : IExternalCommand
    {
        private static AssignTagDialog? _instance;

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

            // Collect all existing Tag values project-wide
            var existingTags = CollectExistingTags(doc);

            var dialog = new AssignTagDialog(uiDoc, existingTags);

            // Pre-load if a single valve is already selected
            var selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 1)
            {
                var elem = doc.GetElement(selectedIds.First());
                if (elem is FabricationPart fp && PartTypeClassifier.GetPcfType(fp) == "VALVE")
                    dialog.ViewModel.LoadValve(ReadValveInfo(fp, doc));
            }

            _instance = dialog;
            dialog.Closed += (_, _) => _instance = null;
            dialog.Show();
            return Result.Succeeded;
        }

        // ── Helpers used by AssignTagDialog via internal access ───────────────

        internal static HashSet<string> CollectExistingTags(Document doc)
        {
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var elem in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .WhereElementIsNotElementType()
                .Cast<Element>())
            {
                string? t = ExportCommand.FindParameter(elem, "Tag")?.AsString();
                if (!string.IsNullOrWhiteSpace(t)) tags.Add(t);
            }
            return tags;
        }

        internal static ValveInfo ReadValveInfo(FabricationPart part, Document doc)
        {
            string desc = ExportCommand.FindParameter(part, "Item Description")?.AsString()
                       ?? ExportCommand.FindParameter(part, "Description")?.AsString()
                       ?? part.Name;
            string existingTag = ExportCommand.FindParameter(part, "Tag")?.AsString() ?? string.Empty;
            return new ValveInfo(part.Id, desc.Trim(), part.UniqueId, existingTag);
        }

        /// <summary>
        /// Returns the next untagged valve in the active view after <paramref name="afterId"/>,
        /// wrapping around. Returns null if no untagged valves remain.
        /// <paramref name="excludeId"/> is skipped even if untagged (used by Skip to avoid
        /// returning the same valve immediately).
        /// </summary>
        internal static FabricationPart? FindNextUntaggedValve(
            Document doc, ElementId afterId, ElementId? excludeId = null)
        {
            var all = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .WhereElementIsNotElementType()
                .Cast<FabricationPart>()
                .Where(p => PartTypeClassifier.GetPcfType(p) == "VALVE")
                .Where(p => string.IsNullOrWhiteSpace(
                    ExportCommand.FindParameter(p, "Tag")?.AsString()))
                .Where(p => excludeId == null || p.Id != excludeId)
                .OrderBy(p => p.Id.Value)
                .ToList();

            if (all.Count == 0) return null;

            // First untagged valve with Id > afterId; wrap to first if none
            return all.FirstOrDefault(p => p.Id.Value > afterId.Value) ?? all[0];
        }

        /// <summary>
        /// Generates sequential tag strings starting from <paramref name="startTag"/>.
        /// Preserves zero-padding width of the numeric suffix (e.g. "HA-101" → "HA-102", "HA-103").
        /// </summary>
        internal static IEnumerable<string> GenerateTags(string startTag, int count)
        {
            // Split into non-numeric prefix and trailing numeric portion
            int i = startTag.Length - 1;
            while (i >= 0 && char.IsDigit(startTag[i])) i--;

            string prefix = startTag[..(i + 1)];
            string numStr = startTag[(i + 1)..];

            if (numStr.Length == 0 || !int.TryParse(numStr, out int startNum))
            {
                // No trailing number — append 1, 2, 3 …
                for (int k = 1; k <= count; k++)
                    yield return prefix + k;
                yield break;
            }

            int padWidth = numStr.Length;
            for (int k = 0; k < count; k++)
                yield return prefix + (startNum + k).ToString().PadLeft(padWidth, '0');
        }
    }

    // ── Selection filter — only allows FabricationPart valves to be picked ──────

    internal sealed class ValveSelectionFilter : ISelectionFilter
    {
        private readonly Document _doc;
        public ValveSelectionFilter(Document doc) => _doc = doc;

        public bool AllowElement(Element elem) =>
            elem is FabricationPart fp && PartTypeClassifier.GetPcfType(fp) == "VALVE";

        public bool AllowReference(Reference reference, XYZ position)
        {
            // PickObjects calls AllowReference for each click; must return true
            // for element references so the pick is accepted.
            var elem = _doc.GetElement(reference);
            return elem is FabricationPart fp && PartTypeClassifier.GetPcfType(fp) == "VALVE";
        }
    }

    // ── Data record passed between Revit thread and ViewModel ────────────────────

    public record ValveInfo(ElementId Id, string Description, string UniqueId, string ExistingTag);
}
