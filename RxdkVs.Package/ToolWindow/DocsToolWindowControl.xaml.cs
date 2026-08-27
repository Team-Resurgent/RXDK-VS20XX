using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using RxdkVs.Package.Services;

namespace RxdkVs.Package.ToolWindow
{
    /// <summary>
    /// WPF host for the internal documentation viewer. Wraps a WebView2 that renders the themed
    /// <see cref="DocsShell"/> for a RXDK-Docs set (rxdk-vs, rxdk-vscode, or xboxsdk): a TOC sidebar +
    /// content pane that matches the VS Code doc panel. Page bodies are fetched from the host over the
    /// WebView2 message bridge; images resolve through a virtual host mapped to the doc-set folder.
    /// </summary>
    public partial class DocsToolWindowControl : UserControl
    {
        private string _docsRoot;
        private string _startPage;
        private string _currentPage;
        private bool _coreReady;
        private bool _navMappingSet;

        public DocsToolWindowControl()
        {
            InitializeComponent();
            // Re-theme in place when the user switches VS themes.
            VSColorTheme.ThemeChanged += OnThemeChanged;
            Unloaded += (s, e) => VSColorTheme.ThemeChanged -= OnThemeChanged;
        }

        /// <summary>Point the viewer at a doc set (folder with toc.json) and open a landing page.</summary>
        public async Task ShowDocsAsync(string docsRoot, string startPage)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _docsRoot = docsRoot;
            _startPage = startPage;
            _currentPage = startPage;
            await EnsureCoreAsync();
            RenderShell();
        }

        private async Task EnsureCoreAsync()
        {
            if (_coreReady)
            {
                return;
            }
            // A writable user-data folder outside the VS install dir (devenv's folder isn't writable).
            var udf = Path.Combine(Path.GetTempPath(), "RxdkDocsWebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, udf, null);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            // No context menu / dev tools / autofill in a doc pane.
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            _coreReady = true;
        }

        private void RenderShell()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_coreReady || string.IsNullOrEmpty(_docsRoot))
            {
                return;
            }
            // Map the doc-set folder onto a virtual host so <img>/resource links resolve.
            if (_navMappingSet)
            {
                Web.CoreWebView2.ClearVirtualHostNameToFolderMapping(DocsShell.VirtualHost);
            }
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                DocsShell.VirtualHost, _docsRoot, CoreWebView2HostResourceAccessKind.Allow);
            _navMappingSet = true;

            var palette = BuildPalette();
            try { Web.DefaultBackgroundColor = ParseHex(palette["bg"]); } catch { /* best effort */ }

            var tocPath = Path.Combine(_docsRoot, "toc.json");
            var tocJson = File.Exists(tocPath) ? File.ReadAllText(tocPath) : "{}";
            var shell = DocsShell.BuildShellHtml(null, tocJson, _currentPage ?? _startPage, palette);
            Web.CoreWebView2.NavigateToString(shell);
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                var kind = root.TryGetProperty("type", out var type) ? type.GetString() : null;
                if (kind == "print")
                {
                    // Use the OS print dialog (which offers "Microsoft Print to PDF" as a printer),
                    // not the in-browser preview -- the browser preview's Save-as-PDF can emit a
                    // 0-byte file and leave the hosted WebView2 painted black.
                    Web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
                    return;
                }
                if (kind != "navigate")
                {
                    return;
                }
                var page = root.TryGetProperty("page", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(page))
                {
                    return;
                }
                _currentPage = page;
                var html = DocsShell.RenderPageBody(_docsRoot, page);
                var payload = JsonSerializer.Serialize(new { type = "content", page, html });
                Web.CoreWebView2.PostWebMessageAsJson(payload);
            }
            catch
            {
                // Ignore malformed messages; the pane just doesn't navigate.
            }
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            // ThemeChanged is raised on the UI thread; rebuild the shell (and palette), keep the page.
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_coreReady && !string.IsNullOrEmpty(_docsRoot))
            {
                RenderShell();
            }
        }

        // Map the VS environment colors onto the CSS variables the shell stylesheet expects. Colors
        // that don't have a stable theme key are derived as translucent gray overlays (theme-neutral).
        private static Dictionary<string, string> BuildPalette()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var fg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            var border = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBorderColorKey);
            var accent = VSColorTheme.GetThemedColor(EnvironmentColors.ControlLinkTextColorKey);
            var accentHover = VSColorTheme.GetThemedColor(EnvironmentColors.ControlLinkTextHoverColorKey);
            return new Dictionary<string, string>
            {
                ["bg"] = ToHex(bg),
                ["fg"] = ToHex(fg),
                ["sidebar-bg"] = ToHex(Blend(bg, fg, 0.03)),
                ["input-bg"] = ToHex(Blend(bg, fg, 0.08)),
                ["border"] = ToHex(border),
                ["accent"] = ToHex(accent),
                ["accent-active"] = ToHex(accentHover),
                ["muted"] = ToHex(Blend(fg, bg, 0.45)),
                ["active-fg"] = ToHex(fg),
                ["code-bg"] = "rgba(127,127,127,.14)",
                ["hover"] = "rgba(127,127,127,.14)",
                ["active"] = "rgba(127,127,127,.28)",
                ["row-alt"] = "rgba(127,127,127,.06)",
            };
        }

        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color ParseHex(string hex)
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb(
                Convert.ToInt32(hex.Substring(0, 2), 16),
                Convert.ToInt32(hex.Substring(2, 2), 16),
                Convert.ToInt32(hex.Substring(4, 2), 16));
        }

        private static Color Blend(Color a, Color b, double t)
        {
            byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromArgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
        }
    }
}
