using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

#pragma warning disable VSSDK007, VSTHRD110

namespace VS_LaunchArguments
{
    public partial class CommandLineToolWindowControl : UserControl, IVsUpdateSolutionEvents, IVsSelectionEvents
    {
        // Value 3 from __VSSELELEMID; using the literal avoids a type-availability issue in some SDK versions
        private const uint SEID_StartupProject = 3;

        private DTE2 _dte;
        private bool _updatingFields;

        // Held as fields — COM event sinks are GC'd if referenced only from locals
        private SolutionEvents _solutionEvents;

        // Fires OnActiveProjectCfgChange when config/platform changes
        private IVsSolutionBuildManager2 _buildManager;
        private uint _buildManagerCookie;

        // Fires OnElementValueChanged(SEID_StartupProject) when startup project changes
        private IVsMonitorSelection _monitorSelection;
        private uint _selectionCookie;

        // Debounce timer for scratchpad/toggle file persistence
        private DispatcherTimer _saveTimer;

        // ── P/Invoke — strip WS_EX_TOPMOST from the popup HWND ───────────────────

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

            // Store as fields to prevent GC of COM event sinks
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

            // Wire scratchpad TextChanged in code so it doesn't get tangled with other event ordering in XAML
            ScratchpadTextBox.TextChanged += OnScratchpadTextChanged;

            Unloaded += OnUnloaded;

            // If a solution is already open when the tool window is first shown, pick up its state
            LoadScratchpadState();
            LoadFromActiveProject();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Drain any debounced save before we go away
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

        // Fires while _dte.Solution.FullName is still valid — flush before the path goes away
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
        //
        // State lives in a small binary file alongside the .sln:
        //     <solutionDir>\.vs\<solutionName>\VSCmdPanel\state.dat
        //
        // This bypasses VS's .suo mechanism entirely, which is fragile because it
        // requires the package to be loaded before VS reads the .suo. Our file is
        // read/written directly by the control whenever it has a solution path —
        // no package lifetime dependencies, no autoload required.

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
            if (path == null) return; // No solution open — leave UI as-is

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
                catch
                {
                    // Corrupt or partial file — fall through with defaults
                }
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

        // Force any pending debounced save to commit immediately
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

        // ── IVsUpdateSolutionEvents — fires on config/platform change ─────────────

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

        // ── IVsSelectionEvents — fires on startup project change ──────────────────

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
                if (proj == null || !IsCppProject(proj))
                    return;

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
                if (proj == null || !IsCppProject(proj))
                    return;

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
                if (proj == null || !IsCppProject(proj))
                    return;

                string projArgs = GetDebugProperty(proj, DebugProperty.CommandArguments);
                string projWd   = GetDebugProperty(proj, DebugProperty.WorkingDirectory);

                _updatingFields = true;
                if (projArgs != null && projArgs != ArgsTextBox.Text)
                    ArgsTextBox.Text = projArgs;
                if (projWd != null && projWd != WorkingDirTextBox.Text)
                    WorkingDirTextBox.Text = projWd;
                _updatingFields = false;
            }
            catch { _updatingFields = false; }
        }

        // ── Args/WD TextChanged handlers ──────────────────────────────────────────

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

        private void OnEditFieldGotFocus(object sender, RoutedEventArgs e)
        {
            if (ScratchpadToggle.IsChecked != true) return;
            ScratchpadPopup.Width = RootGrid.ActualWidth;
            ScratchpadPopup.IsOpen = true;
        }

        private void OnAnyFieldLostFocus(object sender, RoutedEventArgs e)
        {
            // Defer one dispatcher pass so the incoming focus target is known before deciding to close.
            // DispatcherPriority.Background is intentional — JoinableTaskFactory has no priority equivalent.
#pragma warning disable VSTHRD001
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!ArgsTextBox.IsKeyboardFocused &&
                    !WorkingDirTextBox.IsKeyboardFocused &&
                    !ScratchpadTextBox.IsKeyboardFocused)
                {
                    ScratchpadPopup.IsOpen = false;
                }
            }));
#pragma warning restore VSTHRD001
        }

        private void ScratchpadToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (ArgsTextBox.IsKeyboardFocused || WorkingDirTextBox.IsKeyboardFocused)
            {
                ScratchpadPopup.Width = RootGrid.ActualWidth;
                ScratchpadPopup.IsOpen = true;
            }
            if (!_updatingFields) SaveScratchpadStateNow();
        }

        private void ScratchpadToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            ScratchpadPopup.IsOpen = false;
            if (!_updatingFields) SaveScratchpadStateNow();
        }

        // WPF re-creates the popup HWND on every open, so WS_EX_TOPMOST must be stripped each time.
        private void ScratchpadPopup_Opened(object sender, EventArgs e)
        {
            var source = PresentationSource.FromVisual(ScratchpadTextBox) as HwndSource;
            if (source != null)
                SetWindowPos(source.Handle, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void OnApplicationDeactivated(object sender, EventArgs e) =>
            ScratchpadPopup.IsOpen = false;

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
                var debug = GetActiveDebugSettings(project);
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
                var debug = GetActiveDebugSettings(project);
                if (debug == null) return;
                if (property == DebugProperty.CommandArguments)
                    debug.CommandArguments = value ?? string.Empty;
                else
                    debug.WorkingDirectory = value ?? string.Empty;
            }
            catch { }
        }

        private VCDebugSettings GetActiveDebugSettings(Project project)
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
                    return (VCDebugSettings)cfg.DebugSettings;
            }
            return null;
        }

        private Project GetStartupProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var startupProjects = _dte.Solution.SolutionBuild.StartupProjects as Array;
            if (startupProjects == null || startupProjects.Length == 0)
                return null;

            string uniqueName = startupProjects.GetValue(0) as string;
            if (string.IsNullOrEmpty(uniqueName))
                return null;

            foreach (Project p in _dte.Solution.Projects)
            {
                var found = FindProjectByUniqueNameRecursive(p, uniqueName);
                if (found != null)
                    return found;
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
                        if (found != null)
                            return found;
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
