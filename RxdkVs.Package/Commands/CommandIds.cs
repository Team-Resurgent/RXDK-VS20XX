namespace RxdkVs.Package.Commands
{
    /// <summary>
    /// Numeric command IDs, mirrored 1:1 in RxdkPackage.vsct. Each value is the &lt;Button&gt;
    /// id in the command table; RxdkCommands binds a handler to (CommandSet, id). These map
    /// to RXDK-VSCode's contributes.commands entries (see package.json).
    /// </summary>
    internal static class CommandIds
    {
        // Menu / group anchors.
        public const int RxdkTopMenu = 0x1000;
        public const int RxdkMainGroup = 0x1100;   // Build / Deploy / Run / Debug
        public const int RxdkConsoleGroup = 0x1200; // Reboot / Set IP / DXT
        public const int RxdkProjectGroup = 0x1300; // New Project
        public const int RxdkToolsGroup = 0x1400;   // SDK folder, tools, docs, xbwatson…
        public const int RxdkSetupGroup = 0x1500;   // prerequisites / settings

        // Buttons (rxdk.* command parity).
        public const int CmdSetupPrerequisites = 0x0101; // rxdk.setupPrerequisites
        public const int CmdNewProject = 0x0102;         // rxdk.newProject
        public const int CmdBuild = 0x0103;              // rxdk.build
        public const int CmdDeploy = 0x0104;             // rxdk.deploy
        public const int CmdRun = 0x0105;                // rxdk.run
        public const int CmdRemoveDxt = 0x0106;          // rxdk.removeDxt
        public const int CmdRebootConsole = 0x0107;      // rxdk.rebootConsole
        public const int CmdDebug = 0x0108;              // rxdk.debug
        public const int CmdInstallXboxNeighborhood = 0x0109; // rxdk.installXboxNeighborhood
        public const int CmdSetXboxIp = 0x010A;          // rxdk.setXboxIp
        public const int CmdShowToolWindow = 0x010B;     // rxdk.showSidebar
        public const int CmdOpenSdkDocs = 0x010C;        // rxdk.openSdkDocs
        public const int CmdOpenExtensionDocs = 0x010D;  // rxdk.openExtensionDocs
        public const int CmdOpenSdkFolder = 0x010E;      // rxdk.openSdkFolder
        public const int CmdOpenToolsFolder = 0x010F;    // rxdk.openToolsFolder
        public const int CmdOpenDocsFolder = 0x0110;     // rxdk.openDocsFolder
        public const int CmdFetchLatestSdk = 0x0111;     // rxdk.fetchLatestSdk
        public const int CmdInstallDotNet = 0x0112;      // rxdk.installDotNetRuntime
        public const int CmdLaunchXbwatson = 0x0113;     // rxdk.launchXbwatson
        public const int CmdLaunchXbNeighborhood = 0x0114; // rxdk.launchXbNeighborhood
        public const int CmdOpenXboxNeighborhood = 0x0115; // rxdk.openXboxNeighborhood
        public const int CmdSetBuildType = 0x0117;       // rxdk.setBuildType
        public const int CmdOpenSettings = 0x0118;       // rxdk.openSettings
        public const int CmdDeployProject = 0x0119;      // rxdk.deployProject (project context menu)
        public const int CmdImportProject = 0x011A;      // rxdk.importProject (VS2003 importer)
        public const int CmdLaunchXemu = 0x011B;         // rxdk.launchXemu (build + boot ISO in xemu)
        public const int CmdInstallBuildTools = 0x011C;  // install MSVC v143 C++ build tools (VS Installer)
        public const int CmdInstallXboxPlatform = 0x011D; // install the custom 'Xbox' MSBuild platform (elevated)
        public const int CmdImportVsCodeProject = 0x011F; // rxdk.importVsCodeProject (generate a .vcxproj/.sln from an existing rxdk.project.json)
    }
}
