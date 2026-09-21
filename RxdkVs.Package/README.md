# RxdkVs.Package — RXDK for Visual Studio (VSIX)

A classic in-process **VSSDK** extension (.NET Framework 4.7.2) that ports the
[RXDK-VSCode](https://github.com/Team-Resurgent/RXDK-VSCode) original-Xbox dev experience to
**Visual Studio 2022 and 2026**. It is the `RxdkVs.Package/` component of the
[PLAN.md](../PLAN.md) port.

This folder is a **scaffold**: correct, idiomatic, complete source a developer can open in VS
and finish. It will not build without the VS SDK build tooling installed (see below).

## Integration model (why it's structured this way)

The VSIX is **.NET Framework** (it loads into the VS shell's AppDomain). The engine and debug
adapter are **net8** and therefore *cannot* be loaded in-proc — the package drives them as
**child processes**:

| Concern | Mechanism | Code |
|---|---|---|
| Build / Deploy / Run / Reboot / Set-IP | spawn `Rxdk.Cli.exe <verb> …`, stream to the **RXDK** Output pane, surface `error:`/gcc diagnostics to the **Error List** | `Services/CliRunner.cs`, `Commands/RxdkCommands.cs` |
| Debug (F5) | VS **Debug Adapter Host** launches `Rxdk.Dap.exe` (stdio DAP); `launch.vs.json` `type:"xbox"` routes to it | `RxdkVs.Package.pkgdef` |
| Project model | VS **Open Folder** mode; `rxdk.project.json` is the project marker; we generate `tasks.vs.json` / `launch.vs.json` / `CppProperties.json` | `Services/ProjectConfigGenerator.cs` |
| UI | RXDK **tool window** (WPF) + a top-level **RXDK menu** mirroring the VS Code command palette | `ToolWindow/`, `RxdkPackage.vsct` |

Both net8 exes are located through one helper, `Services/ToolLocator.cs`, which probes an env
override, a bundled `tools\` dir next to the VSIX, `%ProgramData%\RXDK\engine`, and finally the
dev build tree — so you can F5 the VSIX against a freshly-built CLI without packaging.

## Files

```
RxdkVs.Package.csproj          Old-style VSIX csproj (ProjectTypeGuids, VSSDK PackageReferences)
source.extension.vsixmanifest  VS 2022+2026 target range [17.0,19.0); metadata mirrors RXDK-VSCode
RxdkPackage.cs                 AsyncPackage: menu resource, tool window, autoload on rxdk.project.json
RxdkPackage.vsct               Command table: top-level RXDK menu + all buttons (command-palette parity)
RxdkPackage.pkgdef → RxdkVs.Package.pkgdef   Debug Adapter Host registration for "xbox" → Rxdk.Dap.exe
RxdkPackageGuids.cs            Single source of truth for all GUIDs
Commands/CommandIds.cs         Numeric command IDs (mirrored in the .vsct)
Commands/RxdkCommands.cs       Handler dispatcher; shells out via CliRunner; F5 delegates to VS debugger
Services/CliRunner.cs          Spawns Rxdk.Cli.exe, pipes to Output pane, parses diagnostics to Error List
Services/ToolLocator.cs        Resolves Rxdk.Cli.exe / Rxdk.Dap.exe + staged RXDK roots
Services/OpenFolderContext.cs  Resolves the open-folder root and its rxdk.project.json
Services/ProjectConfigGenerator.cs   Writes tasks.vs.json / launch.vs.json / CppProperties.json
ToolWindow/RxdkToolWindow.cs   Tool window pane
ToolWindow/RxdkToolWindowControl.xaml(.cs)   WPF UI: Xbox IP + Build/Deploy/Run/Debug/Reboot/New/Set-IP
Properties/AssemblyInfo.cs
Resources/                     extension-icon.png, RxdkCommands.png (placeholder icon strip)
LICENSE.txt
```

## Command parity with RXDK-VSCode

Every `contributes.commands` entry from RXDK-VSCode `package.json` has a corresponding button in
`RxdkPackage.vsct` / `Commands/CommandIds.cs` (Build, Deploy, Run, Debug, Warm Reboot, Remove DXT,
Set Xbox IP, New Project, New Prebuilt XBE, Complete Setup, Open SDK/Tools/Docs folders, SDK/Extension
docs, Fetch Latest SDK, Install .NET 8, Launch xbWatson/xbNeighborhood, Open Xbox Neighborhood,
Set Build Type, Settings). The `taskDefinitions` (`type:"rxdk"`) and `debuggers`
(`type:"xbox"`) contributions map to the generated `tasks.vs.json` / `launch.vs.json`.

## Building & testing

**Requires** the *"Visual Studio extension development"* workload (installs `Microsoft.VSSDK.BuildTools`
and the VS SDK reference assemblies). `dotnet build` is **not** supported for this project type.

```powershell
# From a Developer Command Prompt / Developer PowerShell for VS:
nuget restore RxdkVs.Package\RxdkVs.Package.csproj      # or msbuild -t:Restore
msbuild RxdkVs.Package\RxdkVs.Package.csproj /p:Configuration=Debug
# Output: bin\Debug\RxdkVs.Package.vsix
```

F5 (with the project set as startup, `DeployExtension=true`) launches the VS **experimental
instance** with the VSIX deployed. Then: **Open Folder** on an RXDK sample → the RXDK menu and
tool window appear → Build/Deploy/Run drive `Rxdk.Cli.exe`; F5 on the "xbox" launch config drives
`Rxdk.Dap.exe` through the Debug Adapter Host.

Point the package at your built engine while iterating:

```powershell
$env:RXDK_TOOLS_DIR = "D:\Git\RXDK-VS20XX\Rxdk.Cli\bin\Debug\net8.0"   # holds Rxdk.Cli.exe / Rxdk.Dap.exe
```

## What's stubbed / TODO for the human to finish

1. **Packaging the net8 exes** (`Services/ToolLocator.cs`): decide bundle-in-VSIX vs.
   download-at-runtime into `%ProgramData%\RXDK\engine`, then wire the final path. The `.pkgdef`
   currently points the adapter at `$PackageFolder$\tools\Rxdk.Dap.exe`.
2. **Debug Adapter Host registration** (`RxdkVs.Package.pkgdef`): the registry key shape can drift
   between VS versions — validate against
   <https://github.com/microsoft/VSDebugAdapterHost> and its
   [packaging wiki](https://github.com/Microsoft/VSDebugAdapterHost/wiki/Packaging-a-VS-Code-Debug-Adapter-For-Use-in-VS).
   The engine CLSID/ProgramProvider GUID in the AD7Metrics block is a placeholder if a full AD7
   shim is needed; the Debug-Adapter-Host `Configurations\xbox` block is the primary path.
3. **Autoload UI-context rule** (`RxdkPackage.cs`): verify `HierSingleSelectionName` fires in pure
   Open-Folder mode; if not, set the context imperatively via `IVsMonitorSelection.SetCmdUIContext`.
4. **New Project / New Prebuilt XBE wizards** (Phase 3): `NewProjectAsync` currently opens the tool
   window and points the user at manual `rxdk.project.json` creation. Add an `IVsTemplateWizard`
   flow over the `templates/` set that also calls `ProjectConfigGenerator.Generate`.
5. **Options page** (Phase 3): persist `rxdk.defaultConsole` and build type (`--optimize`) in a
   `DialogPage`. `SetBuildTypeAsync` / `OpenSettingsAsync` are placeholders.
6. **Remove DXT**: add a dedicated `remove-dxt` verb to `Rxdk.Cli` and call it (currently reboots).
7. **Command icons**: `Resources\RxdkCommands.png` is a placeholder colored-square strip — replace
   with real 16×16 icons (or switch the `.vsct` to `KnownMonikers`).
9. **ProjectConfigGenerator manifest parsing**: it parses `rxdk.project.json` with
   `System.Text.Json` directly (the net8 `Rxdk.Engine.Model` type can't be referenced from a
   Framework assembly). If a `netstandard2.0` build of the model is produced, swap in a direct
   deserialize.

## Notes / assumptions

- Output exe names taken from the existing projects: **`Rxdk.Cli.exe`**, **`Rxdk.Dap.exe`**
  (net8, `Rxdk.Cli`/`Rxdk.Dap` in the solution).
- `CppProperties.json` `includePath` targets `%ProgramData%\RXDK\sdk\include` — the staged SDK
  location the engine's `RxdkPaths` uses, matching `rxdk.stagedSdkPath`'s Windows default.
- VS SDK meta-package pinned to a 17.9.x build (VS 2022-era); it remains binary-compatible into
  the 18.x (VS 2026) range declared in the manifest. Bump if a newer API is needed.
- `RXDK-VS20XX.sln` and the sibling net8 projects were **not** modified (owned by another process).
```
