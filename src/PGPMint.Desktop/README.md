# PGPMint Desktop

Avalonia 12.2.0 / .NET 10 desktop for Linux and macOS. Windows keeps its WinUI application; the Avalonia project can also run on Windows for managed and headless validation.

The application accepts no password arguments. New macOS vaults open automatically using a random secret in the user's login keychain; macOS may request system authorization. Existing password vaults and Linux use the masked password dialog. It shares the CLI vault location (`Environment.SpecialFolder.LocalApplicationData/PGPMint` on Linux/macOS, `PGPMint-Portable` on Windows) and holds `VaultSession` until the coordinator has stopped and disposed. The Windows validation build uses a separate directory from WinUI's DPAPI vault. Existing formats are not silently converted. Back up the complete directory including the password header and automatic-unlock metadata when present. A keychain backup alone does not provide portable recovery: also export passphrase-protected OpenPGP keys. Missing or inaccessible system secrets fail without replacing stored data.

Configure a target suffix and GPUs, apply settings, and start. Missing calibration runs automatically; Recalibrate invalidates enabled GPU calibration and starts calibration/search for the target. Four power tiers become available after calibration. Results show CPU-verified hits and saved keys separately. Export requires a UID name or email and a nonempty passphrase, then two local file selections. Existing files are always rejected, including after an overwrite prompt. Private files use Core's create-new API with owner-only Unix permissions; the decrypted seed is zeroed in a finally block.

The tray/menu bar provides Show, Hide, Pause, Resume, Stop and Quit. A working native menu and (on Linux) StatusNotifierWatcher are required before hiding; Linux also needs `/usr/bin/gdbus`. If the watcher disappears, the hidden window is restored. Without a tray, closing asks explicitly to stop and quit. Hiding leaves search running and skips UI refresh. Visible telemetry refreshes at most four times per second. MiniHUD and autostart are intentionally absent.

Sleep inhibition uses Linux `/usr/bin/systemd-inhibit` (readiness acknowledged after acquisition) or macOS IOKit assertions. Missing services/permissions are shown as unavailable. The Linux child is owned, closed and reaped on stop, pause, lock or quit. macOS inhibition prevents automatic idle sleep; closing the lid, manual sleep and low battery can still sleep the machine. The Avalonia Windows validation build currently displays sleep inhibition as unavailable; the Windows product remains WinUI.

Build and test separately:

```powershell
dotnet build tests/PGPMint.Desktop.Tests/PGPMint.Desktop.Tests.csproj -c Release -p:Platform=x64
dotnet test tests/PGPMint.Desktop.Tests/PGPMint.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-build
```

On Linux/macOS, package the selected RID with the .NET 10 SDK:

```bash
python scripts/packaging/package.py --rid linux-x64 --tag v0.0.0-test --platform x64 --scratch /absolute/path/to/packages
python scripts/packaging/package.py --rid osx-arm64 --tag v0.0.0-test --platform ARM64 --scratch /absolute/path/to/packages
```

Supported package targets: linux-x64, linux-arm64, osx-x64 and osx-arm64. macOS packaging requires a macOS host with Xcode Command Line Tools, verifies the native Metal bridge exists, and applies ad-hoc signing. macOS archives contain a `.app`; Linux archives contain the self-contained executable and libraries. Developer ID signing, notarization, distribution/installers and real hardware/platform qualification remain release work. Do not run the macOS archive on another machine as if it were notarized.

Version source: [official NuGet](https://www.nuget.org/packages/Avalonia/12.2.0). Platform behavior: [Avalonia tray documentation](https://docs.avaloniaui.net/controls/navigation/trayicon/), [systemd-inhibit](https://www.freedesktop.org/software/systemd/man/253/systemd-inhibit.html), [Apple idle sleep assertion](https://developer.apple.com/documentation/iokit/kiopmassertiontypepreventuseridlesystemsleep).
