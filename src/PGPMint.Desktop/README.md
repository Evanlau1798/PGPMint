# PGPMint Desktop (Linux Avalonia)

Redesigned Avalonia 12.2.0 / .NET 10 desktop for Linux (`linux-x64` and `linux-arm64`), which also serves as the cross-platform managed and headless test harness. Windows ships its native WinUI 3 app in `src/PGPMint.App`; macOS ships its native SwiftUI/AppKit app + sidecar host in `src/PGPMint.MacApp` and `src/PGPMint.Host`.

The application accepts no password arguments. Linux and portable password vaults unlock with a masked password dialog (`SecretDialog`), share the CLI vault location (`Environment.SpecialFolder.LocalApplicationData/PGPMint` on Linux, `PGPMint-Portable` on Windows), and hold an exclusive `VaultSession` lock until the coordinator has stopped and disposed. The Windows validation build uses a separate directory from WinUI's DPAPI vault; existing formats are never silently converted. Back up the complete directory including the password header, and keep passphrase-protected OpenPGP `.asc` exports for portable recovery.

First launch presents a Setup Wizard to choose the target Key ID suffix, hardware GPU(s), and language. The main dashboard uses a compact 3-card layout (speed readout & honest sensor/tier badge, 4-tier selector with hover tooltips, unified Start/Stop button, progress & probability telemetry, and matched key list with Copy, `.asc` Export, Delete, and Clear All), plus a separate Settings window. Missing workload calibration runs automatically; Recalibrate invalidates enabled GPU calibrations and re-measures. Power tiers (`Eco`, `Balanced`, `Efficiency`, `Max`) unlock after calibration completes. Export requires a UID name or email and a nonempty passphrase, then two local `.asc` save locations; existing files are always rejected and decrypted seeds are zeroed in a `finally` block.

The system tray menu provides `Open`, `Start`/`Stop`, the `Power tier` submenu, and `Quit`. A working native menu and (on Linux) `org.kde.StatusNotifierWatcher` via `/usr/bin/gdbus` are required before hiding to tray; if the watcher disappears, the hidden window restores automatically. Without a working tray, closing during an active search prompts to stop and quit. Visible telemetry refreshes at most four times per second and pauses when hidden or minimized. MiniHUD and autostart are intentionally absent.

On Linux, active searches acquire silent sleep inhibition via `/usr/bin/systemd-inhibit --what=sleep --mode=block` and release/reap the child process on stop, lock, or quit without cluttering the dashboard header.

Build and test separately:

```powershell
dotnet build tests/PGPMint.Desktop.Tests/PGPMint.Desktop.Tests.csproj -c Release -p:Platform=x64
dotnet test tests/PGPMint.Desktop.Tests/PGPMint.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-build
```

Package Linux targets with the .NET 10 SDK (or macOS targets on a macOS host with Xcode Command Line Tools):

```bash
python scripts/packaging/package.py --rid linux-x64 --tag v1.2.0 --platform x64 --scratch /absolute/path/to/packages
python scripts/packaging/package.py --rid osx-arm64 --tag v1.2.0 --platform ARM64 --scratch /absolute/path/to/packages
```

Version source: [Avalonia 12.2.0](https://www.nuget.org/packages/Avalonia/12.2.0). Platform references: [Avalonia TrayIcon](https://docs.avaloniaui.net/controls/navigation/trayicon/), [systemd-inhibit](https://www.freedesktop.org/software/systemd/man/253/systemd-inhibit.html).
