using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

#pragma warning disable VSSDK007, VSTHRD110

namespace VS_LaunchArguments
{
    // ── History data ──────────────────────────────────────────────────────────────
    // Moved here from HistoryWindow.xaml.cs (that file can now be removed from the project).

    public class HistoryEntry
    {
        public string Command { get; set; }
        public long   Time    { get; set; }

        public string TimeText =>
            Time > 0
                ? DateTimeOffset.FromUnixTimeSeconds(Time).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : string.Empty;
    }

    // ── Control ───────────────────────────────────────────────────────────────────

    public partial class CommandLineToolWindowControl : UserControl, IVsUpdateSolutionEvents, IVsSelectionEvents
    {
        private const uint SEID_StartupProject = 3;

        private DTE2 _dte;
        private bool _updatingFields;

        private SolutionEvents _solutionEvents;

        private IVsSolutionBuildManager2 _buildManager;
        private uint _buildManagerCookie;

        private IVsMonitorSelection _monitorSelection;
        private uint _selectionCookie;

        private DispatcherTimer _saveTimer;

        // Tracks whether we're subscribed to the root visual's PreviewMouseDown
        // for history popup click-outside-to-close detection.
        private bool _historyOutsideSubscribed;

        // ── P/Invoke — strip WS_EX_TOPMOST from the scratchpad popup HWND ─────────

        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE     = 0x0002;
        private const uint SWP_NOSIZE     = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        // ── Lifetime ──────────────────────────────────────────────────────────────

        public CommandLineToolWindowControl()
        {
            InitializeComponent();
            ThreadHelper.ThrowIfNotOnUIThread();

            _dte = (DTE2)Package.GetGlobalService(typeof(DTE));

            _solutionEvents = _dte.Events.SolutionEvents;
            _solutionEvents.Opened        += OnSolutionOpened;
            _solutionEvents.BeforeClosing += OnSolutionBeforeClosing;
            _solutionEvents.AfterClosing  += OnSolutionClosed;

            _buildManager = Package.GetGlobalService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
            if (_buildManager != null)
                _buildManager.AdviseUpdateSolutionEvents(this, out _buildManagerCookie);

            _monitorSelection = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (_monitorSelection != null)
                _monitorSelection.AdviseSelectionEvents(this, out _selectionCookie);

            Application.Current.Deactivated += OnApplicationDeactivated;

            ScratchpadTextBox.TextChanged += OnScratchpadTextChanged;

            Unloaded += OnUnloaded;

            LoadScratchpadState();
            LoadFromActiveProject();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            FlushPendingSave();
            if (_saveTimer != null)
            {
                _saveTimer.Stop();
                _saveTimer = null;
            }

            Application.Current.Deactivated -= OnApplicationDeactivated;

            if (_buildManagerCookie != 0 && _buildManager != null)
            {
                _buildManager.UnadviseUpdateSolutionEvents(_buildManagerCookie);
                _buildManagerCookie = 0;
            }
            if (_selectionCookie != 0 && _monitorSelection != null)
            {
                _monitorSelection.UnadviseSelectionEvents(_selectionCookie);
                _selectionCookie = 0;
            }
        }

        // ── Solution events ───────────────────────────────────────────────────────

        private void OnSolutionOpened()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                LoadScratchpadState();
                LoadFromActiveProject();
            });
        }

        private void OnSolutionBeforeClosing()
        {
            FlushPendingSave();
        }

        private void OnSolutionClosed()
        {
            _updatingFields = true;
            ArgsTextBox.Text           = string.Empty;
            WorkingDirTextBox.Text     = string.Empty;
            ScratchpadTextBox.Text     = string.Empty;
            ScratchpadToggle.IsChecked = false;
            _updatingFields = false;
        }

        // ── Scratchpad / toggle persistence ───────────────────────────────────────

        private const byte StateFormatVersion = 1;

        private string GetScratchpadStatePath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var solutionPath = _dte?.Solution?.FullName;
                if (string.IsNullOrEmpty(solutionPath)) return null;

                var dir = Path.Combine(
                    Path.GetDirectoryName(solutionPath),
                    ".vs",
                    Path.GetFileNameWithoutExtension(solutionPath),
                    "VSCmdPanel");
                return Path.Combine(dir, "state.dat");
            }
            catch { return null; }
        }

        private void LoadScratchpadState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = GetScratchpadStatePath();
            if (path == null) return;

            string text = string.Empty;
            bool toggle = false;

            if (File.Exists(path))
            {
                try
                {
                    using (var stream = File.OpenRead(path))
                    using (var reader = new BinaryReader(stream))
                    {
                        byte version = reader.ReadByte();
                        if (version == StateFormatVersion)
                        {
                            toggle = reader.ReadBoolean();
                            text   = reader.ReadString();
                        }
                    }
                }
                catch { }
            }

            _updatingFields = true;
            ScratchpadTextBox.Text     = text;
            ScratchpadToggle.IsChecked = toggle;
            _updatingFields = false;
        }

        private void SaveScratchpadStateNow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = GetScratchpadStatePath();
            if (path == null) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = File.Create(path))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(StateFormatVersion);
                    writer.Write(ScratchpadToggle.IsChecked == true);
                    writer.Write(ScratchpadTextBox.Text ?? string.Empty);
                }
            }
            catch { }
        }

        private void ScheduleScratchpadSave()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_updatingFields) return;

            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _saveTimer.Tick += OnSaveTimerTick;
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnSaveTimerTick(object sender, EventArgs e)
        {
            _saveTimer.Stop();
            SaveScratchpadStateNow();
        }

        private void FlushPendingSave()
        {
            if (_saveTimer != null && _saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveScratchpadStateNow();
            }
        }

        private void OnScratchpadTextChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleScratchpadSave();
        }


        // ── Command-line history popup ────────────────────────────────────────────

        // When the popup is open we subscribe to PreviewMouseDown on the root visual so
        // any click outside the popup (or the H button itself) closes it.  The H button
        // is excluded from "outside" so clicking it while open goes through the toggle
        // path in HistoryButton_Click instead of the outside-click path.
        private void SubscribeHistoryOutsideClick()
        {
            if (_historyOutsideSubscribed) return;
            var root = PresentationSource.FromVisual(this)?.RootVisual as UIElement;
            if (root == null) return;
            root.AddHandler(PreviewMouseDownEvent,
                new MouseButtonEventHandler(OnHistoryOutsideMouseDown), true);
            _historyOutsideSubscribed = true;
        }

        private void UnsubscribeHistoryOutsideClick()
        {
            if (!_historyOutsideSubscribed) return;
            var root = PresentationSource.FromVisual(this)?.RootVisual as UIElement;
            root?.RemoveHandler(PreviewMouseDownEvent,
                new MouseButtonEventHandler(OnHistoryOutsideMouseDown));
            _historyOutsideSubscribed = false;
        }

        private void OnHistoryOutsideMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!HistoryPopupContent.IsMouseOver && !HistoryButton.IsMouseOver)
                CloseHistoryPopup();
        }

        private void CloseHistoryPopup()
        {
            UnsubscribeHistoryOutsideClick();
            HistoryPopup.IsOpen = false;
        }

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (HistoryPopup.IsOpen)
            {
                CloseHistoryPopup();
                return;
            }

            var path = GetHistoryFilePath(out var exeName);
            if (path == null)
            {
                MessageBox.Show("Could not determine target exe for the active startup project.",
                    "Command History", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!File.Exists(path))
            {
                MessageBox.Show("No history file found at:\n\n" + path,
                    "Command History", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var entries = LoadHistoryFile(path);
            if (entries.Count == 0)
            {
                MessageBox.Show("History file is empty or could not be parsed:\n\n" + path,
                    "Command History", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            HistoryHeaderText.Text       = "Command history — " + exeName;
            HistoryListBox.ItemsSource   = entries;
            HistoryListBox.SelectedIndex = 0;
            HistoryPopup.Width           = MeasureHistoryPopupWidth(entries);
            HistoryPopup.IsOpen          = true;
            SubscribeHistoryOutsideClick();
            HistoryListBox.Focus();
        }

        private void HistoryListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitHistorySelection();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseHistoryPopup();
                ArgsTextBox.Focus();
                e.Handled = true;
            }
        }

        private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            CommitHistorySelection();
        }

        private void CommitHistorySelection()
        {
            var entry = HistoryListBox.SelectedItem as HistoryEntry;
            if (entry != null)
                ArgsTextBox.Text = entry.Command;
            CloseHistoryPopup();
        }

        // Click on the per-row trashcan. Removes the entry from the JSON file and
        // refreshes the displayed list.  Marked Handled so the click doesn't also
        // select/commit the row underneath.
        private void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var entry = (sender as FrameworkElement)?.DataContext as HistoryEntry;
            if (entry == null) { e.Handled = true; return; }

            var path = GetHistoryFilePath(out _);
            if (path == null || !File.Exists(path)) { e.Handled = true; return; }

            if (DeleteHistoryEntry(path, entry))
            {
                if (HistoryListBox.ItemsSource is List<HistoryEntry> list)
                {
                    list.Remove(entry);
                    HistoryListBox.Items.Refresh();
                    if (list.Count == 0)
                        CloseHistoryPopup();
                }
            }
            e.Handled = true;
        }

        // Reads the history file, removes the first entry matching target's (command, time),
        // and writes it back. Preserves all other top-level keys (e.g. colorScheme) and
        // all other history entries unchanged. Returns true if an entry was removed.
        private bool DeleteHistoryEntry(string path, HistoryEntry target)
        {
            try
            {
                var json       = File.ReadAllText(path);
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root       = serializer.Deserialize<Dictionary<string, object>>(json);

                if (root == null) return false;
                if (!root.TryGetValue("history", out var histObj)) return false;
                if (!(histObj is IEnumerable historyItems)) return false;

                var newHistory = new List<object>();
                bool removed = false;

                foreach (var item in historyItems)
                {
                    if (!removed && item is Dictionary<string, object> entry)
                    {
                        string cmd = entry.TryGetValue("command", out var c) ? c as string : null;
                        long time = 0;
                        if (entry.TryGetValue("time", out var t))
                        {
                            switch (t)
                            {
                                case int ti:     time = ti;       break;
                                case long tl:    time = tl;       break;
                                case decimal td: time = (long)td; break;
                                case double tdb: time = (long)tdb;break;
                            }
                        }

                        if (cmd == target.Command && time == target.Time)
                        {
                            removed = true;
                            continue;
                        }
                    }
                    newHistory.Add(item);
                }

                if (!removed) return false;

                root["history"] = newHistory;
                File.WriteAllText(path, serializer.Serialize(root));
                return true;
            }
            catch { return false; }
        }

        // Measures the longest command string at the current font to set popup width.
        private double MeasureHistoryPopupWidth(IList<HistoryEntry> entries)
        {
            const double minWidth   = 400;
            const double maxWidth   = 1200;
            const double timestampW = 150; // "yyyy-MM-dd HH:mm" + italic margin
            const double deleteW    = 24;  // trashcan button column
            const double chrome     = 32;  // borders + scrollbar + item padding

            string longest = string.Empty;
            foreach (var entry in entries)
                if (entry.Command.Length > longest.Length)
                    longest = entry.Command;

            if (string.IsNullOrEmpty(longest))
                return minWidth;

            try
            {
                var ft = new FormattedText(
                    longest,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"),
                    FontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                return Math.Max(minWidth, Math.Min(maxWidth, ft.Width + timestampW + deleteW + chrome));
            }
            catch
            {
                return minWidth;
            }
        }

        // Returns %LOCALAPPDATA%\<exename>_history. Expands MSBuild macros in
        // VCDebugSettings.Command (typically $(TargetPath)), falls back to PrimaryOutput.
        private string GetHistoryFilePath(out string exeName)
        {
            exeName = null;
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var proj = GetStartupProject();
                if (proj == null || !IsCppProject(proj)) return null;

                var cfg = GetActiveVCConfig(proj);
                if (cfg == null) return null;

                var debug = cfg.DebugSettings as VCDebugSettings;
                string exePath = debug?.Command;

                if (!string.IsNullOrEmpty(exePath) && exePath.IndexOf("$(") >= 0)
                    exePath = cfg.Evaluate(exePath);

                if (string.IsNullOrEmpty(exePath))
                    exePath = cfg.PrimaryOutput;

                if (string.IsNullOrEmpty(exePath)) return null;

                var name = Path.GetFileName(exePath);
                if (string.IsNullOrEmpty(name)) return null;

                exeName = name;
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, name + "_history");
            }
            catch { return null; }
        }

        // Parses { "history": [ { "command": "...", "time": 1234567890 }, ... ] }
        // Returns entries sorted newest-first.
        private List<HistoryEntry> LoadHistoryFile(string path)
        {
            var result = new List<HistoryEntry>();
            try
            {
                var json       = File.ReadAllText(path);
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root       = serializer.Deserialize<Dictionary<string, object>>(json);

                if (root == null || !root.TryGetValue("history", out var histObj)) return result;
                if (!(histObj is IEnumerable historyItems)) return result;

                foreach (var item in historyItems)
                {
                    if (!(item is Dictionary<string, object> entry)) continue;
                    if (!entry.TryGetValue("command", out var cmdObj) || !(cmdObj is string cmd)) continue;
                    if (string.IsNullOrEmpty(cmd)) continue;

                    long time = 0;
                    if (entry.TryGetValue("time", out var tObj))
                    {
                        switch (tObj)
                        {
                            case int ti:     time = ti;      break;
                            case long tl:    time = tl;      break;
                            case decimal td: time = (long)td; break;
                            case double tdb: time = (long)tdb; break;
                        }
                    }

                    result.Add(new HistoryEntry { Command = cmd, Time = time });
                }

                result.Sort((a, b) => b.Time.CompareTo(a.Time));
            }
            catch { }
            return result;
        }

        // ── IVsUpdateSolutionEvents ───────────────────────────────────────────────

        int IVsUpdateSolutionEvents.OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RefreshFromActiveConfig();
            });
            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents.UpdateSolution_Begin(ref int pfCancelUpdate)                           => VSConstants.S_OK;
        int IVsUpdateSolutionEvents.UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) => VSConstants.S_OK;
        int IVsUpdateSolutionEvents.UpdateSolution_StartUpdate(ref int pfCancelUpdate)                     => VSConstants.S_OK;
        int IVsUpdateSolutionEvents.UpdateSolution_Cancel()                                                => VSConstants.S_OK;

        // ── IVsSelectionEvents ────────────────────────────────────────────────────

        int IVsSelectionEvents.OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            if (elementid == SEID_StartupProject)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    RefreshFromActiveConfig();
                });
            }
            return VSConstants.S_OK;
        }

        int IVsSelectionEvents.OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld,
            IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
            IVsHierarchy pHierNew, uint itemidNew,
            IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;

        int IVsSelectionEvents.OnCmdUIContextChanged(uint dwCmdID, int fActive) => VSConstants.S_OK;

        // ── Args/working-dir read/write ───────────────────────────────────────────

        private void LoadFromActiveProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var proj = GetStartupProject();
                if (proj == null || !IsCppProject(proj)) return;

                string args = GetDebugProperty(proj, DebugProperty.CommandArguments);
                string wd   = GetDebugProperty(proj, DebugProperty.WorkingDirectory);

                _updatingFields = true;
                if (!string.IsNullOrEmpty(args)) ArgsTextBox.Text       = args;
                if (!string.IsNullOrEmpty(wd))   WorkingDirTextBox.Text = wd;
                _updatingFields = false;
            }
            catch { _updatingFields = false; }
        }

        private void RefreshFromActiveConfig()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var proj = GetStartupProject();
                if (proj == null || !IsCppProject(proj)) return;

                ApplyOrSeed(proj, DebugProperty.CommandArguments, ArgsTextBox);
                ApplyOrSeed(proj, DebugProperty.WorkingDirectory,  WorkingDirTextBox);
            }
            catch { }
        }

        private void ApplyOrSeed(Project proj, DebugProperty property, TextBox field)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(field.Text))
            {
                string stored = GetDebugProperty(proj, property);
                if (!string.IsNullOrEmpty(stored))
                {
                    _updatingFields = true;
                    field.Text = stored;
                    _updatingFields = false;
                }
            }
            else
            {
                SetDebugProperty(proj, property, field.Text);
            }
        }

        private void SyncFromActiveProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var proj = GetStartupProject();
                if (proj == null || !IsCppProject(proj)) return;

                string projArgs = GetDebugProperty(proj, DebugProperty.CommandArguments);
                string projWd   = GetDebugProperty(proj, DebugProperty.WorkingDirectory);

                _updatingFields = true;
                if (projArgs != null && projArgs != ArgsTextBox.Text)
                    ArgsTextBox.Text = projArgs;
                if (projWd   != null && projWd   != WorkingDirTextBox.Text)
                    WorkingDirTextBox.Text = projWd;
                _updatingFields = false;
            }
            catch { _updatingFields = false; }
        }

        // ── TextChanged handlers ──────────────────────────────────────────────────

        private void ArgsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_updatingFields) return;
            PushToProject(DebugProperty.CommandArguments, ArgsTextBox.Text);
        }

        private void WorkingDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_updatingFields) return;
            PushToProject(DebugProperty.WorkingDirectory, WorkingDirTextBox.Text);
        }

        private void PushToProject(DebugProperty property, string value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var proj = GetStartupProject();
                if (proj != null && IsCppProject(proj))
                    SetDebugProperty(proj, property, value);
            });
        }

        // ── Focus / popup handling ────────────────────────────────────────────────

        private void OnIsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(bool)e.NewValue) return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SyncFromActiveProject();
            });
        }

        private void ScratchpadToggle_Checked(object sender, RoutedEventArgs e)
        {
            ScratchpadPopup.Width  = RootGrid.ActualWidth;
            ScratchpadPopup.IsOpen = true;
            if (!_updatingFields) SaveScratchpadStateNow();
        }

        private void ScratchpadToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            ScratchpadPopup.IsOpen = false;
            if (!_updatingFields) SaveScratchpadStateNow();
        }

        private void ScratchpadPopup_Opened(object sender, EventArgs e)
        {
            var source = PresentationSource.FromVisual(ScratchpadTextBox) as HwndSource;
            if (source != null)
                SetWindowPos(source.Handle, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void OnApplicationDeactivated(object sender, EventArgs e)
        {
            ScratchpadPopup.IsOpen = false;
            CloseHistoryPopup();
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
            ScratchpadPopup.Width = e.NewSize.Width;

        private void OnSingleLinePasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
            string text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
            string sanitized = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            e.DataObject = new DataObject(DataFormats.UnicodeText, sanitized);
        }

        // ── VCProject helpers ─────────────────────────────────────────────────────

        private enum DebugProperty { CommandArguments, WorkingDirectory }

        private string GetDebugProperty(Project project, DebugProperty property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var debug = GetActiveVCConfig(project)?.DebugSettings as VCDebugSettings;
                if (debug == null) return null;
                return property == DebugProperty.CommandArguments
                    ? debug.CommandArguments
                    : debug.WorkingDirectory;
            }
            catch { return null; }
        }

        private void SetDebugProperty(Project project, DebugProperty property, string value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var debug = GetActiveVCConfig(project)?.DebugSettings as VCDebugSettings;
                if (debug == null) return;
                if (property == DebugProperty.CommandArguments)
                    debug.CommandArguments = value ?? string.Empty;
                else
                    debug.WorkingDirectory = value ?? string.Empty;
            }
            catch { }
        }

        private VCConfiguration GetActiveVCConfig(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var vcproj = project.Object as VCProject;
            if (vcproj == null) return null;

            var cfgMgr = project.ConfigurationManager;
            if (cfgMgr == null) return null;

            var activeCfg = cfgMgr.ActiveConfiguration;
            if (activeCfg == null) return null;

            string configName   = activeCfg.ConfigurationName;
            string platformName = activeCfg.PlatformName;

            foreach (VCConfiguration cfg in (IVCCollection)vcproj.Configurations)
            {
                var platform = (VCPlatform)cfg.Platform;
                if (string.Equals(cfg.ConfigurationName, configName,   StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(platform.Name,         platformName, StringComparison.OrdinalIgnoreCase))
                    return cfg;
            }
            return null;
        }

        private Project GetStartupProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var startupProjects = _dte.Solution.SolutionBuild.StartupProjects as Array;
            if (startupProjects == null || startupProjects.Length == 0) return null;

            string uniqueName = startupProjects.GetValue(0) as string;
            if (string.IsNullOrEmpty(uniqueName)) return null;

            foreach (Project p in _dte.Solution.Projects)
            {
                var found = FindProjectByUniqueNameRecursive(p, uniqueName);
                if (found != null) return found;
            }
            return null;
        }

        private Project FindProjectByUniqueNameRecursive(Project project, string uniqueName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.Equals(project.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase))
                return project;

            if (project.ProjectItems != null)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject != null)
                    {
                        var found = FindProjectByUniqueNameRecursive(item.SubProject, uniqueName);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

        private bool IsCppProject(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return project.Object is VCProject; }
            catch { return false; }
        }
    }
}

#pragma warning restore VSSDK007, VSTHRD110
