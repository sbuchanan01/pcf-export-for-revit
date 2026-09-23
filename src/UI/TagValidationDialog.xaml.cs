using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PcfExport.Models;

namespace PcfExport.UI
{
    public partial class TagValidationDialog : Window
    {
        private readonly UIDocument _uiDoc;

        /// <summary>Invoked after Close() when the user confirms all tags.</summary>
        public Action? OnConfirmed { get; set; }

        /// <summary>Invoked after Close() when the user cancels.</summary>
        public Action? OnCancelled { get; set; }

        public TagValidationViewModel ViewModel { get; }

        public TagValidationDialog(
            List<ValveTagItem> missingItems,
            UIDocument uiDoc,
            HashSet<string> existingTags,
            bool isEquipmentMode = false)
        {
            InitializeComponent();
            _uiDoc      = uiDoc;
            ViewModel   = new TagValidationViewModel(missingItems, existingTags, isEquipmentMode);
            DataContext  = ViewModel;

            if (isEquipmentMode)
            {
                Title = "Equipment Tag Assignment";
                // Change Export button to OK
                if (FindName("ExportButton") is System.Windows.Controls.Button btn)
                    btn.Content = "OK";
            }

            HighlightCurrent();
            Loaded += (_, _) => TagBox.Focus();
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.GoPrev();
            HighlightCurrent();
            TagBox.Focus();
            TagBox.SelectAll();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.GoNext();
            HighlightCurrent();
            TagBox.Focus();
            TagBox.SelectAll();
        }

        // "Next" button next to the Tag input — auto-fills the next open tag
        // value using the same prefix + digit width as the most recently
        // entered tag on this run.
        private void NextNumber_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SuggestNext();
            TagBox.Focus();
            TagBox.SelectAll();
        }

        // Allow Enter to advance to next item (blocked when current item has an error)
        private void TagBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (ViewModel.CanGoNext)
                {
                    ViewModel.GoNext();
                    HighlightCurrent();
                }
                TagBox.Focus();
                TagBox.SelectAll();
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            // Hard validation guard — never rely solely on the button being disabled
            if (!ViewModel.CanExport)
            {
                System.Windows.MessageBox.Show(
                    "One or more tags are missing or duplicated. " +
                    "Each valve must have a unique Tag before exporting.",
                    "Tag Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Close();
            OnConfirmed?.Invoke();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
            OnCancelled?.Invoke();
        }

        private void ToggleUsedTags_Click(object sender, RoutedEventArgs e)
            => ViewModel.ToggleUsedTags();

        /// <summary>Selects the current valve element in the Revit model view.</summary>
        private void HighlightCurrent()
        {
            try
            {
                _uiDoc.Selection.SetElementIds(
                    new List<ElementId> { ViewModel.CurrentItem.ElementId });
            }
            catch { /* Selection may fail if element is not visible in active view */ }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents a single valve that is missing its Tag value.
    /// </summary>
    public class ValveTagItem
    {
        public ElementId     ElementId   { get; init; } = ElementId.InvalidElementId;
        public string        UniqueId    { get; init; } = string.Empty;
        public string        Description { get; init; } = string.Empty;
        public PcfComponent  Component   { get; init; } = null!;

        /// <summary>Tag value — edited by the user in the dialog.</summary>
        public string Tag { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────────

    public class TagValidationViewModel : INotifyPropertyChanged
    {
        private readonly List<ValveTagItem> _items;

        /// <summary>
        /// Tags that are already in use by valves that had values before this dialog opened.
        /// Checked so the user cannot reuse an existing tag.
        /// </summary>
        private readonly HashSet<string> _existingTags;
        private readonly bool _isEquipmentMode;

        private int _currentIndex;

        public TagValidationViewModel(List<ValveTagItem> items, HashSet<string> existingTags, bool isEquipmentMode = false)
        {
            _items        = items;
            _existingTags = existingTags;
            _isEquipmentMode = isEquipmentMode;
        }

        // ── Current item ─────────────────────────────────────────────────────
        public ValveTagItem CurrentItem => _items[_currentIndex];

        public string CurrentDescription => CurrentItem.Description;
        public string CurrentUniqueId    => CurrentItem.UniqueId;
        public bool   CurrentIsMissing   => string.IsNullOrWhiteSpace(CurrentItem.Tag);

        public string CurrentTag
        {
            get => CurrentItem.Tag;
            set
            {
                CurrentItem.Tag = value?.ToUpperInvariant() ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentIsMissing));
                OnPropertyChanged(nameof(CurrentIsDuplicate));
                OnPropertyChanged(nameof(CurrentHasError));
                OnPropertyChanged(nameof(CurrentErrorText));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(CanSuggestNext));
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(ExistingTagsSorted));
                OnPropertyChanged(nameof(UsedTagsHeaderText));
            }
        }

        // ── Validation ───────────────────────────────────────────────────────

        /// <summary>
        /// True when the current tag duplicates another item in this dialog
        /// OR an already-existing valve tag.
        /// </summary>
        public bool CurrentIsDuplicate
        {
            get
            {
                string tag = CurrentItem.Tag;
                if (string.IsNullOrWhiteSpace(tag)) return false;

                // Duplicate within the missing-tag list
                bool inList = _items.Any(i => !ReferenceEquals(i, CurrentItem) &&
                                              string.Equals(i.Tag, tag, StringComparison.OrdinalIgnoreCase));

                // Duplicate against valves that already had tags before dialog opened
                bool inExisting = _existingTags.Contains(tag);

                return inList || inExisting;
            }
        }

        /// <summary>True when ANY item has a duplicate tag (used to gate Export).</summary>
        private bool AnyDuplicate()
        {
            var allTags = new HashSet<string>(_existingTags, StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items)
            {
                if (string.IsNullOrWhiteSpace(item.Tag)) continue;
                if (!allTags.Add(item.Tag)) return true; // Add returns false on duplicate
            }
            return false;
        }

        /// <summary>Error message shown beneath the tag input box.</summary>
        public string CurrentErrorText
        {
            get
            {
                if (CurrentIsDuplicate)
                    return $"Tag \"{CurrentItem.Tag}\" is already used — each valve must have a unique Tag.";
                if (CurrentIsMissing)
                    return "Tag is required.";
                return string.Empty;
            }
        }

        public bool CurrentHasError => CurrentIsMissing || CurrentIsDuplicate;

        // ── Navigation ───────────────────────────────────────────────────────
        public string ProgressText => $"{_currentIndex + 1} of {_items.Count}";
        public bool CanGoPrev => _currentIndex > 0;

        /// <summary>Next is blocked when the current item has an error (missing or duplicate).</summary>
        public bool CanGoNext => _currentIndex < _items.Count - 1 && !CurrentHasError;

        public void GoPrev()
        {
            if (!CanGoPrev) return;
            _currentIndex--;
            NotifyNavigation();
        }

        public void GoNext()
        {
            if (!CanGoNext) return;
            _currentIndex++;
            NotifyNavigation();
        }

        // ── Auto-suggest next open tag ────────────────────────────────────────

        /// <summary>
        /// Returns the most recently entered tag — walks back from the current
        /// item to find the nearest filled neighbour. Used by the "Next"
        /// button to copy that tag's prefix and digit width.
        /// </summary>
        private string? FindPriorTag()
        {
            for (int i = _currentIndex - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(_items[i].Tag))
                    return _items[i].Tag;
            }
            return null;
        }

        /// <summary>
        /// True when there's a prior tag whose trailing digits we can
        /// increment. Disables the button on the first item or when the
        /// prior tag has no numeric suffix.
        /// </summary>
        public bool CanSuggestNext
        {
            get
            {
                string? prior = FindPriorTag();
                if (prior == null) return false;
                return prior.Length > 0 && char.IsDigit(prior[prior.Length - 1]);
            }
        }

        /// <summary>
        /// Fills the current item's Tag with the next open number that
        /// matches the prior tag's pattern: same prefix, same zero-padded
        /// digit width. Skips any number already used by another item in
        /// this dialog or by an existing valve elsewhere in the model.
        /// </summary>
        public void SuggestNext()
        {
            string? prior = FindPriorTag();
            if (prior == null) return;

            // Parse the trailing digit run.
            int end   = prior.Length;
            int start = end;
            while (start > 0 && char.IsDigit(prior[start - 1])) start--;
            if (start == end) return; // no trailing digits to increment

            string prefix = prior.Substring(0, start);
            string digits = prior.Substring(start, end - start);
            int    width  = digits.Length;
            if (!int.TryParse(digits, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out int seed)) return;

            // Build the "already used" set: project-wide existing tags + every
            // other item's currently-entered tag.
            var used = new HashSet<string>(_existingTags, StringComparer.OrdinalIgnoreCase);
            foreach (var i in _items)
            {
                if (ReferenceEquals(i, CurrentItem)) continue;
                if (!string.IsNullOrWhiteSpace(i.Tag)) used.Add(i.Tag);
            }

            // Walk forward until we find an open slot. Cap the search so a
            // pathological setup can't lock up the UI.
            for (int n = seed + 1; n < seed + 100_000; n++)
            {
                string candidate = prefix + n.ToString(
                    "D" + width, System.Globalization.CultureInfo.InvariantCulture);
                if (!used.Contains(candidate))
                {
                    CurrentTag = candidate;
                    return;
                }
            }
        }

        // ── State ────────────────────────────────────────────────────────────
        public string HeaderText => _isEquipmentMode
            ? "The selected equipment does not have a Tag value."
            : $"{_items.Count} valve{(_items.Count == 1 ? "" : "s")} with a missing Tag value.";

        public string SubHeaderText => _isEquipmentMode
            ? "Enter a Tag value, then click OK."
            : "Select each valve, enter its Tag, then click Export.";

        public bool ShowFooterStatus => !_isEquipmentMode;

        /// <summary>Export is only allowed when every item has a tag and no duplicates exist.</summary>
        public bool CanExport =>
            _items.TrueForAll(i => !string.IsNullOrWhiteSpace(i.Tag)) && !AnyDuplicate();

        // ── Used Tags collapsible section ────────────────────────────────────
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

        /// <summary>Sorted union of project-wide existing tags and already-filled item tags.</summary>
        public IReadOnlyList<string> ExistingTagsSorted
        {
            get
            {
                var all = new HashSet<string>(_existingTags, StringComparer.OrdinalIgnoreCase);
                foreach (var item in _items)
                    if (!string.IsNullOrWhiteSpace(item.Tag))
                        all.Add(item.Tag);
                return all.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public string UsedTagsHeaderText => $"Used Tag Values ({ExistingTagsSorted.Count})";

        public void ToggleUsedTags()
        {
            IsUsedTagsExpanded = !_isUsedTagsExpanded;
        }

        // ── INotifyPropertyChanged ───────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void NotifyNavigation()
        {
            OnPropertyChanged(nameof(CurrentItem));
            OnPropertyChanged(nameof(CurrentDescription));
            OnPropertyChanged(nameof(CurrentUniqueId));
            OnPropertyChanged(nameof(CurrentTag));
            OnPropertyChanged(nameof(CurrentIsMissing));
            OnPropertyChanged(nameof(CurrentIsDuplicate));
            OnPropertyChanged(nameof(CurrentErrorText));
            OnPropertyChanged(nameof(CurrentHasError));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanSuggestNext));
            OnPropertyChanged(nameof(CanExport));
        }
    }
}
