using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace RxdkVs.Package.Services
{
    /// <summary>
    /// Launches a debug session for the solution's startup Xbox project. This is the single
    /// entry point used by both the RXDK &gt; Debug menu command and the F5 / green-Run-button
    /// interceptor (StartDebugInterceptor).
    ///
    /// Everything is read from the project's MSBuild properties (the .vcxproj), NOT from
    /// rxdk.project.json: the <c>RxdkXbox</c> marker identifies an Xbox project, and
    /// <c>NMakeOutput</c> gives the built .xbe from which the .exe/.pdb/title name are derived.
    /// The build+deploy still run through Rxdk.Cli against the project directory.
    /// </summary>
    internal static class XboxDebugLauncher
    {
        // Holds the current session's "Xbox Title" tailer alive (a bare Timer would be collected).
        private static TitleOutputPane _titlePane;

        internal sealed class StartupInfo
        {
            public string ProjectDir;     // dir of the .vcxproj (Rxdk.Cli --project-root)
            public string XbeOutput;      // NMakeOutput (…\out\<name>.xbe)
            public string ConfigName;     // "Debug" / "Release"
            public bool IsXbox;           // RxdkXbox == true
            public EnvDTE.Project Project; // for building via VS (generates the manifest)
            public string SolutionConfig; // active solution config name, e.g. "Debug"
        }

        /// <summary>True when the current startup project is an RXDK Xbox project.</summary>
        public static async Task<bool> IsXboxStartupProjectAsync(AsyncPackage package)
        {
            var info = await GetStartupInfoAsync(package);
            return info != null && info.IsXbox;
        }

        /// <summary>Lightweight facts about the Solution-Explorer-selected project (for context menus).</summary>
        internal struct SelectedProject
        {
            public bool IsXbox;      // RxdkXbox == true
            public bool IsDxt;       // NMakeOutput ends with .dxt
            public string Dir;       // project directory
            public string Name;      // output base name (from NMakeOutput)
            public string XbeOutput; // NMakeOutput
            public string SolutionConfig;
            public EnvDTE.Project Project;
        }

        /// <summary>
        /// Reads the currently selected Solution Explorer project's RXDK facts synchronously (safe
        /// to call from a command's BeforeQueryStatus, which runs on the UI thread). Returns false
        /// if the selection is not a single project.
        /// </summary>
        public static bool TryGetSelectedProject(out SelectedProject sel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            sel = default;
            if (!(Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection mon))
                return false;

            IntPtr hierPtr = IntPtr.Zero, containerPtr = IntPtr.Zero;
            try
            {
                if (mon.GetCurrentSelection(out hierPtr, out uint itemid, out _, out containerPtr) != VSConstants.S_OK)
                    return false;
                if (hierPtr == IntPtr.Zero) return false;
                if (!(Marshal.GetObjectForIUnknown(hierPtr) is IVsHierarchy hier)) return false;
                if (!(GetExtObject(hier) is EnvDTE.Project proj)) return false;

                string dir;
                try { dir = Path.GetDirectoryName(proj.FullName); }
                catch { return false; }

                var fullConfig = "Debug|Xbox";
                try
                {
                    var cfg = proj.ConfigurationManager?.ActiveConfiguration;
                    if (cfg != null) fullConfig = $"{cfg.ConfigurationName}|{cfg.PlatformName}";
                }
                catch { /* keep default */ }

                var bps = hier as IVsBuildPropertyStorage;
                var isXbox = string.Equals(ReadProp(bps, "RxdkXbox", fullConfig), "true", StringComparison.OrdinalIgnoreCase);
                var outp = ReadProp(bps, "NMakeOutput", fullConfig) ?? string.Empty;

                var solutionConfig = "Debug";
                try { solutionConfig = ((EnvDTE.DTE)proj.DTE).Solution.SolutionBuild.ActiveConfiguration.Name; }
                catch { /* keep default */ }

                sel = new SelectedProject
                {
                    IsXbox = isXbox,
                    IsDxt = outp.EndsWith(".dxt", StringComparison.OrdinalIgnoreCase),
                    Dir = dir,
                    Name = Path.GetFileNameWithoutExtension(outp),
                    XbeOutput = outp,
                    SolutionConfig = solutionConfig,
                    Project = proj,
                };
                return true;
            }
            catch { return false; }
            finally
            {
                if (hierPtr != IntPtr.Zero) Marshal.Release(hierPtr);
                if (containerPtr != IntPtr.Zero) Marshal.Release(containerPtr);
            }
        }

        /// <summary>
        /// Build + (re)deploy the selected RXDK project's .xbe and media to the console — the
        /// retry path when the devkit was off during F5. For a DXT, deploy to E:\dxt and warm-reboot.
        /// </summary>
        public static async Task DeploySelectedAsync(AsyncPackage package, CliRunner cli)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!TryGetSelectedProject(out var sel) || !sel.IsXbox)
            {
                await ShowAsync(package, "Select an RXDK Xbox project in Solution Explorer, then try Deploy again.");
                return;
            }
            if (string.IsNullOrEmpty(sel.XbeOutput))
            {
                await ShowAsync(package, "Could not determine the project's output (NMakeOutput). Build once, then Deploy.");
                return;
            }

            var info = new StartupInfo
            {
                ProjectDir = sel.Dir, XbeOutput = sel.XbeOutput, IsXbox = true,
                Project = sel.Project, SolutionConfig = sel.SolutionConfig,
            };
            // Incremental build via VS first (regenerates the manifest + ensures output is current),
            // then deploy — so this both retries a failed deploy and picks up any source changes.
            if (!await BuildViaVsAsync(package, info))
            {
                await ShowAsync(package, "Build failed — see the Output / Error List.");
                return;
            }
            // Deploy reads the committed rxdk.project.json (generated from the .vcxproj by the build)
            // and selects the built configuration's per-config outputDir.
            if (await cli.RunAsync(new[] { "deploy", "--project-root", info.ProjectDir, "--configuration", info.SolutionConfig }, info.ProjectDir) != 0)
            {
                await ShowAsync(package, "Deploy failed — is the devkit on and reachable? Fix it and run Deploy to Xbox again.");
                return;
            }
            if (sel.IsDxt)
            {
                await cli.RunAsync(new[] { "reboot" }, info.ProjectDir);
                await ShowAsync(package, $"Deployed {sel.Name}.dxt to E:\\dxt and warm-rebooted the console.");
                return;
            }
            await ShowAsync(package, $"Deployed {sel.Name} (.xbe + media) to the console.");
        }

        /// <summary>
        /// Build + deploy the startup Xbox project, then start a debug session against it via
        /// the VS Debug Adapter Host. No-op with a message if there's no Xbox startup project.
        /// </summary>
        public static async Task LaunchAsync(AsyncPackage package, CliRunner cli)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var info = await GetStartupInfoAsync(package);
            if (info == null || !info.IsXbox)
            {
                await ShowAsync(package, "No Xbox project is set as the startup project.");
                return;
            }
            if (string.IsNullOrEmpty(info.XbeOutput))
            {
                await ShowAsync(package, "Could not determine the project's output (NMakeOutput). Build the project once, then try again.");
                return;
            }

            var dap = ToolLocator.ResolveDap();
            if (dap == null || !File.Exists(dap))
            {
                await ShowAsync(package, "Rxdk.Dap.exe not found. Publish the engine to %ProgramData%\\RXDK\\engine (or set RXDK_TOOLS_DIR).");
                return;
            }

            // Build through VS/MSBuild (not Rxdk.Cli directly): that runs the platform's
            // RxdkGenerateProjectJson target, which writes the committed rxdk.project.json from the
            // .vcxproj. Then deploy reads that and selects the built configuration.
            if (!await BuildViaVsAsync(package, info))
            {
                await ShowAsync(package, "Build failed — see the Output / Error List.");
                return;
            }
            if (await cli.RunAsync(new[] { "deploy", "--project-root", info.ProjectDir, "--configuration", info.SolutionConfig }, info.ProjectDir) != 0)
            {
                await ShowAsync(package, "Deploy failed — is the devkit on and reachable?");
                return;
            }

            // A DXT is loaded by xbdm at boot, not attached as a title. Build + deploy to
            // E:\dxt (done above), warm-reboot, and stop — there is no debug-adapter session.
            if (info.XbeOutput.EndsWith(".dxt", StringComparison.OrdinalIgnoreCase))
            {
                await cli.RunAsync(new[] { "reboot" }, info.ProjectDir);
                await ShowAsync(package,
                    "DXT deployed to E:\\dxt and the console was warm-rebooted. A debug-monitor " +
                    "extension loads inside xbdm at boot, so there is no F5 attach-debug for it — " +
                    "it's now live on the console.");
                return;
            }

            // Derive the launch config from the .xbe output path.
            var outDir = Path.GetDirectoryName(info.XbeOutput);
            var name = Path.GetFileNameWithoutExtension(info.XbeOutput);
            // The shared adapter (Rxdk.Dap) appends the title's debug spew (DM_DEBUGSTR) to this file
            // when __titleOutputFile is set; TitleOutputPane tails it into a clean "Xbox Title" pane,
            // the formatted counterpart to the raw adapter stream in the Debug pane (parity with the
            // VS Code "Xbox Title" channel).
            var titleOutputFile = Path.Combine(Path.GetTempPath(), $"rxdk-title-{name}.log");
            var launch = new Dictionary<string, object>
            {
                ["$adapter"] = dap,
                ["type"] = "xbox",
                ["request"] = "launch",
                ["name"] = $"Debug {name}",
                ["program"] = Path.Combine(outDir, name + ".exe"),
                ["pdb"] = Path.Combine(outDir, name + ".pdb"),
                ["xbePath"] = $@"xe:\{name}\{name}.xbe",
                ["__workspaceFolder"] = info.ProjectDir,
                ["__titleOutputFile"] = titleOutputFile,
                ["reboot"] = false,
            };
            var launchFile = Path.Combine(Path.GetTempPath(), $"rxdk-launch-{name}.json");
            File.WriteAllText(launchFile, SimpleJson(launch));

            // Start (replacing any prior) the "Xbox Title" pane tailer before launching so no early
            // title output is missed. It stops itself when the session returns to design mode.
            _titlePane?.Stop();
            _titlePane = new TitleOutputPane(package);
            await _titlePane.StartAsync(titleOutputFile);

            var dte = (EnvDTE.DTE)await package.GetServiceAsync(typeof(EnvDTE.DTE));
            try
            {
                dte?.ExecuteCommand("DebugAdapterHost.Launch", $"/LaunchJson:\"{launchFile}\"");
            }
            catch (Exception ex)
            {
                await ShowAsync(package, $"Failed to start debugging: {ex.Message}. Is the VS Debug Adapter Host component installed?");
            }
        }

        /// <summary>
        /// Build the startup project synchronously through VS/MSBuild (which runs
        /// Rxdk.Xbox.targets to generate the manifest). Returns true on success.
        /// </summary>
        private static async Task<bool> BuildViaVsAsync(AsyncPackage package, StartupInfo info)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                if (!(await package.GetServiceAsync(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte))
                {
                    await ShowAsync(package, "Visual Studio automation (DTE) is unavailable.");
                    return false;
                }
                var sb = dte.Solution.SolutionBuild;
                sb.BuildProject(info.SolutionConfig, info.Project.UniqueName, WaitForBuildToFinish: true);
                return sb.LastBuildInfo == 0; // number of projects that failed to build
            }
            catch (Exception ex)
            {
                await ShowAsync(package, $"Build could not be started: {ex.Message}");
                return false;
            }
        }

        // ---- startup-project MSBuild property reads ----

        private static async Task<StartupInfo> GetStartupInfoAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var sbm = (IVsSolutionBuildManager)await package.GetServiceAsync(typeof(SVsSolutionBuildManager));
            if (sbm == null) return null;
            if (sbm.get_StartupProject(out IVsHierarchy hier) != VSConstants.S_OK || hier == null) return null;

            var proj = GetExtObject(hier) as EnvDTE.Project;
            if (proj == null) return null;

            string projectDir;
            try { projectDir = Path.GetDirectoryName(proj.FullName); }
            catch { return null; }

            var configName = "Debug";
            string fullConfig = "Debug|Xbox";
            try
            {
                var cfg = proj.ConfigurationManager?.ActiveConfiguration;
                if (cfg != null)
                {
                    configName = cfg.ConfigurationName;
                    fullConfig = $"{cfg.ConfigurationName}|{cfg.PlatformName}";
                }
            }
            catch { /* keep defaults */ }

            var bps = hier as IVsBuildPropertyStorage;
            var isXbox = string.Equals(ReadProp(bps, "RxdkXbox", fullConfig), "true", StringComparison.OrdinalIgnoreCase);
            var xbe = ReadProp(bps, "NMakeOutput", fullConfig);

            string solutionConfig = configName;
            try { solutionConfig = ((EnvDTE.DTE)proj.DTE).Solution.SolutionBuild.ActiveConfiguration.Name; }
            catch { /* keep project config name */ }

            return new StartupInfo
            {
                ProjectDir = projectDir, XbeOutput = xbe, ConfigName = configName, IsXbox = isXbox,
                Project = proj, SolutionConfig = solutionConfig,
            };
        }

        private static string ReadProp(IVsBuildPropertyStorage bps, string name, string config)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (bps == null) return null;
            try
            {
                if (bps.GetPropertyValue(name, config, (uint)_PersistStorageType.PST_PROJECT_FILE, out string value) == VSConstants.S_OK)
                    return value;
            }
            catch { /* property absent */ }
            return null;
        }

        private static object GetExtObject(IVsHierarchy hier)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return hier.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out object ext) == VSConstants.S_OK
                ? ext : null;
        }

        private static async Task ShowAsync(AsyncPackage package, string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(package, message, "RXDK",
                OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        // Minimal JSON writer for the flat launch dictionary (avoids taking a JSON dependency here).
        private static string SimpleJson(Dictionary<string, object> map)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            var first = true;
            foreach (var kv in map)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  \"").Append(kv.Key).Append("\": ");
                if (kv.Value is bool b) sb.Append(b ? "true" : "false");
                else sb.Append('"').Append(kv.Value.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            sb.Append("\n}\n");
            return sb.ToString();
        }
    }
}
