using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RxdkVs.Package.Services;
using RxdkVs.Package.ToolWindow;
using Task = System.Threading.Tasks.Task;

namespace RxdkVs.Package.Commands
{
    /// <summary>
    /// Binds every RXDK command (from Commands/CommandIds.cs, declared in RxdkPackage.vsct) to a
    /// handler on the OleMenuCommandService, and implements the handlers. Build/Deploy/Run/Reboot
    /// shell out to Rxdk.Cli.exe via <see cref="CliRunner"/>; folder/doc commands open Explorer or
    /// a browser; Debug delegates to the VS debugger (which routes the "xbox" launch config to the
    /// Debug Adapter Host → Rxdk.Dap.exe).
    ///
    /// This is the C# analog of RXDK-VSCode's extension.ts command registration.
    /// </summary>
    internal sealed class RxdkCommands
    {
        private readonly RxdkPackage _package;
        private readonly CliRunner _cli;

        private RxdkCommands(RxdkPackage package)
        {
            _package = package;
            _cli = new CliRunner(package);
        }

        public static async Task InitializeAsync(RxdkPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var instance = new RxdkCommands(package);

            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            if (commandService == null)
            {
                return;
            }
            instance.RegisterAll(commandService);
        }

        private void RegisterAll(OleMenuCommandService svc)
        {
            void Add(int id, Func<Task> handler, EventHandler beforeQueryStatus = null)
            {
                var cmdId = new CommandID(RxdkPackageGuids.CommandSet, id);
                var cmd = new OleMenuCommand((s, e) => _package.JoinableTaskFactory.RunAsync(handler).FileAndForget("rxdk/command"), cmdId);
                if (beforeQueryStatus != null) cmd.BeforeQueryStatus += beforeQueryStatus;
                svc.AddCommand(cmd);
            }

            // Context-menu visibility: Deploy for any RXDK Xbox project; Remove DXT only for DXTs.
            void OnQueryDeploy(object s, EventArgs e)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ((OleMenuCommand)s).Visible = Services.XboxDebugLauncher.TryGetSelectedProject(out var sel) && sel.IsXbox;
            }
            void OnQueryRemoveDxt(object s, EventArgs e)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ((OleMenuCommand)s).Visible = Services.XboxDebugLauncher.TryGetSelectedProject(out var sel) && sel.IsXbox && sel.IsDxt;
            }
            // "Launch in xemu" is offered for an RXDK Xbox project once an xemu path is set in Options.
            void OnQueryXemu(object s, EventArgs e)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var page = (Options.RxdkOptionsPage)_package.GetDialogPage(typeof(Options.RxdkOptionsPage));
                var configured = !string.IsNullOrWhiteSpace(page.XemuPath);
                ((OleMenuCommand)s).Visible = configured
                    && Services.XboxDebugLauncher.TryGetSelectedProject(out var sel) && sel.IsXbox;
            }

            Add(CommandIds.CmdBuild, () => RunCliAsync("build"));
            Add(CommandIds.CmdDeploy, () => RunCliAsync("deploy"));
            Add(CommandIds.CmdRun, () => RunCliAsync("run"));
            Add(CommandIds.CmdLaunchXemu, LaunchXemuAsync, OnQueryXemu);
            Add(CommandIds.CmdRebootConsole, () => RunCliAsync("reboot", requiresProject: false));
            Add(CommandIds.CmdRemoveDxt, RemoveDxtAsync, OnQueryRemoveDxt);
            Add(CommandIds.CmdDeployProject, DeployProjectAsync, OnQueryDeploy);
            Add(CommandIds.CmdSetXboxIp, SetXboxIpAsync);
            Add(CommandIds.CmdDebug, DebugAsync);
            Add(CommandIds.CmdNewProject, NewProjectAsync);
            Add(CommandIds.CmdImportProject, ImportProjectAsync);
            Add(CommandIds.CmdShowToolWindow, ShowToolWindowAsync);
            Add(CommandIds.CmdOpenSdkFolder, () => OpenFolderAsync(ToolLocator.StagedSdkRoot));
            Add(CommandIds.CmdOpenToolsFolder, () => OpenFolderAsync(ToolLocator.StagedToolsRoot));
            Add(CommandIds.CmdOpenDocsFolder, () => OpenFolderAsync(ToolLocator.StagedDocsRoot));
            Add(CommandIds.CmdOpenSdkDocs, () => OpenDocsAsync("sdk"));
            Add(CommandIds.CmdOpenExtensionDocs, () => OpenDocsAsync("rxdk-vs"));
            Add(CommandIds.CmdFetchLatestSdk, () => RunCliAsync("install-sdk", requiresProject: false, "--max-version", Services.ExtensionInfo.GetVersion()));
            Add(CommandIds.CmdInstallDotNet, EnsureDotNet8Async);
            Add(CommandIds.CmdInstallBuildTools, InstallBuildToolsAsync);
            Add(CommandIds.CmdInstallXboxPlatform, InstallXboxPlatformAsync);
            Add(CommandIds.CmdLaunchXbwatson, () => LaunchHostToolAsync("xbwatson"));
            Add(CommandIds.CmdLaunchXbNeighborhood, () => LaunchHostToolAsync("xbNeighborhood"));
            Add(CommandIds.CmdOpenXboxNeighborhood, OpenXboxNeighborhoodAsync);
            Add(CommandIds.CmdInstallXboxNeighborhood, InstallXboxNeighborhoodAsync);
            Add(CommandIds.CmdCycleGlobalsScope, CycleGlobalsScopeAsync);
            Add(CommandIds.CmdSetBuildType, SetBuildTypeAsync);
            Add(CommandIds.CmdSetupPrerequisites, SetupPrerequisitesAsync);
            Add(CommandIds.CmdOpenSettings, OpenSettingsAsync);
        }

        // ---- CLI-backed commands ----

        private async Task RunCliAsync(string verb, bool requiresProject = true, params string[] extraArgs)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var args = new List<string> { verb };
            string projectRoot = null;

            if (requiresProject)
            {
                projectRoot = await OpenFolderContext.ResolveProjectRootAsync(_package);
                if (projectRoot == null)
                {
                    await ShowInfoAsync("No RXDK project selected. Set the Xbox project as the startup project (or open one of its files), then try again.");
                    return;
                }
                args.Add("--project-root");
                args.Add(projectRoot);
            }
            args.AddRange(extraArgs);

            try
            {
                await _cli.RunAsync(args, projectRoot ?? Environment.CurrentDirectory);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"RXDK {verb} failed: {ex.Message}");
            }
        }

        // Build the project and boot the resulting ISO in xemu (no debugging). Routes through the
        // CLI so xemu's serial console output streams into the RXDK output pane.
        private async Task LaunchXemuAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var page = (Options.RxdkOptionsPage)_package.GetDialogPage(typeof(Options.RxdkOptionsPage));
            if (string.IsNullOrWhiteSpace(page.XemuPath))
            {
                await ShowInfoAsync("Set the xemu path first: Tools > Options > RXDK > General.");
                return;
            }
            await RunCliAsync("launch-xemu", requiresProject: true,
                "--xemu-path", page.XemuPath, "--xemu-params", page.XemuParams ?? "");
        }

        // Deploy the selected project's .xbe + media (retry path when the devkit was off at F5).
        // Wired to the Solution Explorer project context menu (RXDK Xbox projects only).
        private Task DeployProjectAsync() => XboxDebugLauncher.DeploySelectedAsync(_package, _cli);

        // Remove the selected DXT project's extension from xe:\dxt and warm-reboot. Context-menu
        // command shown only for RXDK DXT projects, so it targets that specific project by name.
        private async Task RemoveDxtAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!XboxDebugLauncher.TryGetSelectedProject(out var sel) || !sel.IsDxt)
            {
                await ShowInfoAsync("Select an RXDK DXT project in Solution Explorer, then try Remove DXT again.");
                return;
            }
            var args = new[] { "remove-dxt", "--project-root", sel.Dir, "--name", sel.Name };
            if (await _cli.RunAsync(args, sel.Dir) != 0)
            {
                await ShowErrorAsync($"Remove DXT failed — is the devkit on, and was {sel.Name}.dxt deployed?");
                return;
            }
            await _cli.RunAsync(new[] { "reboot" }, sel.Dir);
            await ShowInfoAsync($"Removed {sel.Name}.dxt from the console and warm-rebooted.");
        }

        private async Task SetXboxIpAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var current = await GetXboxIpAsync();
            var input = PromptForString("Set Xbox IP / Hostname", "Enter the devkit IP address or hostname:", current ?? string.Empty);
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }
            await _cli.RunAsync(new[] { "set-ip", "--address", input.Trim() }, Environment.CurrentDirectory);
        }

        // ---- Debug (F5 → Debug Adapter Host → Rxdk.Dap.exe) ----

        private Task DebugAsync()
        {
            // Same path as F5 / the green Run button: build + deploy the startup Xbox project,
            // then launch the Xbox debug adapter via the Debug Adapter Host. Reads the output
            // from the .vcxproj (NMakeOutput), not rxdk.project.json.
            return XboxDebugLauncher.LaunchAsync(_package, _cli);
        }

        /// <summary>Read the "name" field from rxdk.project.json (folder name as a fallback).</summary>
        private static string ReadProjectName(string projectRoot)
        {
            try
            {
                var json = File.ReadAllText(Path.Combine(projectRoot, "rxdk.project.json"));
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                    return n.GetString();
            }
            catch { /* fall through */ }
            return Path.GetFileName(projectRoot.TrimEnd('\\', '/'));
        }

        // ---- Project scaffolding ----

        private async Task NewProjectAsync()
        {
            // Open VS's standard New Project dialog; the RXDK templates (Original Xbox Game/Empty/
            // Lib/DXT/Video Player/Cube/…) are contributed via the VSIX and filterable by the Xbox tag.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (EnvDTE.DTE)await _package.GetServiceAsync(typeof(EnvDTE.DTE));
            try
            {
                dte?.ExecuteCommand("File.NewProject");
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"Could not open New Project: {ex.Message}");
            }
        }

        // ---- VS2003 project import ----

        private async Task ImportProjectAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var (vcproj, projectRoot, copySources) = RxdkToolWindowControl.PromptForImport();
            if (string.IsNullOrEmpty(vcproj) || string.IsNullOrEmpty(projectRoot))
            {
                return; // cancelled
            }

            // The import lands in <project root>\<project name> -- a child of the chosen root. Copy the
            // sources in unless that child folder is the project's own folder (then it's an in-place
            // import and paths reference the originals). Keeps manifest source paths relative either way.
            var projectName = Path.GetFileNameWithoutExtension(vcproj);
            var outDir = Path.Combine(projectRoot, projectName);
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(vcproj)) ?? projectRoot;
            if (!string.Equals(Path.GetFullPath(outDir).TrimEnd('\\'), sourceDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                copySources = true;

            // No scaffold to copy: the RXDK MSBuild integration lives in the installed "Xbox"
            // platform (imported via Platform=Xbox), so imported projects need no per-project
            // props/targets/rule files. (Run "Install Xbox Platform" once if it isn't installed.)

            // Create the output folder up front: the CLI runs with outDir as its working directory,
            // so it must exist before the process can even start (else "directory name is invalid").
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"Could not create the output folder:\n{outDir}\n\n{ex.Message}");
                return;
            }

            // A .sln imports the whole multi-project graph (import-sln); a .vcproj imports one project.
            var isSolution = vcproj.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
            var verb = isSolution ? "import-sln" : "import-vcproj";
            var argList = new List<string> { verb, "--in", vcproj, "--out", outDir };
            if (copySources) argList.Add("--copy-sources");
            var args = argList.ToArray();
            int rc;
            try
            {
                rc = await _cli.RunAsync(args, outDir);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"Import failed: {ex.Message}");
                return;
            }
            if (rc != 0)
            {
                await ShowErrorAsync("Import failed — see the RXDK output window for details.");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (EnvDTE.DTE)await _package.GetServiceAsync(typeof(EnvDTE.DTE));

            if (isSolution)
            {
                // A generated .sln ties the imported projects together; open it in VS.
                var producedSln = Directory.GetFiles(outDir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (producedSln != null && dte != null)
                {
                    // Opening a solution replaces the current one, so only prompt if one is open.
                    var hasOpen = dte.Solution != null && dte.Solution.IsOpen;
                    var go = !hasOpen ? (int)VSConstants.MessageBoxResult.IDYES : VsShellUtilities.ShowMessageBox(_package,
                        $"Imported the solution to:\n{outDir}\n\nOpen {Path.GetFileName(producedSln)} now? This closes the current solution.", "RXDK",
                        OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    if (go == (int)VSConstants.MessageBoxResult.IDYES)
                    {
                        try { dte.Solution.Open(producedSln); }
                        catch (Exception ex) { await ShowErrorAsync($"Imported OK but could not open the solution: {ex.Message}"); }
                    }
                    return;
                }
                await ShowInfoAsync($"Imported the solution to {outDir}.");
                return;
            }

            // Single project: the importer writes exactly one .vcxproj at the output root.
            var proj = Directory.GetFiles(outDir, "*.vcxproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (proj != null && dte != null)
            {
                var solution = dte.Solution;
                if (solution != null && solution.IsOpen)
                {
                    var add = VsShellUtilities.ShowMessageBox(_package,
                        $"Imported {Path.GetFileName(proj)}.\n\nAdd it to the current solution?", "RXDK",
                        OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    if (add == (int)VSConstants.MessageBoxResult.IDYES)
                    {
                        try { solution.AddFromFile(proj, false); }
                        catch (Exception ex) { await ShowErrorAsync($"Imported OK but could not add to the solution: {ex.Message}"); }
                    }
                    return;
                }
                // No solution open: open the project in VS (VS creates an implicit solution for it).
                try { dte.ExecuteCommand("File.OpenProject", $"\"{proj}\""); }
                catch (Exception ex) { await ShowErrorAsync($"Imported OK but could not open the project: {ex.Message}"); }
                return;
            }

            // VS automation unavailable — last-resort reveal in Explorer so the import isn't lost.
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{outDir}\"") { UseShellExecute = true }); }
            catch { /* best effort */ }
        }

        // ---- Tool window ----

        private async Task ShowToolWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await _package.ShowToolWindowAsync(typeof(RxdkToolWindow), 0, create: true, cancellationToken: _package.DisposalToken);
            if (window?.Frame == null)
            {
                await ShowErrorAsync("Could not create the RXDK tool window.");
            }
        }

        // ---- Folder / docs / launchers ----

        private async Task OpenFolderAsync(string path)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                await ShowInfoAsync($"Folder does not exist yet: {path}\nRun Install Prerequisites (RXDK window) first.");
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }

        private async Task OpenDocsAsync(string which)
        {
            // "sdk" -> the Xbox SDK help set (cloned under docs\xboxsdk), "rxdk" -> the extension
            // docs (docs\rxdk). The RXDK-Docs pages are .htm with a toc.json, and the SDK set has
            // no index page, so resolve the landing page rather than assuming docs\<x>\index.html.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var candidates = which == "sdk" ? new[] { "xboxsdk", "sdk" } : new[] { which };
            string landing = null;
            string tried = null;
            foreach (var folder in candidates)
            {
                tried = Path.Combine(ToolLocator.StagedDocsRoot, folder);
                landing = ResolveDocsLanding(tried);
                if (landing != null) break;
            }
            if (landing != null)
            {
                Process.Start(new ProcessStartInfo(landing) { UseShellExecute = true });
            }
            else
            {
                await ShowInfoAsync($"Documentation not found under {tried}.\nRun Install Prerequisites (RXDK window) to clone RXDK-Docs.");
            }
        }

        /// <summary>
        /// Resolves the landing page for a docs folder: an index.htm/html if present, otherwise the
        /// first "page" referenced by the folder's toc.json (the SDK help set has no index page).
        /// Returns null if the folder is missing or no page can be found.
        /// </summary>
        private static string ResolveDocsLanding(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return null;
            }
            // Prefer the toc.json's declared landing page ("defaultPage"), then its first page.
            var toc = Path.Combine(dir, "toc.json");
            if (File.Exists(toc))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(toc));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("defaultPage", out var dp) && dp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var p = Path.Combine(dir, dp.GetString());
                        if (File.Exists(p)) return p;
                    }
                    var page = FindFirstTocPage(root);
                    if (!string.IsNullOrEmpty(page))
                    {
                        var p = Path.Combine(dir, page);
                        if (File.Exists(p)) return p;
                    }
                }
                catch { /* malformed toc — fall through */ }
            }
            foreach (var name in new[] { "index.htm", "index.html", "default.htm", "default.html" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>Depth-first search for the first "page" string in a toc.json tree.</summary>
        private static string FindFirstTocPage(System.Text.Json.JsonElement el)
        {
            switch (el.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    if (el.TryGetProperty("page", out var pg) && pg.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = pg.GetString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                    foreach (var prop in el.EnumerateObject())
                    {
                        var r = FindFirstTocPage(prop.Value);
                        if (r != null) return r;
                    }
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        var r = FindFirstTocPage(item);
                        if (r != null) return r;
                    }
                    break;
            }
            return null;
        }

        private async Task LaunchHostToolAsync(string tool)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var exe = Path.Combine(ToolLocator.StagedToolsRoot, tool + ".exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = ToolLocator.StagedToolsRoot });
            }
            else
            {
                await ShowInfoAsync($"{tool} not found at {exe}. Run Install Prerequisites (RXDK window) to download host tools.");
            }
        }

        // The Xbox Neighborhood shell namespace extension ({DB15FEDD-...}) registers as a child of
        // This PC ({20D04FE0-...}), so it must be opened by that nested shell path — a bare
        // "shell:::{XboxNeighborhood}" placeholder is not a real CLSID and Explorer can't resolve it.
        private const string XboxNeighborhoodShellPath =
            @"shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\::{DB15FEDD-96B8-4DA9-97E0-7E5CCA05CC44}";

        private async Task OpenXboxNeighborhoodAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // Windows-only Xbox Neighborhood shell folder (matches rxdk.openXboxNeighborhood).
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", XboxNeighborhoodShellPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await ShowInfoAsync($"Could not open Xbox Neighborhood: {ex.Message}");
            }
        }

        // RXDK-Tools GitHub repo + the installer asset that registers the Xbox Neighborhood
        // Explorer shell namespace extension (Rxdk.XbShellExt.Shell.dll). Mirrors RXDK-VSCode's
        // installXboxNeighborhood (xboxNeighborhoodShell.ts).
        private const string RxdkToolsRepo = "Team-Resurgent/RXDK-Tools";
        private const string XboxNeighborhoodSetupAsset = "XboxNeighborhood-Setup.exe";

        // Download XboxNeighborhood-Setup.exe from the latest RXDK-Tools release and launch it
        // interactively. The setup registers the Explorer shell namespace extension (needs
        // elevation), so it self-elevates; "Open Xbox Neighborhood" becomes usable once the user
        // finishes and refreshes. Windows-only (it's a Windows Explorer integration).
        private async Task InstallXboxNeighborhoodAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                await ShowErrorAsync("Xbox Neighborhood is Windows-only.");
                return;
            }

            string dest;
            try
            {
                await _cli.LogAsync($"[RXDK] Resolving {XboxNeighborhoodSetupAsset} from {RxdkToolsRepo}…");
                // Run the network work off the UI thread (WebClient continuations stay off it too).
                dest = await Task.Run(() => DownloadXboxNeighborhoodSetupAsync());
                await _cli.LogAsync($"[RXDK] Downloaded the Xbox Neighborhood installer to {dest}");
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"Could not download the Xbox Neighborhood installer: {ex.Message}");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                // UseShellExecute so the setup can self-elevate (UAC). Don't wait — it's interactive.
                Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"Downloaded the installer but could not launch it: {ex.Message}");
                return;
            }

            await _cli.LogAsync("[RXDK] Launched the Xbox Neighborhood installer.");
            await ShowInfoAsync(
                "The Xbox Neighborhood installer has launched. Finish the setup (it will elevate), " +
                "then click Refresh in the RXDK window — \"Open Xbox Neighborhood\" becomes available " +
                "once the shell extension is registered.");
        }

        // Resolve the XboxNeighborhood-Setup.exe asset on the latest RXDK-Tools release and download
        // it to a temp file. Talks to the GitHub Releases API the same way the engine's GitHubReleases
        // resolver does (User-Agent + versioned Accept header, honoring GITHUB_TOKEN/GH_TOKEN).
        private static async Task<string> DownloadXboxNeighborhoodSetupAsync()
        {
            // .NET Framework 4.7+ negotiates TLS 1.2/1.3 by default (SystemDefault), so we don't
            // touch the process-wide ServicePointManager.SecurityProtocol (banned here — it would
            // change Visual Studio's own networking state).
            var apiUrl = $"https://api.github.com/repos/{RxdkToolsRepo}/releases/latest";
            string releaseJson;
            using (var client = CreateGitHubWebClient())
            {
                releaseJson = await client.DownloadStringTaskAsync(apiUrl);
            }

            string downloadUrl = null;
            using (var doc = System.Text.Json.JsonDocument.Parse(releaseJson))
            {
                if (doc.RootElement.TryGetProperty("assets", out var assets) &&
                    assets.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        if (a.TryGetProperty("name", out var n) &&
                            string.Equals(n.GetString(), XboxNeighborhoodSetupAsset, StringComparison.Ordinal) &&
                            a.TryGetProperty("browser_download_url", out var u))
                        {
                            downloadUrl = u.GetString();
                            break;
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(downloadUrl))
            {
                throw new InvalidOperationException(
                    $"the latest {RxdkToolsRepo} release has no asset \"{XboxNeighborhoodSetupAsset}\"");
            }

            var dest = Path.Combine(Path.GetTempPath(), $"XboxNeighborhood-Setup-{DateTime.UtcNow:yyyyMMddHHmmss}.exe");
            using (var client = CreateGitHubWebClient())
            {
                await client.DownloadFileTaskAsync(downloadUrl, dest);
            }
            return dest;
        }

        // A WebClient carrying the headers GitHub's REST API and release-asset CDN expect.
        private static System.Net.WebClient CreateGitHubWebClient()
        {
            var client = new System.Net.WebClient();
            client.Headers.Add("User-Agent", "RXDK-VS20XX");
            client.Headers.Add("Accept", "application/vnd.github+json");
            client.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                        ?? Environment.GetEnvironmentVariable("GH_TOKEN");
            if (!string.IsNullOrEmpty(token))
            {
                client.Headers.Add("Authorization", "Bearer " + token);
            }
            return client;
        }

        // ---- Runtime / prerequisites / settings ----

        // Per-user managed .NET root (mirrors RXDK-VSCode's ~/.dotnet). CliRunner.InjectDotnetRoot
        // hands this to the spawned engine as DOTNET_ROOT when it carries a net8 runtime.
        private static string ManagedDotnetRoot() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");

        // The RXDK engine is framework-dependent .NET 8. Command-menu entry: ensure it's present,
        // auto-installing to ~/.dotnet when missing, and report the outcome.
        private async Task EnsureDotNet8Async()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (HasDotNet8())
            {
                await ShowInfoAsync("The .NET 8 runtime is present.");
                return;
            }
            var ok = await InstallDotNet8Async();
            if (ok)
                await ShowInfoAsync($"The .NET 8 runtime was installed to {ManagedDotnetRoot()}.");
            else
                await ShowErrorAsync(
                    "Automatic .NET 8 install failed (see the RXDK output pane). Install the .NET 8 " +
                    "Desktop Runtime manually from https://dotnet.microsoft.com/download/dotnet/8.0.");
        }

        // Download + run Microsoft's official dotnet-install script to drop a .NET 8 runtime under
        // ~/.dotnet (per-user, no elevation). Must NOT go through the CLI (that's what needs .NET);
        // runs powershell in-process and streams to the RXDK pane. Returns true if net8 is present after.
        private async Task<bool> InstallDotNet8Async()
        {
            if (HasDotNet8()) return true;
            var dir = ManagedDotnetRoot();
            await _cli.LogAsync($"[RXDK] .NET 8 runtime not found — installing to {dir} (this can take a minute)...");
            // Canonical one-liner: fetch dot.net/v1/dotnet-install.ps1 and run it for the net8 runtime.
            var psScript =
                "$ErrorActionPreference='Stop';" +
                "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;" +
                "& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1').Content)) " +
                $"-Runtime dotnet -Channel 8.0 -InstallDir '{dir}'";
            var args = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"";
            try
            {
                await _cli.RunProcessAsync("powershell.exe", args, Environment.CurrentDirectory);
            }
            catch (Exception ex)
            {
                await _cli.LogAsync($"[RXDK] .NET install error: {ex.Message}");
                return false;
            }
            var ok = HasDotNet8();
            await _cli.LogAsync(ok
                ? "[RXDK] .NET 8 runtime ready."
                : "[RXDK] .NET 8 runtime still not detected after install.");
            return ok;
        }

        // True when a .NET 8 shared runtime is discoverable where the framework-dependent apphost
        // probes: DOTNET_ROOT, the default Program Files\dotnet, or the per-user %USERPROFILE%\.dotnet.
        private static bool HasDotNet8()
        {
            var roots = new List<string>();
            var dnr = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dnr)) roots.Add(dnr);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
            foreach (var r in roots)
            {
                var shared = Path.Combine(r, "shared", "Microsoft.NETCore.App");
                try
                {
                    if (Directory.Exists(shared) &&
                        Directory.GetDirectories(shared).Any(d => Path.GetFileName(d).StartsWith("8.", StringComparison.Ordinal)))
                        return true;
                }
                catch { /* ignore and try the next root */ }
            }
            return false;
        }

        // MSVC v143 C++ build tools component (VS 2022/2026). The RXDK native .vcxproj project
        // system needs a C++ toolset installed to load projects and drive IntelliSense, even
        // though the actual compile is delegated to Zig/clang.
        private const string Vc143Component = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";

        // Install the custom 'Xbox' MSBuild platform into every VS install's VCTargetsPath so RXDK
        // .vcxproj projects (Platform=Xbox) load and build. The platform is a thin alias to x64
        // (RXDK titles are Makefile projects built by Rxdk.Cli; x64's toolset only drives
        // IntelliSense). Writing under Program Files needs elevation, so the copy runs via a
        // one-shot elevated PowerShell (UAC) — nothing is changed silently.
        private async Task InstallXboxPlatformAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var vsixDir = Path.GetDirectoryName(typeof(RxdkCommands).Assembly.Location);
                var src = Path.Combine(vsixDir ?? "", "VcPlatform", "Platforms", "Xbox");
                if (!Directory.Exists(src))
                {
                    await ShowErrorAsync($"Xbox platform files not found in the extension ({src}). Reinstall the RXDK extension.");
                    return;
                }

                var dests = FindXboxPlatformDests();
                if (dests.Count == 0)
                {
                    await ShowInfoAsync("No Visual Studio C++ targets were found. Install the \"Desktop development with C++\" workload (Install C++ Build Tools), then try again.");
                    return;
                }

                var list = string.Join("\n", dests.Select(d => "  • " + d));
                var go = VsShellUtilities.ShowMessageBox(_package,
                    "This installs (or updates) the RXDK 'Xbox' build platform in Visual Studio so Xbox " +
                    "projects load and build:\n\n" + list + "\n\nYou'll be asked to elevate (UAC). Continue?",
                    "RXDK", OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                if (go != (int)VSConstants.MessageBoxResult.IDYES) return;

                // Build a one-shot elevated script that robocopies the alias into each dest, then
                // writes a version stamp so a later run can tell a current platform from a stale one.
                var version = GetExtensionVersion();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("$ErrorActionPreference='Continue'");
                foreach (var d in dests)
                {
                    sb.AppendLine($"New-Item -ItemType Directory -Force -Path \"{d}\" | Out-Null");
                    sb.AppendLine($"robocopy \"{src}\" \"{d}\" /E /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null");
                    sb.AppendLine($"Set-Content -Path \"{Path.Combine(d, "RxdkPlatform.version")}\" -Value \"{version}\" -NoNewline -Encoding ascii");
                }
                var script = Path.Combine(Path.GetTempPath(), "rxdk-install-xbox-platform.ps1");
                File.WriteAllText(script, sb.ToString());

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                        UseShellExecute = true,
                        Verb = "runas", // triggers the UAC elevation prompt
                    };
                    using (var p = Process.Start(psi))
                    {
                        await System.Threading.Tasks.Task.Run(() => p.WaitForExit());
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    await ShowInfoAsync("Elevation was cancelled — the Xbox platform was not installed.");
                    return;
                }

                var ok = dests.Any(d => File.Exists(Path.Combine(d, "Platform.props")));
                if (ok)
                    await ShowInfoAsync("The RXDK 'Xbox' platform is installed. Reload your solution (or restart Visual Studio) and Xbox projects will build.");
                else
                    await ShowErrorAsync("The Xbox platform copy did not complete. See if elevation was declined, then try again.");
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"Could not install the Xbox platform: {ex.Message}");
            }
        }

        // Every VS install's VCTargetsPath\Platforms\Xbox destination (dirs that ship an x64
        // platform, which the Xbox alias imports). Uses vswhere to find all instances.
        private static List<string> FindXboxPlatformDests()
        {
            var dests = new List<string>();
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vswhere = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) return dests;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-all -prerelease -property installationPath",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                string outp;
                using (var p = Process.Start(psi)) { outp = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000); }
                foreach (var line in outp.Split('\n'))
                {
                    var install = line.Trim();
                    if (install.Length == 0) continue;
                    var vcRoot = Path.Combine(install, "MSBuild", "Microsoft", "VC");
                    if (!Directory.Exists(vcRoot)) continue;
                    foreach (var vc in Directory.GetDirectories(vcRoot, "v1*"))
                    {
                        if (Directory.Exists(Path.Combine(vc, "Platforms", "x64")))
                            dests.Add(Path.Combine(vc, "Platforms", "Xbox"));
                    }
                }
            }
            catch { /* return whatever we found */ }
            return dests;
        }

        // Launch the Visual Studio Installer to add the C++ v143 build tools to the running VS.
        // We don't install silently — the Installer's own UI (and the UAC prompt) let the user
        // review and approve the change.
        private async Task InstallBuildToolsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var installerDir = Path.Combine(pf86, "Microsoft Visual Studio", "Installer");
                var vsInstaller = Path.Combine(installerDir, "vs_installer.exe");
                var vswhere = Path.Combine(installerDir, "vswhere.exe");
                if (!File.Exists(vsInstaller))
                {
                    await ShowInfoAsync(
                        "Could not find the Visual Studio Installer. Open it from the Start menu, click " +
                        "Modify on your Visual Studio, and add the \"Desktop development with C++\" workload " +
                        "(MSVC v143 build tools).");
                    return;
                }

                // Resolve the running VS install path so we modify the right instance.
                string installPath = null;
                if (File.Exists(vswhere))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vswhere,
                        Arguments = "-latest -property installationPath",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        installPath = (await p.StandardOutput.ReadToEndAsync()).Trim();
                        p.WaitForExit(10000);
                    }

                    // Already installed? vswhere returns a path only when the component is present.
                    var checkPsi = new ProcessStartInfo
                    {
                        FileName = vswhere,
                        Arguments = $"-latest -requires {Vc143Component} -property installationPath",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    using (var p = Process.Start(checkPsi))
                    {
                        var has = (await p.StandardOutput.ReadToEndAsync()).Trim();
                        p.WaitForExit(10000);
                        if (!string.IsNullOrEmpty(has))
                        {
                            await ShowInfoAsync("The MSVC v143 C++ build tools are already installed.");
                            return;
                        }
                    }
                }

                var go = VsShellUtilities.ShowMessageBox(_package,
                    "This opens the Visual Studio Installer to add the C++ build tools (MSVC v143)" +
                    (string.IsNullOrEmpty(installPath) ? "." : $" to:\n{installPath}") +
                    "\n\nYou'll be asked to elevate, and Visual Studio may need to close during the " +
                    "install. Continue?",
                    "RXDK", OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                if (go != (int)VSConstants.MessageBoxResult.IDYES) return;

                var args = "modify --add " + Vc143Component + " --norestart";
                if (!string.IsNullOrEmpty(installPath))
                    args = $"modify --installPath \"{installPath}\" --add {Vc143Component} --norestart";

                // UseShellExecute so the Installer can elevate (UAC). Its UI shows the summary and
                // the user clicks Modify to apply.
                Process.Start(new ProcessStartInfo(vsInstaller, args) { UseShellExecute = true });
                await ShowInfoAsync(
                    "The Visual Studio Installer is opening to add the C++ v143 build tools. Follow its " +
                    "prompts, then restart Visual Studio.");
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"Could not launch the Visual Studio Installer: {ex.Message}");
            }
        }

        // One-click setup: installs everything RXDK needs, skipping whatever is already present, so
        // it's cheap to re-run. Covers the VS-side prerequisites (C++ build tools + the Xbox
        // platform) and the CLI-managed components (Zig, host tools, SDK, docs) in one action.
        private async Task SetupPrerequisitesAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var cwd = Environment.CurrentDirectory;

            // 0) .NET 8 runtime — the RXDK engine (Rxdk.Cli/Rxdk.Dap) is framework-dependent, so
            //    everything below (which runs the CLI) needs it. VS 2022 17.8+ ships it; when it's
            //    genuinely missing, auto-install a per-user copy to ~/.dotnet (CliRunner then points
            //    the engine's DOTNET_ROOT there). If that fails, guide and stop.
            if (!await InstallDotNet8Async())
            {
                await ShowErrorAsync(
                    "The .NET 8 runtime is required (the RXDK engine runs on it) and the automatic " +
                    "install failed — see the RXDK output pane. Update Visual Studio to 17.8+ (it " +
                    "includes .NET 8), or install the .NET 8 Desktop Runtime from " +
                    "https://dotnet.microsoft.com/download/dotnet/8.0, then re-run setup.");
                return;
            }

            // 1) MSVC v143 C++ build tools — the VC project system needs them to load/build .vcxproj
            //    and to host IntelliSense (the compile itself is Zig/clang). Opens the VS Installer
            //    when missing; that's an external, interactive step, so if we kick it off the Xbox
            //    platform install below is skipped this run (re-run setup after VS restarts).
            var buildToolsPending = false;
            if (!HasVc143())
            {
                await InstallBuildToolsAsync();
                buildToolsPending = true;
            }

            // 2) The custom 'Xbox' MSBuild platform (copied into VCTargetsPath\Platforms\Xbox). Needs
            //    the x64 platform (from the C++ tools) present, so only attempt it once those exist.
            var platformInstalled = false;
            if (!IsXboxPlatformCurrent())
            {
                if (buildToolsPending)
                {
                    // can't install into VCTargetsPath until the C++ tools finish installing
                }
                else
                {
                    await InstallXboxPlatformAsync();
                    platformInstalled = IsXboxPlatformCurrent();
                }
            }

            // 3) CLI-managed components: install only what's missing (won't re-fetch Zig etc.).
            //    Pass the extension version so a component whose live version is newer than this
            //    extension can use is withheld (CLI exit code 3) rather than pulled ahead.
            var installed = 0;
            var gatedAny = false;
            var maxVersion = ExtensionInfo.GetVersion();
            async Task EnsureAsync(string statusVerb, string installVerb)
            {
                if (await _cli.RunAsync(new[] { statusVerb }, cwd) != 0)
                {
                    var rc = await _cli.RunAsync(new[] { installVerb, "--max-version", maxVersion }, cwd);
                    if (rc == 3) gatedAny = true;
                    else if (rc == 0) installed++;
                }
            }
            await EnsureAsync("zig-status", "install-zig");
            await EnsureAsync("tools-status", "install-tools");
            await EnsureAsync("sdk-status", "install-sdk");
            await EnsureAsync("docs-status", "install-docs");

            // Single summary rather than a dialog per step.
            if (buildToolsPending)
            {
                await ShowInfoAsync(
                    "Finish the C++ build tools install in the Visual Studio Installer, restart Visual " +
                    "Studio, then click Install Prerequisites again to install the Xbox platform and " +
                    "any remaining components.");
            }
            else if (gatedAny && installed == 0 && !platformInstalled)
            {
                await ShowInfoAsync(
                    "A newer RXDK component is published than this extension can use, so it was not " +
                    "installed. Update the RXDK for Visual Studio extension first, then run setup again.");
            }
            else if (installed == 0 && !platformInstalled)
            {
                await ShowInfoAsync("RXDK is fully set up — C++ tools, Xbox platform, SDK, host tools, Zig and docs are all present.");
            }
            else
            {
                var parts = new List<string>();
                if (platformInstalled) parts.Add("the Xbox platform");
                if (installed > 0) parts.Add($"{installed} CLI component(s)");
                var msg = "RXDK setup finished — installed " + string.Join(" and ", parts) +
                    ". Use the COMPONENTS section (Update / Update All) to update them later.";
                if (gatedAny) msg += " Note: a newer component was withheld — update the RXDK extension first to get it.";
                await ShowInfoAsync(msg);
            }
        }

        // True when the MSVC v143 C++ build tools component is present in any VS instance.
        private static bool HasVc143()
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vswhere = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) return false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = $"-latest -requires {Vc143Component} -property installationPath",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(10000);
                    return !string.IsNullOrEmpty(outp);
                }
            }
            catch { return false; }
        }

        // True when the 'Xbox' platform is installed into at least one VS instance's VCTargetsPath.
        // The platform payload is considered current only when EVERY C++ toolset dest has both
        // Platform.props and a version stamp matching this extension's version. Existence alone is
        // not enough: an older RXDK VSIX may have left a stale platform (e.g. missing the folded-in
        // IntelliSense props) — that must be refreshed, not mistaken for "already installed".
        private static bool IsXboxPlatformCurrent()
        {
            try
            {
                var dests = FindXboxPlatformDests();
                if (dests.Count == 0) return false;
                var version = GetExtensionVersion();
                return dests.All(d =>
                    File.Exists(Path.Combine(d, "Platform.props")) &&
                    string.Equals(ReadPlatformStamp(d), version, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        // True when the VS-side prerequisites are all present: the .NET 8 runtime, the MSVC v143
        // C++ tools, and a current 'Xbox' MSBuild platform. The tool window uses this (together with
        // the CLI component versions) to hide the "Install Prerequisites" button once setup is
        // complete — e.g. after the standalone installer has run. Marketplace-installed users, who
        // have none of this yet, still see the button.
        public static bool VsSidePrerequisitesInstalled()
            => HasDotNet8() && HasVc143() && IsXboxPlatformCurrent();

        // Version this extension ships (from its manifest). Shared with the components gate.
        private static string GetExtensionVersion() => Services.ExtensionInfo.GetVersion();

        private static string ReadPlatformStamp(string dest)
        {
            try
            {
                var f = Path.Combine(dest, "RxdkPlatform.version");
                return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch { return null; }
        }

        private async Task SetBuildTypeAsync()
        {
            // Persisted in an Options page (Phase 3). For now surface the choices; the actual
            // --optimize value is passed by the build task once wired to settings.
            await ShowInfoAsync("Set Build Type: Debug / ReleaseSafe / ReleaseFast / ReleaseSmall. " +
                "An Options page persists this in Phase 3; until then edit tasks.vs.json's --optimize.");
        }

        private async Task CycleGlobalsScopeAsync()
        {
            // Live debug command; forwarded to Rxdk.Dap via a custom DAP request during a session.
            // TODO: send a custom 'rxdk/cycleGlobalsScope' request through the Debug Adapter Host
            // (parity with RXDK-VSCode rxdk.cycleGlobalsScope). No-op when no session is active.
            await ShowInfoAsync("Cycle Globals Visibility applies during an active debug session (Phase 2).");
        }

        private async Task OpenSettingsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // TODO(Phase 3): a DialogPage Options grid under Tools > Options > RXDK. For now open
            // the standard Options dialog.
            var dte = (EnvDTE.DTE)await _package.GetServiceAsync(typeof(EnvDTE.DTE));
            try { dte?.ExecuteCommand("Tools.Options"); } catch { /* best effort */ }
        }

        // ---- helpers shared with the tool window ----

        public async Task<string> GetXboxIpAsync()
        {
            var cliPath = ToolLocator.ResolveCli();
            if (cliPath == null)
            {
                return null;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = "xbox-ip",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
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
            catch
            {
                return null;
            }
        }

        // ---- tiny UI helpers ----

        private async Task ShowInfoAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(_package, message, "RXDK",
                OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private async Task ShowErrorAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(_package, message, "RXDK",
                OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        // Minimal modal string prompt. VS has no first-class input box, so we use a small WPF
        // dialog hosted by the tool window control's helper.
        private static string PromptForString(string title, string prompt, string initial)
        {
            return RxdkToolWindowControl.PromptForString(title, prompt, initial);
        }
    }
}
