using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RxdkVs.Package.Commands;
using RxdkVs.Package.Services;

namespace RxdkVs.Package.ToolWindow
{
    /// <summary>
    /// WPF content for the RXDK tool window. Buttons re-use the package's command surface by
    /// invoking the corresponding CommandID on the OleMenuCommandService, so there's exactly one
    /// implementation of each action (in RxdkCommands) whether it's triggered from the menu or here.
    /// The IP label is refreshed from `Rxdk.Cli xbox-ip`.
    /// </summary>
    public partial class RxdkToolWindowControl : UserControl
    {
        private RxdkPackage _package;

        public RxdkToolWindowControl()
        {
            InitializeComponent();
            LoadLogo();
        }

        // Loads the extension icon (deployed next to the DLL as Resources\extension-icon.png)
        // into the header. Best-effort: on any failure the header just shows the "RXDK" title.
        private void LoadLogo()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(GetType().Assembly.Location);
                var path = System.IO.Path.Combine(dir, "Resources", "extension-icon.png");
                if (!System.IO.File.Exists(path)) return;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                LogoImage.Source = bmp;
            }
            catch { /* header shows the title without a logo */ }
        }

        public void Initialize(RxdkPackage package)
        {
            _package = package;
            _ = RefreshAsync();
        }

        // ---- button handlers: dispatch to the shared command IDs ----

        // Console
        private void OnReboot(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdRebootConsole);
        private void OnSetIp(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdSetXboxIp, refreshAfter: true);
        // Folders
        private void OnOpenSdkFolder(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdOpenSdkFolder);
        private void OnOpenToolsFolder(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdOpenToolsFolder);
        private void OnOpenDocsFolder(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdOpenDocsFolder);
        // Documentation
        private void OnOpenSdkDocs(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdOpenSdkDocs);
        private void OnOpenExtensionDocs(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdOpenExtensionDocs);
        // Tools
        private void OnLaunchXbwatson(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdLaunchXbwatson);
        private void OnLaunchNeighborhoodApp(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdLaunchXbNeighborhood);
        private void OnOpenXboxNeighborhood(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdOpenXboxNeighborhood);
        private void OnInstallXboxNeighborhood(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdInstallXboxNeighborhood);
        private void OnCycleGlobals(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdCycleGlobalsScope);
        // Project
        private void OnImportProject(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdImportProject);
        // Setup — one button orchestrates all installers; the individual commands
        // (CmdInstallBuildTools/CmdInstallXboxPlatform/CmdInstallDotNet) remain on the RXDK menu.
        private void OnCompleteSetup(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdSetupPrerequisites);
        private void OnSettings(object sender, RoutedEventArgs e) => Exec(CommandIds.CmdOpenSettings);
        private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

        private void Exec(int commandId, bool refreshAfter = false)
        {
            if (_package == null)
            {
                return;
            }
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var svc = (OleMenuCommandService)await _package.GetServiceAsync(typeof(IMenuCommandService));
                var id = new CommandID(RxdkPackageGuids.CommandSet, commandId);
                svc?.GlobalInvoke(id);
                if (refreshAfter)
                {
                    await RefreshAsync();
                }
            }).FileAndForget("rxdk/toolwindow");
        }

        // ---- IP refresh ----

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            if (_package == null)
            {
                return;
            }
            SetStatus("Querying devkit…");
            string ip = null;
            try
            {
                ip = await ProbeIpAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"IP query failed: {ex.Message}");
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IpText.Text = string.IsNullOrEmpty(ip) ? "(none configured)" : ip;
            SetStatus(string.IsNullOrEmpty(ip)
                ? "No Xbox console configured. Click Set… to enter an IP."
                : "Ready.");

            await LoadComponentVersionsAsync();
        }

        // ---- Components: installed vs available versions + per-component update ----

        // Display name -> the CLI verb that installs *or* updates it (both clone-or-fetch/reset).
        private static readonly (string Name, string Verb)[] ComponentVerbs =
        {
            ("SDK", "install-sdk"),
            ("Docs", "install-docs"),
            ("Tools", "install-tools"),
            ("Samples", "install-samples"),
        };

        private sealed class ComponentRow
        {
            public string Name;
            public string Current;   // "-" when not installed
            public string Available; // "-" when unknown/unreachable
            // The live version is newer than this extension can use: its update is withheld until the
            // extension itself is updated (the CLI reports this as a 4th "blocked" column).
            public bool Blocked;
            public bool Installed => !string.IsNullOrEmpty(Current) && Current != "-";
            public bool AvailableKnown => !string.IsNullOrEmpty(Available) && Available != "-";
            public bool UpdateAvailable =>
                !Blocked && Installed && AvailableKnown &&
                !string.Equals(Norm(Current), Norm(Available), StringComparison.OrdinalIgnoreCase);
            private static string Norm(string v) => (v ?? string.Empty).Trim().TrimStart('v', 'V');
        }

        /// <summary>Run `Rxdk.Cli versions`, parse the tab-separated rows, and render them.</summary>
        private async System.Threading.Tasks.Task LoadComponentVersionsAsync()
        {
            var rows = new List<ComponentRow>();
            string output = null;
            // Pass the extension version as the ceiling so the CLI marks any component whose live
            // version is newer as "blocked" (update withheld until the extension is updated).
            try { output = await RunCliCaptureAsync($"versions --max-version {ExtensionInfo.GetVersion()}", timeoutMs: 30000); }
            catch { /* rendered as unavailable below */ }

            if (!string.IsNullOrEmpty(output))
            {
                foreach (var raw in output.Split('\n'))
                {
                    var parts = raw.TrimEnd('\r').Split('\t');
                    if (parts.Length >= 3)
                        rows.Add(new ComponentRow
                        {
                            Name = parts[0].Trim(),
                            Current = parts[1].Trim(),
                            Available = parts[2].Trim(),
                            Blocked = parts.Length >= 4 && parts[3].Trim() == "1",
                        });
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            RenderComponentRows(rows);
        }

        private void RenderComponentRows(List<ComponentRow> rows)
        {
            if (ComponentsPanel == null) return;
            ComponentsPanel.Children.Clear();

            // "Download RXDK Samples" is only useful before Samples exists — once installed, the
            // COMPONENTS row handles updates and "Open Samples Folder" is what's wanted. Hide the
            // download button when Samples is present (leave it visible if state is unknown).
            if (DownloadSamplesButton != null)
            {
                var samples = rows.FirstOrDefault(r => r.Name == "Samples");
                DownloadSamplesButton.Visibility = (samples != null && samples.Installed)
                    ? Visibility.Collapsed : Visibility.Visible;
            }

            // Hide "Install Prerequisites" once everything the installer (or a prior setup run) would
            // do is already present: the VS-side prerequisites (.NET 8, MSVC v143, the Xbox platform)
            // plus the core CLI components (SDK + host tools). Marketplace-installed users, who have
            // none of this yet, still see the button. Left visible if state is unknown (no rows).
            if (InstallPrereqsButton != null)
            {
                var sdk = rows.FirstOrDefault(r => r.Name == "SDK");
                var tools = rows.FirstOrDefault(r => r.Name == "Tools");
                var coreComponents = sdk != null && sdk.Installed && tools != null && tools.Installed;
                var allReady = coreComponents && RxdkCommands.VsSidePrerequisitesInstalled();
                InstallPrereqsButton.Visibility = allReady ? Visibility.Collapsed : Visibility.Visible;
            }

            if (rows.Count == 0)
            {
                ComponentsPanel.Children.Add(new TextBlock
                {
                    Style = (Style)FindResource("Muted"),
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Version info unavailable (is the RXDK engine installed?).",
                });
                if (UpdateAllButton != null) UpdateAllButton.IsEnabled = false;
                return;
            }

            var anyActionable = false;
            var muted = new SolidColorBrush(Color.FromArgb(0x99, 0x88, 0x88, 0x88));
            foreach (var r in rows)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

                // Blocked = the live version is newer than this extension can use, so no install/update
                // button is offered (the ceiling is the extension version).
                var actionable = !r.Blocked && (!r.Installed || r.UpdateAvailable);
                anyActionable |= actionable;
                if (actionable)
                {
                    var verb = ComponentVerbs.FirstOrDefault(c => c.Name == r.Name).Verb;
                    var btn = new Button
                    {
                        Style = (Style)FindResource("Act"),
                        Content = r.Installed ? "Update" : "Get",
                        Width = 72,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                        Tag = verb ?? "install-sdk",
                    };
                    btn.Click += OnUpdateComponent;
                    DockPanel.SetDock(btn, Dock.Right);
                    row.Children.Add(btn);
                }

                string status;
                if (r.Blocked)
                    status = $"{r.Available} available · update the RXDK extension first";
                else if (!r.Installed)
                    status = r.AvailableKnown ? $"not installed · latest {r.Available}" : "not installed";
                else if (r.UpdateAvailable)
                    status = $"{r.Current} → {r.Available}";
                else
                    status = $"{r.Current} · up to date";

                var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                tb.Inlines.Add(new Run(r.Name + "  ") { FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new Run(status) { Foreground = muted });
                row.Children.Add(tb);

                ComponentsPanel.Children.Add(row);
            }

            if (UpdateAllButton != null) UpdateAllButton.IsEnabled = anyActionable;
        }

        private void OnUpdateComponent(object sender, RoutedEventArgs e)
        {
            var verb = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(verb)) return;
            _ = RunComponentVerbsAsync(new[] { verb });
        }

        private void OnUpdateAll(object sender, RoutedEventArgs e)
        {
            // Only act on components that are not up to date (missing or with a newer version).
            var verbs = ComponentsPanel.Children.OfType<DockPanel>()
                .SelectMany(d => d.Children.OfType<Button>())
                .Select(b => b.Tag as string)
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct()
                .ToArray();
            if (verbs.Length == 0) return;
            _ = RunComponentVerbsAsync(verbs);
        }

        private void OnDownloadSamples(object sender, RoutedEventArgs e) =>
            _ = RunComponentVerbsAsync(new[] { "install-samples" });

        private void OnOpenSamplesFolder(object sender, RoutedEventArgs e)
        {
            var path = ToolLocator.StagedSamplesRoot;
            if (System.IO.Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                SetStatus("Samples not downloaded yet. Click \"Download RXDK Samples\" first.");
            }
        }

        /// <summary>Run one or more component install/update verbs (streamed to the RXDK output pane), then refresh.</summary>
        private async System.Threading.Tasks.Task RunComponentVerbsAsync(string[] verbs)
        {
            if (_package == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var cli = new CliRunner(_package);
            var failed = false;
            var gated = false;
            var maxVersion = ExtensionInfo.GetVersion();
            foreach (var verb in verbs)
            {
                SetStatus($"Running {verb}…");
                try
                {
                    // Pass the extension version so the engine refuses to pull a component newer than the
                    // extension can use. CliRunner returns the CLI's exit code; 3 = gated, other != 0 = failure.
                    var rc = await cli.RunAsync(new[] { verb, "--max-version", maxVersion }, Environment.CurrentDirectory);
                    if (rc == 3) { gated = true; SetStatus("Update the RXDK extension first."); break; }
                    if (rc != 0)
                    {
                        failed = true;
                        SetStatus($"{verb} failed — see the RXDK output window.");
                        break;
                    }
                }
                catch (Exception ex) { failed = true; SetStatus($"{verb} failed: {ex.Message}"); break; }
            }
            SetStatus("Refreshing versions…");
            await LoadComponentVersionsAsync();
            if (gated)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(_package,
                    "A newer RXDK component is available but needs a newer RXDK extension than the one " +
                    "loaded. Update the RXDK for Visual Studio extension first, then update the component. " +
                    "This keeps the extension, host tools, SDK, and docs on a compatible version.",
                    "RXDK", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                SetStatus("Ready.");
            }
            else if (failed)
            {
                SetStatus("Update failed — a tool may be in use. Restart Visual Studio and try again.");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(_package,
                    "An RXDK component update failed. If a host tool is in use (Visual Studio may " +
                    "have launched xbox-launch during a run/deploy), close and reopen Visual Studio, " +
                    "then run the update again. See the RXDK output window for details.",
                    "RXDK", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            else
            {
                SetStatus("Ready.");
            }
        }

        /// <summary>Run the CLI and capture stdout (for quick, non-streaming verbs like `versions`).</summary>
        private static async System.Threading.Tasks.Task<string> RunCliCaptureAsync(string verb, int timeoutMs)
        {
            var cliPath = ToolLocator.ResolveCli();
            if (cliPath == null) return null;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = verb,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                var output = await p.StandardOutput.ReadToEndAsync();
                p.WaitForExit(timeoutMs);
                return output;
            }
        }

        private void SetStatus(string text)
        {
            if (StatusText != null)
            {
                StatusText.Text = text;
            }
        }

        // ---- a tiny modal input box (VS ships none) ----

        /// <summary>
        /// Shows a minimal modal text-input dialog. Returns the entered string, or null if the
        /// user cancels. Used by the Set Xbox IP command.
        /// </summary>
        public static string PromptForString(string title, string prompt, string initial)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 380,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Manual,
            };

            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });

            var input = new TextBox { Text = initial ?? string.Empty };
            root.Children.Add(input);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var ok = new Button { Content = "OK", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", Width = 72, IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            dialog.Content = root;

            string result = null;
            ok.Click += (_, __) => { result = input.Text; dialog.DialogResult = true; };
            input.Focus();
            input.SelectAll();

            return dialog.ShowDialog() == true ? result : null;
        }

        /// <summary>
        /// Modal wizard for importing a VS2003 XDK project: pick the .vcproj, an output folder, and
        /// whether to copy the source files into it. Returns (vcprojPath, outputDir, copySources),
        /// or (null, null, false) if cancelled.
        /// </summary>
        public static (string vcproj, string outDir, bool copySources) PromptForImport()
        {
            var dialog = new Window
            {
                Title = "Import VS2003 XDK Project / Solution",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
            };
            var root = new StackPanel { Margin = new Thickness(12) };

            root.Children.Add(new TextBlock { Text = "VS2003 project (.vcproj) or solution (.sln):", Margin = new Thickness(0, 0, 0, 4) });
            var vcprojBox = new TextBox();
            var vcprojBrowse = new Button { Content = "Browse…", Width = 78, Margin = new Thickness(6, 0, 0, 0) };
            var row1 = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(vcprojBrowse, Dock.Right);
            row1.Children.Add(vcprojBrowse);
            row1.Children.Add(vcprojBox);
            root.Children.Add(row1);

            root.Children.Add(new TextBlock { Text = "Project root (the project is created in a child folder named after it):", Margin = new Thickness(0, 0, 0, 4) });
            var outBox = new TextBox();
            var outBrowse = new Button { Content = "Browse…", Width = 78, Margin = new Thickness(6, 0, 0, 0) };
            var row2 = new DockPanel();
            DockPanel.SetDock(outBrowse, Dock.Right);
            row2.Children.Add(outBrowse);
            row2.Children.Add(outBox);
            root.Children.Add(row2);

            var copyCheck = new CheckBox
            {
                Content = "Copy source files into the output folder (self-contained project)",
                Margin = new Thickness(0, 12, 0, 0),
            };
            root.Children.Add(copyCheck);

            root.Children.Add(new TextBlock
            {
                Text = "The RXDK project is created in <project root>\\<project name>. Sources are copied in " +
                       "unless that folder is the project's own folder (then it's an in-place import).",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            var ok = new Button { Content = "Import", Width = 78, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", Width = 78, IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            dialog.Content = root;

            vcprojBrowse.Click += (_, __) =>
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "VS2003 project or solution (*.vcproj;*.sln)|*.vcproj;*.sln|" +
                             "VS2003 project (*.vcproj)|*.vcproj|VS2003 solution (*.sln)|*.sln|All files (*.*)|*.*",
                    Title = "Select the VS2003 .vcproj or .sln",
                };
                if (ofd.ShowDialog() == true)
                {
                    vcprojBox.Text = ofd.FileName;
                    if (string.IsNullOrEmpty(outBox.Text))
                        outBox.Text = System.IO.Path.GetDirectoryName(ofd.FileName);
                }
            };
            outBrowse.Click += (_, __) =>
            {
                using (var fbd = new System.Windows.Forms.FolderBrowserDialog { Description = "Output folder for the RXDK project" })
                {
                    if (!string.IsNullOrEmpty(outBox.Text)) fbd.SelectedPath = outBox.Text;
                    if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK) outBox.Text = fbd.SelectedPath;
                }
            };

            bool okd = false;
            ok.Click += (_, __) =>
            {
                if (string.IsNullOrWhiteSpace(vcprojBox.Text) || string.IsNullOrWhiteSpace(outBox.Text))
                {
                    System.Windows.MessageBox.Show(dialog, "Pick both a .vcproj/.sln and a project root.", "RXDK");
                    return;
                }
                okd = true;
                dialog.DialogResult = true;
            };

            return dialog.ShowDialog() == true && okd
                ? (vcprojBox.Text.Trim(), outBox.Text.Trim(), copyCheck.IsChecked == true)
                : (null, null, false);
        }

        /// <summary>
        /// Runs `Rxdk.Cli.exe xbox-ip` and returns the resolved devkit address, or null. Kept
        /// local to the control so the IP label refreshes without touching the command service.
        /// </summary>
        private static async System.Threading.Tasks.Task<string> ProbeIpAsync()
        {
            var cliPath = Services.ToolLocator.ResolveCli();
            if (cliPath == null)
            {
                return null;
            }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "xbox-ip",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                var output = await p.StandardOutput.ReadToEndAsync();
                p.WaitForExit(5000);
                var line = output.Trim();
                if (p.ExitCode != 0 || line.StartsWith("no Xbox", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return line;
            }
        }
    }
}
