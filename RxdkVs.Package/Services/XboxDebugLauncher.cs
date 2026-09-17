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
    /// rxdk.project.json: an ApplicationType=RXDK project is identified by Keyword=RXDK, and its
    /// output/name/kind come from the evaluated $(OutDir)/$(TargetName)/$(ConfigurationType)
    /// (see GetProjectFacts). Build runs through VS/MSBuild; deploy runs through Rxdk.Cli with
    /// --no-manifest and the Xbox Deployment property-page values.
    /// </summary>
    internal static class XboxDebugLauncher
    {
        // Holds the current session's "Xbox Title" tailer alive (a bare Timer would be collected).
        private static TitleOutputPane _titlePane;

        internal sealed class StartupInfo
        {
            public string ProjectDir;     // dir of the .vcxproj (Rxdk.Cli --project-root)
            public string XbeOutput;      // evaluated $(OutDir)$(TargetName)$(TargetExt)
            public string ConfigName;     // "Debug" / "Release"
            public bool IsXbox;           // Keyword/ApplicationType=RXDK (RxdkXbox as fallback)
            public bool IsLaunchable;     // Application or DXT (a StaticLibrary is not deployable/runnable)
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
            public bool IsXbox;      // Keyword/ApplicationType=RXDK (RxdkXbox as fallback)
            public bool IsDxt;       // ConfigurationType == DebuggerExtension
            public bool IsLaunchable; // Application or DXT (a StaticLibrary is not deployable/runnable)
            public string Dir;       // project directory
            public string Name;      // $(TargetName)
            public string XbeOutput; // evaluated $(OutDir)$(TargetName)$(TargetExt)
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

                // Evaluated from the VC project model (ApplicationType=RXDK sets no RxdkXbox/NMakeOutput).
                var facts = GetProjectFacts(proj);
                if (facts == null) return false;

                var solutionConfig = "Debug";
                try { solutionConfig = ((EnvDTE.DTE)proj.DTE).Solution.SolutionBuild.ActiveConfiguration.Name; }
                catch { /* keep default */ }

                sel = new SelectedProject
                {
                    IsXbox = facts.IsXbox,
                    IsDxt = facts.IsDxt,
                    IsLaunchable = facts.IsLaunchable,
                    Dir = dir,
                    Name = facts.TargetName ?? Path.GetFileNameWithoutExtension(dir),
                    XbeOutput = facts.TargetPath ?? string.Empty,
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
            if (!sel.IsLaunchable)
            {
                await ShowAsync(package,
                    $"'{sel.Name}' is a static library — it builds a .lib, not a title, so there is nothing to deploy. " +
                    "Select an Xbox application (or DXT) project.");
                return;
            }
            if (string.IsNullOrEmpty(sel.XbeOutput))
            {
                await ShowAsync(package, "Could not determine the project's output. Build once, then Deploy.");
                return;
            }

            var info = new StartupInfo
            {
                ProjectDir = sel.Dir, XbeOutput = sel.XbeOutput, IsXbox = true,
                Project = sel.Project, SolutionConfig = sel.SolutionConfig,
            };
            // Incremental build via VS first (ensures the output is current), then deploy -- so this
            // both retries a failed deploy and picks up any source changes.
            if (!await BuildViaVsAsync(package, info))
            {
                await ShowAsync(package, "Build failed — see the Output / Error List.");
                return;
            }
            // Deploy is driven entirely from the .vcxproj (--no-manifest): the evaluated output dir +
            // the Xbox Deployment property-page values, never rxdk.project.json.
            var deployFacts = GetProjectFacts(info.Project);
            if (deployFacts == null)
            {
                await ShowAsync(package, "Could not read the project's deployment properties.");
                return;
            }
            if (await cli.RunAsync(BuildDeployArgs(deployFacts, info.SolutionConfig), info.ProjectDir) != 0)
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
            if (!info.IsLaunchable)
            {
                await ShowAsync(package,
                    $"'{info.Project?.Name}' is a static library — it builds a .lib, not a title, so it can't be " +
                    "deployed or debugged. Set an Xbox application (or DXT) project as the startup project, then try again.");
                return;
            }
            if (string.IsNullOrEmpty(info.XbeOutput))
            {
                await ShowAsync(package, "Could not determine the project's output. Build the project once, then try again.");
                return;
            }

            var dap = ToolLocator.ResolveDap();
            if (dap == null || !File.Exists(dap))
            {
                await ShowAsync(package, "Rxdk.Dap.exe not found. Publish the engine to %ProgramData%\\RXDK\\engine (or set RXDK_TOOLS_DIR).");
                return;
            }

            // Build through VS/MSBuild (not Rxdk.Cli directly) so the VC project system builds the
            // .xbe/.iso the way the IDE would, then deploy from the .vcxproj (--no-manifest).
            if (!await BuildViaVsAsync(package, info))
            {
                await ShowAsync(package, "Build failed — see the Output / Error List.");
                return;
            }
            var launchFacts = GetProjectFacts(info.Project);
            if (launchFacts == null)
            {
                await ShowAsync(package, "Could not read the project's deployment properties.");
                return;
            }
            if (await cli.RunAsync(BuildDeployArgs(launchFacts, info.SolutionConfig), info.ProjectDir) != 0)
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
            try
            {
                var cfg = proj.ConfigurationManager?.ActiveConfiguration;
                if (cfg != null) configName = cfg.ConfigurationName;
            }
            catch { /* keep defaults */ }

            // Evaluated from the VC project model (ApplicationType=RXDK sets no RxdkXbox/NMakeOutput).
            var facts = GetProjectFacts(proj);
            if (facts == null) return null;
            var isXbox = facts.IsXbox;
            var xbe = facts.TargetPath;

            string solutionConfig = configName;
            try { solutionConfig = ((EnvDTE.DTE)proj.DTE).Solution.SolutionBuild.ActiveConfiguration.Name; }
            catch { /* keep project config name */ }

            return new StartupInfo
            {
                ProjectDir = projectDir, XbeOutput = xbe, ConfigName = configName, IsXbox = isXbox,
                IsLaunchable = facts.IsLaunchable, Project = proj, SolutionConfig = solutionConfig,
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

        // ---- New-toolset (ApplicationType=RXDK) project facts ----
        //
        // The retired Makefile mechanism marked a project with RxdkXbox=true and exposed its output
        // via NMakeOutput, both of which IVsBuildPropertyStorage could read as persisted strings. The
        // ApplicationType=RXDK toolset sets NEITHER: a project is marked Keyword=RXDK and its output
        // is $(OutDir)$(TargetName)$(TargetExt), which are toolset defaults (not persisted), so they
        // only resolve by EVALUATING them in the config's context. That is what the VC project model's
        // VCConfiguration.Evaluate does; we reach it late-bound (dynamic) to avoid a hard
        // Microsoft.VisualStudio.VCProjectEngine reference.

        internal sealed class ProjectFacts
        {
            public bool IsXbox;
            public bool IsDxt;
            public string ConfigurationType; // $(ConfigurationType): Application / StaticLibrary / DebuggerExtension / ...
            public string ProjectDir;
            public string TargetName;      // $(TargetName)
            public string TargetPath;      // absolute $(OutDir)$(TargetName)$(TargetExt) (.xbe/.dxt/.lib)
            public string OutDir;          // absolute evaluated $(OutDir)
            public string RemotePath;      // $(RxdkRemotePath) (may be empty -> CLI convention)
            public bool? ForceCopy;        // $(RxdkForceCopy)
            public string DeployPaths;     // $(RxdkDeployPaths), ';'-separated (may be empty)

            /// <summary>
            /// True for the project kinds that produce something to put on the console: an Application
            /// (.xbe, deploy + F5-attach) or a DebuggerExtension (.dxt, copied to E:\dxt + warm reboot).
            /// A StaticLibrary/DynamicLibrary is still an RXDK Xbox project (Keyword=RXDK) but only builds
            /// a .lib, so it cannot be deployed or debugged as a title.
            /// </summary>
            public bool IsLaunchable =>
                string.Equals(ConfigurationType, "Application", StringComparison.OrdinalIgnoreCase) || IsDxt;
        }

        // The VCConfiguration (dynamic) for a project's config, e.g. "Debug|Xbox".
        private static dynamic GetVcConfig(EnvDTE.Project proj, string fullConfig)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                dynamic vcProj = proj?.Object; // VCProject
                if (vcProj == null) return null;
                foreach (dynamic cfg in vcProj.Configurations)
                    if (string.Equals((string)cfg.Name, fullConfig, StringComparison.OrdinalIgnoreCase))
                        return cfg;
                foreach (dynamic cfg in vcProj.Configurations) return cfg; // fallback: first
            }
            catch { /* not a VC project / automation unavailable */ }
            return null;
        }

        // Evaluate the RXDK-relevant MSBuild properties for the project's active configuration.
        internal static ProjectFacts GetProjectFacts(EnvDTE.Project proj)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (proj == null) return null;
            string projectDir;
            try { projectDir = Path.GetDirectoryName(proj.FullName); }
            catch { return null; }

            string configName = "Debug", platform = "Xbox";
            try
            {
                var active = proj.ConfigurationManager?.ActiveConfiguration;
                if (active != null) { configName = active.ConfigurationName; platform = active.PlatformName; }
            }
            catch { /* keep defaults */ }

            dynamic vc = GetVcConfig(proj, $"{configName}|{platform}");
            string Eval(string expr)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try { return vc?.Evaluate(expr) as string; } catch { return null; }
            }

            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
            // Xbox project: the new toolset marks it Keyword=RXDK / ApplicationType=RXDK; keep the old
            // RxdkXbox marker as a fallback for a project still on the retired mechanism.
            var isXbox = string.Equals(Eval("$(Keyword)"), "RXDK", OIC)
                || string.Equals(Eval("$(ApplicationType)"), "RXDK", OIC)
                || string.Equals(Eval("$(RxdkXbox)"), "true", OIC);
            var configType = Eval("$(ConfigurationType)") ?? "";
            var isDxt = string.Equals(configType, "DebuggerExtension", OIC);

            var outDir = Eval("$(OutDir)") ?? string.Empty;
            var targetName = Eval("$(TargetName)");
            var targetExt = Eval("$(TargetExt)"); // .xbe / .dxt / .lib
            string absOutDir = null, targetPath = null;
            if (!string.IsNullOrEmpty(outDir))
                absOutDir = Path.GetFullPath(Path.IsPathRooted(outDir) ? outDir : Path.Combine(projectDir, outDir));
            if (absOutDir != null && !string.IsNullOrEmpty(targetName))
                targetPath = Path.Combine(absOutDir, targetName + (targetExt ?? string.Empty));

            var forceCopyRaw = Eval("$(RxdkForceCopy)");
            bool? forceCopy = string.IsNullOrEmpty(forceCopyRaw) ? (bool?)null : string.Equals(forceCopyRaw, "true", OIC);

            return new ProjectFacts
            {
                IsXbox = isXbox,
                IsDxt = isDxt,
                ConfigurationType = configType,
                ProjectDir = projectDir,
                TargetName = targetName,
                TargetPath = targetPath,
                OutDir = absOutDir,
                RemotePath = Eval("$(RxdkRemotePath)"),
                ForceCopy = forceCopy,
                DeployPaths = Eval("$(RxdkDeployPaths)"),
            };
        }

        // The Rxdk.Cli 'deploy' argument list for a .vcxproj project: always --no-manifest (VS20XX is
        // .vcxproj-driven and must never read rxdk.project.json), plus the evaluated output dir, name,
        // and the Xbox Deployment page values. Empty properties are omitted so the CLI applies its
        // own defaults (e.g. the xe:\<name> remote-path convention).
        private static string[] BuildDeployArgs(ProjectFacts f, string solutionConfig)
        {
            var args = new List<string>
            {
                "deploy",
                "--project-root", f.ProjectDir,
                "--configuration", solutionConfig,
                "--no-manifest",
            };
            if (!string.IsNullOrEmpty(f.OutDir)) { args.Add("--local-dir"); args.Add(f.OutDir); }
            if (!string.IsNullOrEmpty(f.TargetName)) { args.Add("--name"); args.Add(f.TargetName); }
            if (f.IsDxt) args.Add("--dxt");
            if (!string.IsNullOrWhiteSpace(f.RemotePath)) { args.Add("--remote-dir"); args.Add(f.RemotePath); }
            if (f.ForceCopy == true) { args.Add("--force-copy"); args.Add("true"); }
            if (!string.IsNullOrWhiteSpace(f.DeployPaths)) { args.Add("--deploy-paths"); args.Add(f.DeployPaths); }
            return args.ToArray();
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
