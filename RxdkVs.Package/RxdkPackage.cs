using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using RxdkVs.Package.Commands;
using RxdkVs.Package.Services;
using RxdkVs.Package.ToolWindow;
using Task = System.Threading.Tasks.Task;

namespace RxdkVs.Package
{
    /// <summary>
    /// The RXDK VS package. An <see cref="AsyncPackage"/> is the classic in-process VSSDK
    /// entry point: VS loads this assembly into its own .NET Framework AppDomain. Because the
    /// engine (Rxdk.Cli.exe) and debug adapter (Rxdk.Dap.exe) are net8, they cannot be loaded
    /// in-proc — they are driven as child processes (see Services/CliRunner + ToolLocator).
    ///
    /// Registration attributes below are what actually make the package discoverable:
    ///   [PackageRegistration]     — emits the pkgdef entry so VS knows this is a package.
    ///   [ProvideMenuResource]     — points VS at the compiled .vsct (Menus.ctmenu, ID 1).
    ///   [ProvideToolWindow]       — declares the RXDK tool window so it can be shown/persisted.
    ///   [ProvideAutoLoad]         — loads the package when the RXDK UI context becomes active
    ///                               (i.e. an Open-Folder workspace containing rxdk.project.json),
    ///                               so commands light up without the user invoking one first.
    ///   [ProvideUIContextRule]    — defines that UI context: active when the open folder tree
    ///                               contains a file named rxdk.project.json.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("RXDK for Visual Studio", "Original Xbox development: build, deploy, and debug.", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(Options.RxdkOptionsPage), "RXDK", "General", 0, 0, supportsAutomation: true)]
    [ProvideToolWindow(typeof(RxdkToolWindow), Style = VsDockStyle.Tabbed, Window = "DocumentWell", Orientation = ToolWindowOrientation.Left)]
    // The internal documentation viewer (WebView2). Opens as a large tab in the document well.
    [ProvideToolWindow(typeof(DocsToolWindow), Style = VsDockStyle.Tabbed, Window = "DocumentWell")]
    [ProvideAutoLoad(RxdkPackageGuids.RxdkProjectContextString, PackageAutoLoadFlags.BackgroundLoad)]
    // Also load for any open solution, so the F5 interceptor is registered when a .sln (with
    // native Xbox .vcxproj projects) is open — Open Folder is not the only project model.
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    // UI-context rule: active whenever a file matching rxdk.project.json is present. The
    // "HierSingleSelectionName" term matches the selected/opened hierarchy item name; the glob
    // form below is the shell's file-name expression. This is the closest declarative analog to
    // VS Code's "workspaceContains:**/rxdk.project.json" activation event.
    // TODO(verify): in pure Open-Folder mode (no .sln) VS may not raise this from a declarative
    // rule alone — if it doesn't fire, set the context imperatively in InitializeAsync by probing
    // OpenFolderContext and calling IVsMonitorSelection.SetCmdUIContext on this GUID.
    [ProvideUIContextRule(RxdkPackageGuids.RxdkProjectContextString,
        name: "RXDK project open",
        expression: "HasRxdkProject",
        termNames: new[] { "HasRxdkProject" },
        termValues: new[] { "HierSingleSelectionName:rxdk\\.project\\.json$" })]
    [Guid(RxdkPackageGuids.PackageGuidString)]
    public sealed class RxdkPackage : AsyncPackage
    {
        /// <summary>
        /// Async initialization. Runs on a background thread first (per the SDK contract),
        /// then switches to the UI thread to bind commands to the OleMenuCommandService.
        /// </summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Stage the bundled net8 engine into %ProgramData%\RXDK\engine so the build props
            // (RxdkCli) and the debug launcher find it — otherwise a fresh install can't compile
            // or debug a sample. Runs here on the background thread; best-effort (never throws).
            Services.EngineStager.StageBundledEngine();

            // Command wiring must happen on the UI thread (OleMenuCommandService is a UI service).
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await RxdkCommands.InitializeAsync(this);

            // Register the F5 / green-Run-button interceptor so debugging an Xbox startup project
            // routes to the Xbox debug adapter instead of the Local Windows Debugger.
            await StartDebugInterceptor.RegisterAsync(this, new CliRunner(this));
        }
    }
}
