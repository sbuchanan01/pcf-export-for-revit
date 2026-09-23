using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PcfExport.Revit
{
    /// <summary>
    /// Scoped view override that dims every relevant-category element in
    /// the active view that ISN'T in the current export selection, so the
    /// user can visually confirm what's being exported. Elements added to
    /// the export set later (via Refresh Selection or Connected From/To)
    /// have their override cleared incrementally. Disposing restores the
    /// original graphics.
    ///
    /// Used by ExportCommand for the duration of the ExportDialog + any
    /// nested TagValidationDialog. Safe to dispose more than once.
    /// </summary>
    public sealed class ExportVisibilityScope : IDisposable
    {
        private const int TransparencyPercent = 85;

        // Piping / duct / equipment surface. Anything outside these
        // categories is left alone — the tool is scoped to MEP work.
        private static readonly BuiltInCategory[] RelevantCategories = new[]
        {
            BuiltInCategory.OST_FabricationPipework,
            BuiltInCategory.OST_FabricationDuctwork,
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_MechanicalEquipment,
        };

        private readonly Document _doc;
        private readonly View _view;
        private readonly HashSet<ElementId> _relevantIds = new();
        private readonly HashSet<ElementId> _selection;
        private readonly HashSet<ElementId> _dimmed = new();
        private bool _disposed;

        /// <summary>
        /// Applies the initial dim pass. Silently no-ops if the active
        /// view doesn't accept element overrides (e.g. a schedule).
        /// </summary>
        public ExportVisibilityScope(Document doc, View view, IEnumerable<ElementId> selection)
        {
            _doc  = doc  ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _selection = new HashSet<ElementId>(selection);

            if (!IsOverridableView(_view)) return;

            // Enumerate every relevant-category element visible in the view.
            var filters = RelevantCategories
                .Select(c => (ElementFilter)new ElementCategoryFilter(c))
                .ToList();
            var anyCat = new LogicalOrFilter(filters);

            try
            {
                foreach (var e in new FilteredElementCollector(_doc, _view.Id)
                    .WhereElementIsNotElementType()
                    .WherePasses(anyCat))
                {
                    _relevantIds.Add(e.Id);
                }
            }
            catch { /* view may reject the collector — leave the set empty */ }

            ApplyInitialDim();
        }

        private void ApplyInitialDim()
        {
            var toDim = _relevantIds.Where(id => !_selection.Contains(id)).ToList();
            if (toDim.Count == 0) return;

            var dimOgs = BuildDimOverrides();
            try
            {
                using var tx = new Transaction(_doc, "Dim non-exported elements");
                tx.Start();
                foreach (var id in toDim)
                {
                    try { _view.SetElementOverrides(id, dimOgs); _dimmed.Add(id); }
                    catch { /* skip element the view refuses */ }
                }
                tx.Commit();
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Reconcile with the new export selection: clear the dim on
        /// anything newly added, re-dim anything newly removed (only if
        /// it's a relevant-category element in the view).
        /// </summary>
        public void UpdateSelection(IEnumerable<ElementId> newSelection)
        {
            if (_disposed) return;

            var next = new HashSet<ElementId>(newSelection);
            var newlyAdded   = next.Except(_selection).ToList();
            var newlyRemoved = _selection.Except(next)
                                         .Where(_relevantIds.Contains)
                                         .ToList();

            _selection.Clear();
            _selection.UnionWith(next);

            if (newlyAdded.Count == 0 && newlyRemoved.Count == 0) return;

            var clear  = new OverrideGraphicSettings();
            var dimOgs = BuildDimOverrides();

            try
            {
                using var tx = new Transaction(_doc, "Update export visibility");
                tx.Start();
                foreach (var id in newlyAdded)
                {
                    if (!_dimmed.Contains(id)) continue;
                    try { _view.SetElementOverrides(id, clear); _dimmed.Remove(id); }
                    catch { }
                }
                foreach (var id in newlyRemoved)
                {
                    if (_dimmed.Contains(id)) continue;
                    try { _view.SetElementOverrides(id, dimOgs); _dimmed.Add(id); }
                    catch { }
                }
                tx.Commit();
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Add a single element to the export set and clear its dim.
        /// Used by the Connected From / Connected To picker.
        /// </summary>
        public void AddOne(ElementId id)
        {
            if (_disposed || id == null || id == ElementId.InvalidElementId) return;
            if (!_selection.Add(id)) return;
            if (!_dimmed.Contains(id)) return;

            try
            {
                using var tx = new Transaction(_doc, "Un-dim added element");
                tx.Start();
                try { _view.SetElementOverrides(id, new OverrideGraphicSettings()); _dimmed.Remove(id); }
                catch { }
                tx.Commit();
            }
            catch { /* best effort */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_dimmed.Count == 0) return;

            var clear = new OverrideGraphicSettings();
            try
            {
                using var tx = new Transaction(_doc, "Restore export visibility");
                tx.Start();
                foreach (var id in _dimmed)
                {
                    try { _view.SetElementOverrides(id, clear); } catch { }
                }
                tx.Commit();
            }
            catch { /* best effort */ }
            _dimmed.Clear();
        }

        private static OverrideGraphicSettings BuildDimOverrides() =>
            new OverrideGraphicSettings().SetSurfaceTransparency(TransparencyPercent);

        private static bool IsOverridableView(View v)
        {
            if (v == null || v.IsTemplate) return false;
            switch (v.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.Elevation:
                case ViewType.Section:
                case ViewType.Detail:
                case ViewType.ThreeD:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Walkthrough:
                    return true;
                default:
                    return false;
            }
        }
    }
}
