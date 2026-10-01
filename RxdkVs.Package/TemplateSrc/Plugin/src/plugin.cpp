/*
 * Original Xbox plugin (.xbs) - RXDK DynamicLibrary template.
 *
 * A plugin is a PE DLL: a host program loads it at runtime and resolves its
 * exported functions by name. Which symbols are exported is controlled by
 * plugin.def (EXPORTS list) - edit that file to add or rename your entry points.
 *
 * NOTE: this builds a clean-room RXDK PE DLL. It is loadable by an RXDK-based
 * host; it is NOT ABI-compatible with MSVC/VC7.1-based hosts (different CRT and
 * loader contract), so it will not drop into an MSVC-built application as-is.
 */

#include <xtl.h>

/*
 * DLL entry symbol. The DynamicLibrary configuration points the linker's entry at
 * RxdkDllEntry (-Wl,--entry=RxdkDllEntry). A host's PE loader resolves the DLL's
 * exports directly and does not call this, so it only has to exist and resolve.
 * extern "C" + cdecl gives the undecorated symbol name the entry flag expects.
 */
extern "C" int RxdkDllEntry(void)
{
    return 1;
}

/*
 * Example exported entry point. The host resolves this by name; it is listed in
 * plugin.def so it (and only it) appears in the export table. Replace this with
 * your plugin's real entry point(s) and keep plugin.def in sync.
 */
extern "C" __declspec(dllexport) int PluginEntry(void)
{
    return 0;
}
