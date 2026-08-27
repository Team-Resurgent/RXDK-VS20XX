using System;

namespace RxdkVs.Package
{
    /// <summary>
    /// The single source of truth for every GUID the package uses. These MUST stay in
    /// sync with the string forms in RxdkPackage.vsct, RxdkPackage.cs attributes, and
    /// RxdkVs.Package.pkgdef — the VSSDK matches menus/commands/tool windows by GUID, so
    /// a mismatch silently drops the command from the UI.
    /// </summary>
    internal static class RxdkPackageGuids
    {
        /// <summary>The AsyncPackage's own GUID (matches [Guid] on RxdkPackage).</summary>
        public const string PackageGuidString = "c5390b93-36ff-4de6-9ca3-3776d9c709bb";

        /// <summary>The command-set GUID shared by every command/menu/group in the .vsct.</summary>
        public const string CommandSetGuidString = "5652ff38-066f-4c97-bc61-212735f299f9";

        /// <summary>The RXDK tool window's persistence GUID.</summary>
        public const string ToolWindowGuidString = "9afdd434-9369-4407-965a-03b931e7b46b";

        /// <summary>The RXDK documentation tool window's persistence GUID (the internal doc viewer).</summary>
        public const string DocsToolWindowGuidString = "b2e9c1a4-7d3f-4e28-9a51-6c0f2d4b8e77";

        /// <summary>
        /// UI-context GUID activated when the opened folder contains rxdk.project.json.
        /// [ProvideAutoLoad] on this context loads the package for RXDK folders only.
        /// The matching &lt;Rule&gt; is declared on the package (see RxdkPackage.cs).
        /// </summary>
        public const string RxdkProjectContextString = "84774d77-dd7a-4c51-9928-fa0efe37faf5";

        public static readonly Guid CommandSet = new Guid(CommandSetGuidString);
    }
}
