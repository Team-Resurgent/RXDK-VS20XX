# RXDK for Visual Studio

Build homebrew for the **original Xbox** in Visual Studio 2022 / 2026 against
[RXDK](https://github.com/Team-Resurgent/RXDK-Libs) — the open, self-contained,
MSVC-free Xbox SDK. The Visual Studio counterpart to the
[RXDK VS Code extension](https://github.com/Team-Resurgent/RXDK-VSCode).

## Features

- **Project templates** — Original Xbox Game, Empty, Lib, DXT, Controller Input,
  Font Scroller, Network Server, Video Player, and multi-project samples (Cube,
  Music Visualizer).
- **Native build / deploy / debug** — press **F5**: builds the `.xbe`, deploys it to
  the devkit over XBDM, and attaches the debugger. Deploy to Xbox and Remove DXT are
  on the project's right-click menu.
- **RXDK tool window** — set the devkit IP, warm-reboot, open the SDK / tools / docs
  folders, launch xbWatson and Xbox Neighborhood, browse docs, and manage installed
  components (installed-vs-available versions with per-component and *Update All* buttons).
- **One-click setup** — installs the host tools, SDK, docs, and LLVM toolchain into
  `%ProgramData%\RXDK`.
- **VS2003 project import** — bring a classic XDK `.vcproj` / `.sln` forward to RXDK.

## Getting started

1. Install the extension and restart Visual Studio.
2. Open the **RXDK** tool window (View ▸ Other Windows ▸ RXDK) and click
   **Install Prerequisites** to download the SDK, host tools, docs, and the LLVM toolchain.
3. **File ▸ New ▸ Project**, filter by the **Xbox** tag, and pick a template.
4. Set your devkit IP in the tool window, then press **F5** to build, deploy, and debug.

## Links

- Source: <https://github.com/Team-Resurgent/RXDK-VS20XX>
- Open SDK: <https://github.com/Team-Resurgent/RXDK-Libs>
- Samples: <https://github.com/Team-Resurgent/RXDK-Samples>
- Discord: <https://discord.gg/VcdSfajQGK>

Licensed under GPLv3.
