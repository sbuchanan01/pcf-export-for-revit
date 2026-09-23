using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using PcfExport.Models;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace PcfExport.UI
{
    public partial class ExportDialog : Window
    {
        private readonly UIDocument? _uiDoc;
        private readonly string      _originalLineNumber;
        private readonly PcfExport.Revit.ExportVisibilityScope? _visScope;

        public ExportViewModel ViewModel { get; }

        public ExportDialog(
            UIDocument uiDoc,
            IReadOnlyList<LineNumberToken> lineNumberTokens,
            PcfExport.Revit.ExportVisibilityScope? visScope = null)
        {
            InitializeComponent();
            _uiDoc              = uiDoc;
            _originalLineNumber = string.Join(", ", lineNumberTokens.Select(t => t.Display));
            _visScope           = visScope;
            ViewModel           = new ExportViewModel(lineNumberTokens);
            DataContext         = ViewModel;
        }

        // ── Browse folder ─────────────────────────────────────────────────────

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Output Folder for PCF Files",
            };
            if (dlg.ShowDialog() == true)
                ViewModel.OutputFolderPath = dlg.FolderName;
        }

        // ── Export (fires ExternalEvent so Revit API runs on main thread) ─────

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanExport)
            {
                MessageBox.Show(
                    "Please select an output folder and fill in the Pipeline Reference.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var    tokens      = ViewModel.LineNumberTokens.ToList();
            var    options     = ViewModel.ToOptions();
            string origLineNum = _originalLineNumber;
            var    dlgRef      = this;

            PcfExportApp.ExportHandler!.SetAction(uiApp =>
            {
                ExportCommand.RunExport(uiApp, tokens, options, origLineNum, dlgRef);
                // Dialog is closed inside RunExport/FinishExport on success,
                // or left open so the user can correct inputs and retry.
            });
            PcfExportApp.ExportEvent!.Raise();
        }

        // ── Refresh (re-reads current Revit selection on main thread) ─────────

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            PcfExportApp.ExportHandler!.SetAction(uiApp =>
            {
                var uiDoc     = uiApp.ActiveUIDocument;
                var doc       = uiDoc.Document;
                var newTokens = ExportCommand.ReadLineNumberTokens(doc, uiDoc);

                // Reconcile the visibility dim with the freshly-read selection.
                _visScope?.UpdateSelection(uiDoc.Selection.GetElementIds());

                Dispatcher.Invoke(() =>
                {
                    ViewModel.SetTokens(newTokens);

                    if (newTokens.Count == 1)
                    {
                        ViewModel.PipelineReference = newTokens[0].Display == "<BLANK>"
                            ? string.Empty
                            : newTokens[0].Display;
                    }
                });
            });
            PcfExportApp.ExportEvent!.Raise();
        }

        // ── Pick Elements — hides dialog, prompts user to select, returns ────

        private void PickElements_Click(object sender, RoutedEventArgs e)
        {
            if (_uiDoc == null) return;

            Hide(); // Hide dialog so user can interact with the model

            PcfExportApp.ExportHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                try
                {
                    var refs = uiDoc.Selection.PickObjects(
                        Autodesk.Revit.UI.Selection.ObjectType.Element,
                        new Revit.FabricationPartFilter(),
                        "Select fabrication parts for PCF export, then press Finish in the ribbon.");

                    var selectedIds = refs.Select(r => r.ElementId).ToList();
                    uiDoc.Selection.SetElementIds(selectedIds);

                    // Reconcile the visibility dim with the newly-picked selection.
                    _visScope?.UpdateSelection(selectedIds);

                    var newTokens = ExportCommand.ReadLineNumberTokens(doc, uiDoc);

                    Dispatcher.Invoke(() =>
                    {
                        ViewModel.SetTokens(newTokens);
                        if (newTokens.Count == 1 && string.IsNullOrWhiteSpace(ViewModel.PipelineReference))
                        {
                            ViewModel.PipelineReference = newTokens[0].Display == "<BLANK>"
                                ? string.Empty
                                : newTokens[0].Display;
                        }
                        Show(); // Return to dialog with updated selection
                    });
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    Dispatcher.Invoke(() => Show()); // User cancelled pick — return to dialog
                }
                catch
                {
                    Dispatcher.Invoke(() => Show());
                }
            });
            PcfExportApp.ExportEvent!.Raise();
        }

        // ── Connection selection (Connected From / Connected To) ──────────

        private void SelectConnectedFrom_Click(object sender, RoutedEventArgs e) => PickConnection(isFrom: true);
        private void SelectConnectedTo_Click(object sender, RoutedEventArgs e) => PickConnection(isFrom: false);
        private void ClearConnectedFrom_Click(object sender, RoutedEventArgs e) => ViewModel.ClearConnectedFrom();
        private void ClearConnectedTo_Click(object sender, RoutedEventArgs e) => ViewModel.ClearConnectedTo();

        private void PickConnection(bool isFrom)
        {
            if (_uiDoc == null) return;
            Hide();

            PcfExportApp.ExportHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc = uiDoc.Document;

                try
                {
                    string prompt = isFrom
                        ? "Select the upstream equipment or pipework this line connects FROM."
                        : "Select the downstream equipment or pipework this line connects TO.";

                    var reference = uiDoc.Selection.PickObject(
                        Autodesk.Revit.UI.Selection.ObjectType.Element, prompt);

                    var element = doc.GetElement(reference.ElementId);
                    if (element == null) { Dispatcher.Invoke(() => Show()); return; }

                    var connection = BuildConnection(element, uiDoc);

                    // Connected From/To targets are part of the export
                    // context — un-dim them.
                    _visScope?.AddOne(reference.ElementId);

                    Dispatcher.Invoke(() =>
                    {
                        if (isFrom)
                            ViewModel.SetConnectedFrom(connection);
                        else
                            ViewModel.SetConnectedTo(connection);
                        Show();
                    });
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    Dispatcher.Invoke(() => Show());
                }
                catch
                {
                    Dispatcher.Invoke(() => Show());
                }
            });
            PcfExportApp.ExportEvent!.Raise();
        }

        /// <summary>
        /// Builds a PcfConnection from a selected Revit element.
        /// Mechanical Equipment → reads Tag → END-CONNECTION-EQUIPMENT
        /// MEP Fabrication Pipework → reads Line Number → END-CONNECTION-PIPELINE
        /// </summary>
        /// <summary>Returns null if user cancels the operation.</summary>
        private static PcfConnection? BuildConnection(Element element, UIDocument uiDoc)
        {
            var conn = new PcfConnection();
            var loc = element.Location;
            XYZ pos = loc is LocationPoint lp ? lp.Point
                    : loc is LocationCurve lc ? (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) * 0.5
                    : XYZ.Zero;

            conn.Position = new[]
            {
                UnitUtils.ConvertFromInternalUnits(pos.X, UnitTypeId.Inches),
                UnitUtils.ConvertFromInternalUnits(pos.Y, UnitTypeId.Inches),
                UnitUtils.ConvertFromInternalUnits(pos.Z, UnitTypeId.Inches),
            };

            // Check category
            string catName = element.Category?.Name ?? "";
            bool isMechEquip = catName.Contains("Mechanical Equipment", StringComparison.OrdinalIgnoreCase);
            bool isFabPipe = element is Autodesk.Revit.DB.FabricationPart;

            if (isMechEquip)
            {
                conn.Type = PcfConnection.ConnectionType.Equipment;

                // Find tag from Identity Data group only (not General group which has a different "Tag")
                string tag = "";
                Parameter? tagParam = null;
                string identityDataGroup = "autodesk.parameter.group:identityData-1.0.0";
                foreach (Parameter p in element.Parameters)
                {
                    try
                    {
                        if (p.Definition.Name.Equals("Tag", StringComparison.OrdinalIgnoreCase)
                            && p.StorageType == StorageType.String
                            && p.Definition.GetGroupTypeId().TypeId.Contains("identityData", StringComparison.OrdinalIgnoreCase))
                        {
                            tagParam = p;
                            string? val = p.AsString();
                            if (!string.IsNullOrWhiteSpace(val)) { tag = val; break; }
                        }
                    }
                    catch { }
                }
                // Fallback: Mark parameter
                if (string.IsNullOrWhiteSpace(tag))
                    tag = element.LookupParameter("Mark")?.AsString() ?? "";
                if (string.IsNullOrWhiteSpace(tag))
                    tag = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";

                // If no tag found, show tag validation dialog (same style as valve tags)
                if (string.IsNullOrWhiteSpace(tag))
                {
                    var equipItem = new ValveTagItem
                    {
                        ElementId = element.Id,
                        UniqueId = element.UniqueId,
                        Description = $"{element.Name} (Mechanical Equipment)",
                    };

                    var tagDialog = new TagValidationDialog(
                        new List<ValveTagItem> { equipItem },
                        uiDoc,
                        new HashSet<string>(),
                        isEquipmentMode: true);

                    bool confirmed = false;
                    tagDialog.OnConfirmed = () => confirmed = true;
                    tagDialog.ShowDialog();

                    if (confirmed && !string.IsNullOrWhiteSpace(equipItem.Tag))
                    {
                        tag = equipItem.Tag;
                        // Write the tag back to the element
                        try
                        {
                            using var tx = new Transaction(uiDoc.Document, "Assign Equipment Tag");
                            tx.Start();
                            if (tagParam != null)
                                tagParam.Set(tag);
                            else
                                element.LookupParameter("Tag")?.Set(tag);
                            tx.Commit();
                        }
                        catch { }
                    }
                    else
                        return null; // User cancelled
                }

                conn.Reference = tag;
                conn.DisplayText = $"(Mechanical Equipment {tag})";
            }
            else if (isFabPipe)
            {
                conn.Type = PcfConnection.ConnectionType.Pipeline;
                string lineNum = element.LookupParameter("Line Number")?.AsString() ?? "";
                if (string.IsNullOrWhiteSpace(lineNum))
                {
                    var p = element.LookupParameter("Line Number");
                    if (p != null && p.StorageType == StorageType.Integer)
                    {
                        int val = p.AsInteger();
                        if (val > 0) lineNum = val.ToString();
                    }
                }

                // If no line number, prompt user
                if (string.IsNullOrWhiteSpace(lineNum))
                {
                    var td = new TaskDialog("Missing Line Number");
                    td.MainContent = "The selected MEP Fabrication Pipework does not have a Line Number.\n" +
                                     "You may need to assign it in the model before proceeding.";
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Proceed without Line Number");
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel");
                    td.CommonButtons = TaskDialogCommonButtons.None;
                    var result = td.Show();

                    if (result != TaskDialogResult.CommandLink1)
                        return null; // User cancelled
                }

                conn.Reference = lineNum;
                conn.DisplayText = $"(Line Number {lineNum})";
            }
            else
            {
                // Generic fallback — treat as equipment
                conn.Type = PcfConnection.ConnectionType.Equipment;
                string tag = "";
                foreach (Parameter p in element.Parameters)
                {
                    try
                    {
                        if (p.Definition.Name.Equals("Tag", StringComparison.OrdinalIgnoreCase)
                            && p.StorageType == StorageType.String)
                        {
                            string? val = p.AsString();
                            if (!string.IsNullOrWhiteSpace(val)) { tag = val; break; }
                        }
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(tag))
                    tag = element.LookupParameter("Mark")?.AsString() ?? "";
                if (string.IsNullOrWhiteSpace(tag))
                    tag = element.Name ?? "";
                conn.Reference = tag;
                conn.DisplayText = $"({catName} {tag})";
            }

            return conn;
        }

        /// <summary>Simple text input prompt using a WPF InputBox-style dialog.</summary>
        private static string? PromptForInput(string title, string prompt)
        {
            var win = new Window
            {
                Title = title,
                Width = 350, Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
            };
            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
            var tb = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(tb);
            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var okBtn = new System.Windows.Controls.Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 70, IsCancel = true };
            okBtn.Click += (s, e) => { win.DialogResult = true; win.Close(); };
            cancelBtn.Click += (s, e) => { win.DialogResult = false; win.Close(); };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            sp.Children.Add(btnPanel);
            win.Content = sp;
            tb.Focus();

            return win.ShowDialog() == true ? tb.Text : null;
        }

        // ── Cancel ────────────────────────────────────────────────────────────

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        // ── Token label click — highlights elements in model ──────────────────

        private void TokenLink_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe &&
                fe.Tag is LineNumberToken token &&
                _uiDoc != null)
            {
                try { _uiDoc.Selection.SetElementIds(new List<ElementId>(token.ElementIds)); }
                catch { /* selection may fail if view is not compatible */ }
            }
        }

        // ── Remove (×) button — excludes token from export ────────────────────

        private void RemoveToken_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LineNumberToken token)
                ViewModel.RemoveToken(token);
        }

        // ── Learn More — Plant 3D compatibility info ─────────────────────────

        private void LearnMorePlant3D_Click(object sender, MouseButtonEventArgs e)
        {
            string info =
                "When enabled, the following ITEM-DESCRIPTION formats are applied:\n\n" +
                "COMPONENT TYPE          EXAMPLE\n" +
                "────────────────────────────────────────────\n" +
                "Elbows                        ELL 90 LR, BW, ASTM A-53, Carbon Steel\n" +
                "Tees                            TEE, BW, ASTM A-53, Carbon Steel\n" +
                "Reducing Tees              TEE REDUCING, BW, ASTM A-53, Carbon Steel\n" +
                "Flanges                       FLANGE WN, ASME B16.5, Carbon Steel\n" +
                "Valves                         GATE VALVE, FLG, ASTM A-53, Cast Steel\n" +
                "Reducers                     REDUCER CONC, BW, ASTM A-53, Carbon Steel\n" +
                "Caps                            CAP, BW, ASTM A-53, Carbon Steel\n" +
                "Olets                           WELDOLET, BW, ASTM A-53, Carbon Steel\n\n" +
                "Format structure:\n" +
                "  [Type from SKEY], [End Connection], [Specification], [Material]\n\n" +
                "Pipes and Welds are not modified.\n" +
                "This improves compatibility with Plant 3D's PCF to Pipe and PCF to Iso tools.";

            MessageBox.Show(info, "Standardized Descriptions — Plant 3D Compatible",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A detected line-number value and the elements that carry it.
    /// Display is "1001", "1002", or "&lt;BLANK&gt;" for parts with no value.
    /// </summary>
    public class LineNumberToken
    {
        public string                   Display    { get; init; } = string.Empty;
        public IReadOnlyList<ElementId> ElementIds { get; init; } = [];
    }

    // ─────────────────────────────────────────────────────────────────────────

    public class ExportViewModel : INotifyPropertyChanged
    {
        private List<LineNumberToken> _tokens;

        public IReadOnlyList<LineNumberToken> LineNumberTokens => _tokens;

        public bool IsSingleLineNumber    => _tokens.Count <= 1;
        public bool IsMultipleLineNumbers => _tokens.Count > 1;

        public ExportViewModel(IReadOnlyList<LineNumberToken> lineNumberTokens)
        {
            _tokens = new List<LineNumberToken>(lineNumberTokens);
        }

        public void SetTokens(IReadOnlyList<LineNumberToken> tokens)
        {
            _tokens = new List<LineNumberToken>(tokens);
            NotifyTokenChange();
        }

        public void RemoveToken(LineNumberToken token)
        {
            _tokens = _tokens.Where(t => t != token).ToList();
            NotifyTokenChange();
        }

        private void NotifyTokenChange()
        {
            OnPropertyChanged(nameof(LineNumberTokens));
            OnPropertyChanged(nameof(IsSingleLineNumber));
            OnPropertyChanged(nameof(IsMultipleLineNumbers));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(RequiredFieldHint));
            OnPropertyChanged(nameof(FilenameHint));
            OnPropertyChanged(nameof(PickedElementsHint));
        }

        // ── Output folder ─────────────────────────────────────────────────────
        private string _outputFolderPath = string.Empty;
        public string OutputFolderPath
        {
            get => _outputFolderPath;
            set { SetField(ref _outputFolderPath, value); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(FilenameHint)); }
        }

        // ── Custom filename override (single-mode) ────────────────────────────
        private string _customFileName = string.Empty;
        public string CustomFileName
        {
            get => _customFileName;
            set { SetField(ref _customFileName, value); OnPropertyChanged(nameof(FilenameHint)); }
        }

        /// <summary>
        /// Informational text shown below the folder picker in single-mode.
        /// Reflects the effective filename the export will produce.
        /// </summary>
        public string FilenameHint
        {
            get
            {
                string stem = string.IsNullOrWhiteSpace(CustomFileName)
                    ? PipelineReference.Trim()
                    : CustomFileName.Trim();

                if (string.IsNullOrWhiteSpace(stem))
                    return "File will be saved as: (enter Pipeline Reference or Custom Filename)";

                stem = string.Concat(stem.Split(Path.GetInvalidFileNameChars()));
                return $"File will be saved as: {stem}.pcf";
            }
        }

        // ── Pipeline settings ─────────────────────────────────────────────────
        private string _pipelineReference = string.Empty;
        public string PipelineReference
        {
            get => _pipelineReference;
            set { SetField(ref _pipelineReference, value); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(FilenameHint)); }
        }

        private string _spoolIdentifier = string.Empty;
        public string SpoolIdentifier
        {
            get => _spoolIdentifier;
            set => SetField(ref _spoolIdentifier, value);
        }

        private string _revision = string.Empty;
        public string Revision
        {
            get => _revision;
            set => SetField(ref _revision, value);
        }

        private string _area = string.Empty;
        public string Area
        {
            get => _area;
            set => SetField(ref _area, value);
        }

        // ── Selection mode ────────────────────────────────────────────────────
        private SelectionMode _selectionMode = SelectionMode.CurrentSelection;

        public bool ModeCurrentSelection
        {
            get => _selectionMode == SelectionMode.CurrentSelection;
            set { if (value) { _selectionMode = SelectionMode.CurrentSelection; NotifyModes(); } }
        }
        public bool ModePickElements
        {
            get => _selectionMode == SelectionMode.PickElements;
            set { if (value) { _selectionMode = SelectionMode.PickElements; NotifyModes(); } }
        }
        public bool ModeAllInView
        {
            get => _selectionMode == SelectionMode.AllInActiveView;
            set { if (value) { _selectionMode = SelectionMode.AllInActiveView; NotifyModes(); } }
        }

        // ── Include flags ─────────────────────────────────────────────────────
        private bool _includePipes    = true;
        private bool _includeFittings = true;
        private bool _includeWelds    = true;
        private bool _includeHangers  = false;

        public bool IncludePipes    { get => _includePipes;    set => SetField(ref _includePipes,    value); }
        public bool IncludeFittings { get => _includeFittings; set => SetField(ref _includeFittings, value); }
        public bool IncludeWelds    { get => _includeWelds;    set => SetField(ref _includeWelds,    value); }
        public bool IncludeHangers  { get => _includeHangers;  set => SetField(ref _includeHangers,  value); }

        // ── Compatibility ─────────────────────────────────────────────────
        private bool _useStandardDescriptions = false;
        public bool UseStandardDescriptions { get => _useStandardDescriptions; set => SetField(ref _useStandardDescriptions, value); }

        // ── Pick elements hint ────────────────────────────────────────────────
        public string PickedElementsHint =>
            _tokens.Count > 0
                ? $"{_tokens.SelectMany(t => t.ElementIds).Count()} element(s) selected"
                : "No elements selected";

        // ── Validation ────────────────────────────────────────────────────────
        public bool CanExport =>
            !string.IsNullOrWhiteSpace(OutputFolderPath) &&
            !string.IsNullOrWhiteSpace(PipelineReference) &&
            _tokens.Count > 0;

        public string RequiredFieldHint => "* Required fields";

        // ── Connections (flow direction) ──────────────────────────────────
        private PcfConnection? _connectedFrom;
        private PcfConnection? _connectedTo;

        public string ConnectedFromText => _connectedFrom?.DisplayText ?? "(not set)";
        public string ConnectedToText => _connectedTo?.DisplayText ?? "(not set)";
        public bool HasConnectedFrom => _connectedFrom != null;
        public bool HasConnectedTo => _connectedTo != null;

        public void SetConnectedFrom(PcfConnection? conn)
        {
            _connectedFrom = conn;
            OnPropertyChanged(nameof(ConnectedFromText));
            OnPropertyChanged(nameof(HasConnectedFrom));
        }

        public void SetConnectedTo(PcfConnection? conn)
        {
            _connectedTo = conn;
            OnPropertyChanged(nameof(ConnectedToText));
            OnPropertyChanged(nameof(HasConnectedTo));
        }

        public void ClearConnectedFrom() => SetConnectedFrom(null);
        public void ClearConnectedTo() => SetConnectedTo(null);

        // ── Conversion ────────────────────────────────────────────────────────
        public ExportOptions ToOptions()
        {
            // Compute the single-mode output file path here so RunExport doesn't need to.
            string singleFilePath = string.Empty;
            if (IsSingleLineNumber)
            {
                string stem = string.IsNullOrWhiteSpace(CustomFileName)
                    ? PipelineReference.Trim()
                    : CustomFileName.Trim();
                stem = string.Concat(stem.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(stem)) stem = "export";
                singleFilePath = Path.Combine(OutputFolderPath.Trim(), stem + ".pcf");
            }

            return new ExportOptions
            {
                OutputFilePath    = singleFilePath,
                OutputFolderPath  = OutputFolderPath.Trim(),
                Units             = PcfUnits.Inch,
                PipelineReference = PipelineReference.Trim(),
                SpoolIdentifier   = SpoolIdentifier.Trim(),
                IsogenFlsFile     = "ISOGEN.FLS",
                ProjectIdentifier = string.Empty,
                Revision          = Revision.Trim(),
                Area              = Area.Trim(),
                SelectionMode     = _selectionMode,
                IncludePipes      = IncludePipes,
                IncludeFittings   = IncludeFittings,
                IncludeWelds      = IncludeWelds,
                IncludeHangers    = IncludeHangers,
                UseStandardDescriptions = UseStandardDescriptions,
                ConnectedFrom = _connectedFrom,
                ConnectedTo   = _connectedTo,
            };
        }

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

        private void NotifyModes()
        {
            OnPropertyChanged(nameof(ModeCurrentSelection));
            OnPropertyChanged(nameof(ModePickElements));
            OnPropertyChanged(nameof(ModeAllInView));
            OnPropertyChanged(nameof(CanExport));
        }
    }
}
