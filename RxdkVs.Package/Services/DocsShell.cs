using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RxdkVs.Package.Services
{
    /// <summary>
    /// Builds the themed HTML "shell" (sidebar TOC + content pane) for the internal documentation
    /// viewer, and renders individual RXDK-Docs pages into it. This is the C# port of the VS Code
    /// extension's docs webview (RXDK-VSCode/src/sdkDocs.ts): same body-extract + sanitize + link
    /// rewrite, the same two-column layout and filter box, so a doc set looks the same in both IDEs.
    ///
    /// All assets are embedded here (CSS + JS are string constants), so nothing extra ships in the
    /// VSIX. Page bodies are rewritten so relative resources (images) resolve against a WebView2
    /// virtual host, and in-doc .htm links navigate in-panel via a postMessage round-trip rather than
    /// spawning the browser.
    /// </summary>
    internal static class DocsShell
    {
        /// <summary>Virtual host the WebView2 maps onto the doc-set folder (see DocsToolWindowControl).</summary>
        public const string VirtualHost = "rxdk-docs.invalid";

        // -- page rendering ---------------------------------------------------------------------

        /// <summary>
        /// Reads a doc page, extracts its &lt;body&gt;, strips legacy presentational markup, rewrites
        /// links (relative resources -&gt; virtual host; in-doc .htm -&gt; in-panel data-doc-page), and
        /// wraps it in the .doc article the shell styles.
        /// </summary>
        public static string RenderPageBody(string docsRoot, string page)
        {
            var safe = Path.GetFileName(page ?? string.Empty);
            if (string.IsNullOrEmpty(safe))
            {
                return "<p>No page.</p>";
            }
            var full = Path.Combine(docsRoot, safe);
            if (!File.Exists(full))
            {
                return $"<p>Topic not found: {EscapeHtml(safe)}</p>";
            }
            var raw = DecodeDoc(File.ReadAllBytes(full));
            var withLinks = RewriteLinks(raw);
            var body = SanitizeLegacy(ExtractBody(withLinks));
            return "<article class=\"doc\">" + body + "</article>";
        }

        // The RXDK doc sets are UTF-8; the legacy Xbox SDK set (xboxsdk/) is Windows-1252. Decode as
        // UTF-8 when the bytes are valid UTF-8 (covers RXDK docs + pure ASCII); otherwise fall back to
        // 1252, whose high bytes (smart quotes) are invalid UTF-8 so the strict decode reliably rejects.
        private static string DecodeDoc(byte[] bytes)
        {
            try
            {
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                var s = strict.GetString(bytes);
                return s.TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }

        private static string ExtractBody(string html)
        {
            var m = Regex.Match(html, "<body[^>]*>([\\s\\S]*)</body>", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : html;
        }

        // Rewrite href/src: leave anchors and absolute (scheme:) URLs alone; in-doc .htm(l) links get
        // neutralized to a data-doc-page marker the shell's click handler navigates in-panel; other
        // relative resources (images/foo.gif) point at the virtual host so WebView2 serves them.
        private static string RewriteLinks(string html)
        {
            return Regex.Replace(html, "(href|src)\\s*=\\s*\"([^\"]*)\"", m =>
            {
                var attr = m.Groups[1].Value;
                var target = m.Groups[2].Value;
                if (string.IsNullOrEmpty(target) || target.StartsWith("#") || Regex.IsMatch(target, "^[a-z]+:", RegexOptions.IgnoreCase))
                {
                    return $"{attr}=\"{target}\"";
                }
                var pathOnly = target.Split('#', '?')[0];
                if (attr.Equals("href", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(pathOnly, "\\.html?$", RegexOptions.IgnoreCase))
                {
                    return $"data-doc-page=\"{target}\" href=\"#\"";
                }
                var rel = target.TrimStart('.', '/');
                return $"{attr}=\"https://{VirtualHost}/{rel}\"";
            }, RegexOptions.IgnoreCase);
        }

        // Strip the dated presentational markup the CHM export carries (fixed colors, <font> tags,
        // embedded scripts/styles) so the shell's theme-aware stylesheet takes over.
        private static string SanitizeLegacy(string body)
        {
            body = Regex.Replace(body, "<script[\\s\\S]*?</script>", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "<style[\\s\\S]*?</style>", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "<div[^>]*\\b(?:class|id)\\s*=\\s*[\"']?footer[\"']?[^>]*>[\\s\\S]*?</div>", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "<table[^>]*\\bclass\\s*=\\s*[\"']?buttonbar(?:shade|table)[\"']?[^>]*>[\\s\\S]*?</table>", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "</?font[^>]*>", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "</?basefont[^>]*>", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "\\s(?:bgcolor|background|link|vlink|alink|text|color)\\s*=\\s*\"[^\"]*\"", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "\\s(?:bgcolor|background|link|vlink|alink|text|color)\\s*=\\s*'[^']*'", string.Empty, RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "\\sstyle\\s*=\\s*\"([^\"]*)\"", m =>
            {
                var cleaned = Regex.Replace(m.Groups[1].Value, "(?:background(?:-color)?|color)\\s*:[^;\"]*;?", string.Empty, RegexOptions.IgnoreCase).Trim();
                return cleaned.Length > 0 ? $" style=\"{cleaned}\"" : string.Empty;
            }, RegexOptions.IgnoreCase);
            return body;
        }

        // -- shell ------------------------------------------------------------------------------

        /// <summary>
        /// Builds the full shell document: embedded CSS themed from <paramref name="palette"/>, the
        /// sidebar rendered from the set's toc.json, and the client script that requests page bodies
        /// from the host (WebView2 postMessage) and navigates in-panel.
        /// </summary>
        public static string BuildShellHtml(string setTitle, string tocJson, string startPage, IDictionary<string, string> palette)
        {
            string tocHtml;
            try
            {
                using var doc = JsonDocument.Parse(tocJson);
                var root = doc.RootElement;
                if (string.IsNullOrEmpty(setTitle) && root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    setTitle = t.GetString();
                }
                tocHtml = root.TryGetProperty("toc", out var toc) && toc.ValueKind == JsonValueKind.Array
                    ? RenderTocNodes(toc)
                    : "<ul></ul>";
            }
            catch
            {
                tocHtml = "<ul></ul>";
            }

            var vars = new StringBuilder();
            foreach (var kv in palette)
            {
                vars.Append("      --").Append(kv.Key).Append(": ").Append(kv.Value).Append(";\n");
            }

            return "<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"UTF-8\">\n<style>\n"
                + ":root{\n" + vars + Css + "\n</style></head>\n<body>\n"
                + "  <aside>\n    <div class=\"side-head\">\n"
                + "      <div class=\"side-head-row\"><div class=\"title\">" + EscapeHtml(setTitle ?? "Documentation") + "</div>"
                + "<button id=\"printBtn\" class=\"tool-btn\" title=\"Print this page\">Print</button></div>\n"
                + "      <input id=\"filter\" class=\"filter\" type=\"text\" placeholder=\"Filter topics&hellip;\" autocomplete=\"off\" spellcheck=\"false\">\n"
                + "    </div>\n    <nav class=\"toc\">" + tocHtml + "</nav>\n  </aside>\n"
                + "  <main><div id=\"content\">Loading&hellip;</div></main>\n"
                + "  <script>\n" + BuildScript(startPage) + "\n  </script>\n</body></html>";
        }

        private static string RenderTocNodes(JsonElement nodes)
        {
            var sb = new StringBuilder("<ul>");
            foreach (var node in nodes.EnumerateArray())
            {
                var name = node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
                var page = node.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var hasChildren = node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0;
                sb.Append("<li>");
                if (!string.IsNullOrEmpty(page))
                {
                    sb.Append("<button data-page=\"").Append(EscapeHtml(page)).Append("\">").Append(EscapeHtml(name)).Append("</button>");
                }
                else
                {
                    sb.Append("<span class=\"folder\">").Append(EscapeHtml(name)).Append("</span>");
                }
                if (hasChildren)
                {
                    sb.Append(RenderTocNodes(ch));
                }
                sb.Append("</li>");
            }
            sb.Append("</ul>");
            return sb.ToString();
        }

        private static string BuildScript(string startPage)
        {
            // Mirrors sdkDocs.ts: request the start page, navigate on TOC/link clicks, filter the tree.
            // Uses the WebView2 host bridge (chrome.webview) instead of VS Code's acquireVsCodeApi.
            var start = JsonEscapeString(startPage ?? "");
            return
"    const host = window.chrome.webview;\n" +
"    const content = document.getElementById('content');\n" +
"    function setActive(page){\n" +
"      document.querySelectorAll('[data-page]').forEach(el=>el.classList.toggle('active', el.getAttribute('data-page')===page));\n" +
"      const cur=document.querySelector('[data-page].active'); if(cur&&cur.scrollIntoView) cur.scrollIntoView({block:'nearest'});\n" +
"    }\n" +
"    function navigate(page){ if(!page) return; setActive(page); host.postMessage({type:'navigate', page:page}); }\n" +
"    document.querySelectorAll('[data-page]').forEach(el=>el.addEventListener('click',()=>navigate(el.getAttribute('data-page'))));\n" +
"    host.addEventListener('message', e=>{ const m=e.data; if(m&&m.type==='content'){ setActive(m.page); content.innerHTML=m.html; content.parentElement.scrollTop=0; } });\n" +
"    content.addEventListener('click', e=>{ const a=e.target&&e.target.closest?e.target.closest('a'):null; if(!a) return;\n" +
"      const t=a.getAttribute('data-doc-page'); if(t){ e.preventDefault(); const page=t.split('#')[0].split('?')[0].split('/').pop(); if(page&&page.toLowerCase().endsWith('.htm')) navigate(page); } }, true);\n" +
"    const filter=document.getElementById('filter');\n" +
"    filter.addEventListener('input',()=>{ const q=filter.value.trim().toLowerCase();\n" +
"      document.querySelectorAll('.toc li').forEach(li=>{ if(!q){ li.classList.remove('hidden'); return; }\n" +
"        const btn=li.querySelector(':scope > button, :scope > .folder'); const self=btn?btn.textContent.toLowerCase().includes(q):false;\n" +
"        const child=li.querySelector('li:not(.hidden)'); li.classList.toggle('hidden', !(self|| !!child)); }); });\n" +
"    const pb=document.getElementById('printBtn'); if(pb) pb.addEventListener('click',()=>host.postMessage({type:'print'}));\n" +
"    navigate(" + start + ");\n";
        }

        // The stylesheet (everything after the :root palette block). Kept close to sdkDocs.ts so the
        // two IDEs render identically; colors come from the injected --vars above.
        private const string Css =
@"      color-scheme: light dark;
    }
    *{box-sizing:border-box}
    html,body{height:100%}
    body{margin:0;font-family:'Segoe UI',-apple-system,sans-serif;font-size:13px;color:var(--fg);background:var(--bg);display:grid;grid-template-columns:300px 1fr;}
    aside{display:flex;flex-direction:column;min-height:0;background:var(--sidebar-bg);border-right:1px solid var(--border);}
    .side-head{padding:14px 16px 10px;border-bottom:1px solid var(--border);}
    .side-head-row{display:flex;align-items:center;justify-content:space-between;gap:8px;}
    .side-head .title{font-size:11px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);}
    .tool-btn{flex:0 0 auto;border:1px solid var(--border);background:transparent;color:var(--fg);font:inherit;font-size:11px;padding:2px 9px;border-radius:6px;cursor:pointer;}
    .tool-btn:hover{background:var(--hover);border-color:var(--accent);}
    .side-head .filter{margin-top:10px;width:100%;padding:6px 9px;font:inherit;color:var(--fg);background:var(--input-bg);border:1px solid var(--border);border-radius:6px;}
    .side-head .filter:focus{outline:none;border-color:var(--accent);}
    nav.toc{overflow:auto;padding:8px 6px 16px;flex:1 1 auto;}
    .toc ul{list-style:none;margin:0;padding-left:10px;}
    .toc > ul{padding-left:4px;}
    .toc li{margin:1px 0;}
    .toc li.hidden{display:none;}
    .toc button{width:100%;text-align:left;border:0;background:transparent;color:inherit;padding:5px 10px;border-radius:6px;cursor:pointer;font:inherit;line-height:1.35;}
    .toc button:hover{background:var(--hover);}
    .toc button.active{background:var(--active);color:var(--active-fg);}
    .toc .folder{display:block;padding:8px 10px 3px;color:var(--muted);font-size:11px;font-weight:700;letter-spacing:.04em;text-transform:uppercase;}
    main{overflow:auto;min-height:0;}
    .doc{max-width:860px;margin:0 auto;padding:32px 40px 96px;line-height:1.65;color:var(--fg);}
    .doc :first-child{margin-top:0;}
    .doc h1,.doc h2,.doc h3,.doc h4{line-height:1.25;font-weight:600;margin:1.8em 0 .6em;}
    .doc h1{font-size:1.9em;margin-top:.2em;padding-bottom:.3em;border-bottom:1px solid var(--border);}
    .doc h2{font-size:1.45em;padding-bottom:.25em;border-bottom:1px solid var(--border);}
    .doc h3{font-size:1.2em;}
    .doc h4{font-size:1.05em;color:var(--muted);}
    .doc p,.doc ul,.doc ol,.doc dl{margin:0 0 1em;}
    .doc ul,.doc ol{padding-left:1.6em;}
    .doc li{margin:.25em 0;}
    .doc a{color:var(--accent);text-decoration:none;}
    .doc a:hover{color:var(--accent-active);text-decoration:underline;}
    .doc code,.doc kbd,.doc samp,.doc tt{font-family:ui-monospace,Consolas,monospace;font-size:.92em;background:var(--code-bg);padding:.12em .38em;border-radius:4px;}
    .doc pre{background:var(--code-bg);border:1px solid var(--border);border-radius:8px;padding:14px 16px;overflow:auto;line-height:1.5;}
    .doc pre code,.doc pre tt{background:none;padding:0;border-radius:0;}
    .doc table{border-collapse:collapse;width:100%;margin:0 0 1.2em;font-size:.96em;border-radius:8px;border:1px solid var(--border);overflow:hidden;}
    .doc th,.doc td{border:1px solid var(--border);padding:7px 11px;text-align:left;vertical-align:top;}
    .doc th{background:var(--code-bg);font-weight:600;}
    .doc tr:nth-child(even) td{background:var(--row-alt);}
    .doc img{max-width:100%;height:auto;}
    .doc hr{border:0;border-top:1px solid var(--border);margin:2em 0;}
    .doc blockquote{margin:0 0 1em;padding:.3em 1em;border-left:3px solid var(--accent);color:var(--muted);}
    .doc dt{font-weight:600;margin-top:.8em;}
    .doc dd{margin:0 0 .5em 1.4em;}
    @media print{
      body{display:block;background:#fff;color:#000;}
      aside{display:none;}
      main{overflow:visible;}
      .doc{max-width:none;margin:0;padding:0;color:#000;}
      .doc a{color:#000;text-decoration:underline;}
      .doc pre,.doc code,.doc th,.doc tr:nth-child(even) td{background:#f3f3f3;}
      .doc pre,.doc table,.doc th,.doc td{border-color:#bbb;}
    }";

        // -- helpers ----------------------------------------------------------------------------

        private static string EscapeHtml(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string JsonEscapeString(string s) => JsonSerializer.Serialize(s ?? string.Empty);
    }
}
