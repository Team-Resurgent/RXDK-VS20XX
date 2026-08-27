using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace RxdkVs.Package.ToolWindow
{
    /// <summary>
    /// The internal RXDK documentation viewer window. Hosts <see cref="DocsToolWindowControl"/>
    /// (a WebView2 that renders the themed doc shell). Opened by the "Xbox SDK Documentation" and
    /// "Extension Documentation" commands (see RxdkCommands.OpenDocsAsync), replacing the previous
    /// open-in-system-browser behavior so docs render inside VS, themed to match.
    /// </summary>
    [Guid(RxdkPackageGuids.DocsToolWindowGuidString)]
    public sealed class DocsToolWindow : ToolWindowPane
    {
        public DocsToolWindow() : base(null)
        {
            Caption = "RXDK Documentation";
            Content = new DocsToolWindowControl();
        }

        /// <summary>The hosted WebView2 control, or null before the frame is created.</summary>
        public DocsToolWindowControl View => Content as DocsToolWindowControl;
    }
}
