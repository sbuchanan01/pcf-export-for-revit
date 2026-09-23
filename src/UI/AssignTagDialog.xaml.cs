using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PcfExport.Revit;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace PcfExport.UI
{
    public partial class AssignTagDialog : Window
    {
        private readonly UIDocument _uiDoc;

        public AssignTagViewModel ViewModel { get; }

        public AssignTagDialog(UIDocument uiDoc, HashSet<string> existingTags)
        {
            InitializeComponent();
            _uiDoc     = uiDoc;
            ViewModel  = new AssignTagViewModel(existingTags);
            DataContext = ViewModel;
        }

        // ── Highlight non-tagged valves ───────────────────────────────────────

        private void HighlightNonTagged_Click(object sender, RoutedEventArgs e)
        {
            PcfExportApp.TagHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc   = uiDoc.Document;

                var ids = new FilteredElementCollector(doc, doc.ActiveView.Id)
                    .OfCategory(BuiltInCategory.OST_FabricationPipework)
                    .WhereElementIsNotElementType()
                    .Cast<FabricationPart>()
                    .Where(p => PartTypeClassifier.GetPcfType(p) == "VALVE")
                    .Where(p => string.IsNullOrWhiteSpace(
                        ExportCommand.FindParameter(p, "Tag")?.AsString()))
                    .Select(p => p.Id)
                    .ToList();

                uiDoc.Selection.SetElementIds(ids);

                string msg = ids.Count == 0
                    ? "No untagged valves found in the active view."
                    : $"{ids.Count} untagged valve(s) highlighted in the model.";

                Dispatcher.Invoke(() =>
                {
                    ViewModel.StatusText = msg;
                    if (ids.Count == 0)
                        MessageBox.Show(msg, "Assign Tag");
                });
            });
            PcfExportApp.TagEvent!.Raise();
        }

        // ── Manual mode: Pick Valve ───────────────────────────────────────────

        private void PickValve_Click(object sender, RoutedEventArgs e)
        {
            PcfExportApp.TagHandler!.SetAction(uiApp =>
            {
                try
                {
                    var uiDoc = uiApp.ActiveUIDocument;
                    var doc   = uiDoc.Document;
                    var ref_  = uiDoc.Selection.PickObject(
                        ObjectType.Element,
                        new ValveSelectionFilter(doc),
                        "Pick a valve to tag");

                    if (doc.GetElement(ref_) is not FabricationPart part) return;

                    uiDoc.Selection.SetElementIds(new List<ElementId> { part.Id });
                    var info = AssignTagCommand.ReadValveInfo(part, doc);
                    Dispatcher.Invoke(() =>
                    {
                        ViewModel.LoadValve(info);
                        ManualTagBox.Focus();
                        ManualTagBox.SelectAll();
                    });
                }
                catch (OperationCanceledException) { /* user pressed Escape */ }
            });
            PcfExportApp.TagEvent!.Raise();
        }

        // ── Manual mode: Accept ───────────────────────────────────────────────

        private void Accept_Click(object sender, RoutedEventArgs e) => CommitCurrentTag();

        private void ManualTagBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ViewModel.CanAccept)
                CommitCurrentTag();
        }

        private void CommitCurrentTag()
        {
            if (!ViewModel.CanAccept) return;

            var    elementId = ViewModel.CurrentElementId!;
            string tag       = ViewModel.CurrentTag;

            PcfExportApp.TagHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc   = uiDoc.Document;

                // Commit the tag to the "Tag" parameter
                using var tx = new Transaction(doc, "Assign Valve Tag");
                tx.Start();
                var elem  = doc.GetElement(elementId);
                var param = ExportCommand.FindParameter(elem, "Tag");
                if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                    param.Set(tag);
                tx.Commit();

                Dispatcher.Invoke(() => ViewModel.AddCommittedTag(tag));

                // Advance to the next untagged valve in the active view
                var next = AssignTagCommand.FindNextUntaggedValve(doc, elementId);
                if (next != null)
                {
                    uiDoc.Selection.SetElementIds(new List<ElementId> { next.Id });
                    var info = AssignTagCommand.ReadValveInfo(next, doc);
                    Dispatcher.Invoke(() =>
                    {
                        ViewModel.LoadValve(info);
                        ManualTagBox.Focus();
                        ManualTagBox.SelectAll();
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        ViewModel.ClearCurrentValve();
                        ViewModel.StatusText = "All valves in the active view are now tagged.";
                    });
                }
            });
            PcfExportApp.TagEvent!.Raise();
        }

        // ── Manual mode: Skip ─────────────────────────────────────────────────

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.HasCurrentValve) return;

            var elementId = ViewModel.CurrentElementId!;

            PcfExportApp.TagHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc   = uiDoc.Document;

                // Advance without committing; exclude current so Skip always moves forward
                var next = AssignTagCommand.FindNextUntaggedValve(doc, elementId,
                    excludeId: elementId);

                if (next != null)
                {
                    uiDoc.Selection.SetElementIds(new List<ElementId> { next.Id });
                    var info = AssignTagCommand.ReadValveInfo(next, doc);
                    Dispatcher.Invoke(() =>
                    {
                        ViewModel.LoadValve(info);
                        ManualTagBox.Focus();
                        ManualTagBox.SelectAll();
                    });
                }
                else
                {
                    // Only one untagged valve left (the one we're on) — stay on it
                    Dispatcher.Invoke(() =>
                        ViewModel.StatusText = "No other untagged valves in the active view.");
                }
            });
            PcfExportApp.TagEvent!.Raise();
        }

        // ── Pick in Order: Pick ───────────────────────────────────────────────

        private void PickInOrder_Click(object sender, RoutedEventArgs e)
        {
            PcfExportApp.TagHandler!.SetAction(uiApp =>
            {
                try
                {
                    var uiDoc = uiApp.ActiveUIDocument;
                    var doc   = uiDoc.Document;

                    var refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new ValveSelectionFilter(doc),
                        "Pick valves in the order to be tagged. Press Enter or Escape when done.");

                    var items = refs
                        .Select(r => doc.GetElement(r) as FabricationPart)
                        .Where(p => p != null)
                        .Cast<FabricationPart>()
                        .Select(p => (p.Id, AssignTagCommand.ReadValveInfo(p, doc).Description))
                        .ToList();

                    Dispatcher.Invoke(() => ViewModel.SetPickedItems(items));
                }
                catch (OperationCanceledException) { /* user pressed Escape with no selection */ }
            });
            PcfExportApp.TagEvent!.Raise();
        }

        // ── Pick in Order: Clear ──────────────────────────────────────────────

        private void ClearPick_Click(object sender, RoutedEventArgs e)
            => ViewModel.ClearPickedItems();

        // ── Pick in Order: Apply ──────────────────────────────────────────────

        private void ApplyPickOrder_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanApplyPickOrder) return;

            var items    = ViewModel.PickedItems.ToList();
            string start = ViewModel.StartingTag;

            PcfExportApp.TagHandler!.SetAction(uiApp =>
            {
                var doc  = uiApp.ActiveUIDocument.Document;
                var tags = AssignTagCommand.GenerateTags(start, items.Count).ToList();

                using var tx = new Transaction(doc, "Assign Sequential Valve Tags");
                tx.Start();
                for (int k = 0; k < items.Count; k++)
                {
                    var elem  = doc.GetElement(items[k].Id);
                    var param = ExportCommand.FindParameter(elem, "Tag");
                    if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                        param.Set(tags[k]);
                }
                tx.Commit();

                Dispatcher.Invoke(() =>
                {
                    foreach (string t in tags) ViewModel.AddCommittedTag(t);
                    ViewModel.ClearPickedItems();
                    ViewModel.StatusText = $"Tagged {items.Count} valve(s): {string.Join(", ", tags)}.";
                });
            });
            PcfExportApp.TagEvent!.Raise();
        }

        // ── Used Tags toggle ──────────────────────────────────────────────────

        private void ToggleUsedTags_Click(object sender, RoutedEventArgs e)
            => ViewModel.ToggleUsedTags();

        // ── Close ─────────────────────────────────────────────────────────────

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class AssignTagViewModel : INotifyPropertyChanged
    {
        // All Tag values known to exist (project-wide at load time + committed this session)
        private readonly HashSet<string> _existingTags;

        public AssignTagViewModel(HashSet<string> existingTags)
        {
            _existingTags = new HashSet<string>(existingTags, StringComparer.OrdinalIgnoreCase);
        }

        // ── Mode ──────────────────────────────────────────────────────────────
        private bool _isManualMode = true;

        public bool IsManualMode
        {
            get => _isManualMode;
            set
            {
                if (SetField(ref _isManualMode, value))
                    OnPropertyChanged(nameof(IsPickOrderMode));
            }
        }
        public bool IsPickOrderMode
        {
            get => !_isManualMode;
            set { IsManualMode = !value; }
        }

        // ── Manual mode: current valve ────────────────────────────────────────
        public ElementId? CurrentElementId { get; private set; }

        private string _currentDescription = string.Empty;
        private string _currentUniqueId    = string.Empty;
        private string _currentTag         = string.Empty;

        public string CurrentDescription => _currentDescription;
        public string CurrentUniqueId    => _currentUniqueId;
        public bool   HasCurrentValve    => CurrentElementId != null;
        public bool   HasNoValve         => !HasCurrentValve;

        public string CurrentTag
        {
            get => _currentTag;
            set
            {
                SetField(ref _currentTag, value?.ToUpperInvariant() ?? string.Empty);
                OnPropertyChanged(nameof(CurrentIsDuplicate));
                OnPropertyChanged(nameof(CurrentHasError));
                OnPropertyChanged(nameof(CurrentErrorText));
                OnPropertyChanged(nameof(CanAccept));
            }
        }

        public bool CurrentIsDuplicate =>
            !string.IsNullOrWhiteSpace(_currentTag) &&
            _existingTags.Contains(_currentTag);

        public bool CurrentHasError =>
            HasCurrentValve &&
            (!string.IsNullOrWhiteSpace(_currentTag) && CurrentIsDuplicate);

        public string CurrentErrorText => CurrentIsDuplicate
            ? $"Tag \"{_currentTag}\" is already in use — each valve must have a unique Tag."
            : string.Empty;

        public bool CanAccept =>
            HasCurrentValve &&
            !string.IsNullOrWhiteSpace(_currentTag) &&
            !CurrentIsDuplicate;

        public void LoadValve(ValveInfo info)
        {
            CurrentElementId     = info.Id;
            _currentDescription  = info.Description;
            _currentUniqueId     = info.UniqueId;
            _currentTag          = info.ExistingTag.ToUpperInvariant();

            OnPropertyChanged(nameof(CurrentElementId));
            OnPropertyChanged(nameof(CurrentDescription));
            OnPropertyChanged(nameof(CurrentUniqueId));
            OnPropertyChanged(nameof(CurrentTag));
            OnPropertyChanged(nameof(HasCurrentValve));
            OnPropertyChanged(nameof(HasNoValve));
            OnPropertyChanged(nameof(CurrentIsDuplicate));
            OnPropertyChanged(nameof(CurrentHasError));
            OnPropertyChanged(nameof(CurrentErrorText));
            OnPropertyChanged(nameof(CanAccept));
            StatusText = $"Loaded: {info.Description}";
        }

        public void ClearCurrentValve()
        {
            CurrentElementId    = null;
            _currentDescription = string.Empty;
            _currentUniqueId    = string.Empty;
            _currentTag         = string.Empty;

            OnPropertyChanged(nameof(CurrentElementId));
            OnPropertyChanged(nameof(CurrentDescription));
            OnPropertyChanged(nameof(CurrentUniqueId));
            OnPropertyChanged(nameof(CurrentTag));
            OnPropertyChanged(nameof(HasCurrentValve));
            OnPropertyChanged(nameof(HasNoValve));
            OnPropertyChanged(nameof(CurrentHasError));
            OnPropertyChanged(nameof(CurrentErrorText));
            OnPropertyChanged(nameof(CanAccept));
        }

        // ── Pick in Order ─────────────────────────────────────────────────────
        private List<(ElementId Id, string Description)> _pickedItems = new();

        public IReadOnlyList<(ElementId Id, string Description)> PickedItems => _pickedItems;
        public bool HasPickedItems => _pickedItems.Count > 0;

        private string _startingTag = string.Empty;
        public string StartingTag
        {
            get => _startingTag;
            set
            {
                SetField(ref _startingTag, value?.ToUpperInvariant() ?? string.Empty);
                NotifyPickOrder();
            }
        }

        public string PickPreviewText
        {
            get
            {
                if (_pickedItems.Count == 0) return string.Empty;
                if (string.IsNullOrWhiteSpace(_startingTag))
                    return $"{_pickedItems.Count} valve(s) picked — enter a Starting Tag.";

                var tags = AssignTagCommand.GenerateTags(_startingTag, _pickedItems.Count).ToList();
                string tagList = tags.Count <= 5
                    ? string.Join(", ", tags)
                    : $"{tags[0]}, {tags[1]}, {tags[2]} … {tags[^1]}";

                return $"{_pickedItems.Count} valve(s) picked — will be tagged: {tagList}";
            }
        }

        public bool PickHasConflict
        {
            get
            {
                if (_pickedItems.Count == 0 || string.IsNullOrWhiteSpace(_startingTag))
                    return false;

                var tags = AssignTagCommand.GenerateTags(_startingTag, _pickedItems.Count);
                return tags.Any(t => _existingTags.Contains(t));
            }
        }

        public string PickConflictText
        {
            get
            {
                if (!PickHasConflict) return string.Empty;
                var conflicts = AssignTagCommand.GenerateTags(_startingTag, _pickedItems.Count)
                    .Where(t => _existingTags.Contains(t))
                    .ToList();
                return $"Conflict: {string.Join(", ", conflicts)} already in use. Choose a different starting tag.";
            }
        }

        public bool CanApplyPickOrder =>
            HasPickedItems &&
            !string.IsNullOrWhiteSpace(_startingTag) &&
            !PickHasConflict;

        public void SetPickedItems(IList<(ElementId Id, string Description)> items)
        {
            _pickedItems = new List<(ElementId, string)>(items);
            NotifyPickOrder();
            StatusText = $"{_pickedItems.Count} valve(s) picked.";
        }

        public void ClearPickedItems()
        {
            _pickedItems.Clear();
            NotifyPickOrder();
            StatusText = string.Empty;
        }

        private void NotifyPickOrder()
        {
            OnPropertyChanged(nameof(PickedItems));
            OnPropertyChanged(nameof(HasPickedItems));
            OnPropertyChanged(nameof(PickPreviewText));
            OnPropertyChanged(nameof(PickHasConflict));
            OnPropertyChanged(nameof(PickConflictText));
            OnPropertyChanged(nameof(CanApplyPickOrder));
        }

        // ── Committed tags (this session) ─────────────────────────────────────
        public void AddCommittedTag(string tag)
        {
            _existingTags.Add(tag);
            OnPropertyChanged(nameof(ExistingTagsSorted));
            OnPropertyChanged(nameof(UsedTagsHeaderText));
        }

        // ── Status bar ────────────────────────────────────────────────────────
        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        // ── Used Tags (collapsible) ───────────────────────────────────────────
        private bool _isUsedTagsExpanded = false;

        public bool IsUsedTagsExpanded
        {
            get => _isUsedTagsExpanded;
            private set
            {
                _isUsedTagsExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UsedTagsChevron));
                OnPropertyChanged(nameof(UsedTagsHeaderText));
            }
        }

        public string UsedTagsChevron => _isUsedTagsExpanded ? "▲" : "▼";

        public IReadOnlyList<string> ExistingTagsSorted =>
            _existingTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

        public string UsedTagsHeaderText => $"Used Tag Values ({_existingTags.Count})";

        public void ToggleUsedTags() => IsUsedTagsExpanded = !_isUsedTagsExpanded;

        // ── INotifyPropertyChanged ────────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
