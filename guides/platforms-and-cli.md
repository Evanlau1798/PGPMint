[Back to README](../README.md) | [Benchmarks](benchmarks.md) | [Architecture & Build](architecture-and-build.md)

# Platforms, Security & CLI Reference

## Desktop Applications by Platform

### Windows (`src/PGPMint.App`)

- **Packaging & Runtime**: Native **WinUI 3 + Mica** desktop application distributed as a standalone compressed single-file executable (`win-x64` and `win-arm64`), requiring no Python, CUDA Toolkit, .NET Runtime, or Windows App Runtime installation.
- **Universal GPU Compute Cascade**: Zero-dependency three-tier compute cascade running via native Vulkan Compute (`vulkan-1.dll` + programmatic SPIR-V 1.3, `vulkan-sha1-four-v5`) for peak sustained throughput, cascading to native `OpenCL.dll` (`opencl-sha1-four-v5`), and falling back to DirectCompute (`D3D11 cs_5_0`, `d3d11-sha1-four-v5`) on any Direct3D 11 Feature Level 11.0+ GPU (including Windows on ARM). Supports multi-GPU parallel search while excluding CPU software renderers and virtual display adapters.
- **Dashboard & Power Tiers**: Setup Wizard, minimalist dashboard with live hash rate, smooth progress bar, cumulative hit probability (hover for single-hash odds; switches to `Found` once matched), scientific-notation checked count, live ETA, adaptive results card (up to 5 keys displayed inline), and separate Settings window. Measures a 30-second full efficiency curve per workload group and GPU configuration, supporting live switching across `Eco`, `Balanced`, `Efficiency`, and `Max` with elastic yielding to competing 3D apps.
- **System Tray & Mini HUD**: Closing the main window minimizes PGPMint to the notification area so WinUI 3 stops consuming GPU rendering cycles. An optional pure-CPU Direct2D/DirectWrite floating Mini HUD tracks live speed, probability, ETA, and progress, and auto-hides during fullscreen apps/games.
- **Local Vault & Localization**: Stores settings (`state.json`) and matched seeds (`hits.vault.json`) encrypted via Windows DPAPI (`CurrentUser`) under `%LOCALAPPDATA%\PGPMint\`. Ships 10 built-in languages and supports loading custom `Strings/*.json` locale files placed next to `PGPMint.exe`.

### macOS (`src/PGPMint.MacApp` + `src/PGPMint.Host`)

- **Native UI + Isolated Sidecar Architecture**: Native **SwiftUI + AppKit** desktop app (`macOS 14+`, `macos-arm64` and `macos-x64`) backed by a bundled self-contained `.NET 10` sidecar helper (`pgpmint-host`) communicating over private anonymous `stdin`/`stdout` NDJSON pipes. Private key seeds never cross IPC to the UI process, and closing `stdin` stops GPU workers and releases the exclusive `VaultSession`.
- **Signing & First Launch**: Release and CI macOS packages are ad-hoc signed, not Developer ID signed or notarized. First launch may require allowing execution in **macOS System Settings -> Privacy & Security**.
- **Keychain & Hardware Backends**: Supports Login Keychain automatic unlock for new vaults (while preserving password unlock for existing password vaults), menu bar live hash rate readout with 4-tier switching, `IOKit` idle sleep assertion (`System sleep is inhibited while searching`), native **Metal** (`libpgpmint_metal.dylib`), and **MoltenVK** Vulkan backends. GPU availability depends on the physical Mac hardware.

### Linux (`src/PGPMint.Desktop`)

- **Avalonia Desktop UI**: Redesigned **Avalonia 12.2.0** XAML desktop app (`linux-x64` and `linux-arm64`) featuring a Setup Wizard, minimalist 3-card dashboard, separate Settings window, 4 calibrated power tiers (`Eco`, `Balanced`, `Efficiency`, `Max`) with hover tooltips, and native NVIDIA NVML (`libnvidia-ml.so.1`) live board power (`W`) and `MH/W` telemetry.
- **System Integration**: Uses silent `/usr/bin/systemd-inhibit --what=sleep --mode=block` during active searches (cleanly reaped on stop, lock, or quit) and `org.kde.StatusNotifierWatcher` via `/usr/bin/gdbus` for tray integration. If the watcher disappears, a hidden window restores automatically; without a working tray, closing during an active search prompts to stop and quit. Visible telemetry refreshes at most 4 times per second and pauses when hidden or minimized. Mini HUD and autostart are absent by design.
- **Graphics & Compute Requirements**: Requires an X11 or XWayland session and working hardware Vulkan 1.1+ or OpenCL ICD drivers plus standard desktop runtime libraries (see `TESTING.txt` inside the release archive). Bundling .NET does not replace GPU drivers; software CPU renderers are excluded. WSL is treated as a Linux testing environment rather than a supported GPU target.

---

## Vault Security, Permissions & Backup Rules

- **Completely Offline / Zero Telemetry**: PGPMint makes no network requests.
- **CPU Double Verification**: Every candidate timestamp returned by any GPU backend is re-hashed and verified on the CPU with `System.Security.Cryptography.SHA1` before persistence. Decrypted private seeds are zeroed in memory inside `finally` blocks after key rotation or export.
- **Vault Capacity & Corrupt Safeguard**: Each vault holds up to 50 records. When full, minting stops while leaving existing records intact (export keys you want to keep before deleting entries). If `hits.vault.json` cannot be parsed, PGPMint creates a `.corrupt.<timestamp>.bak` backup and refuses to overwrite stored keys.
- **Vault Formats & Default Data Directories**:
  - **Windows WinUI (`PGPMint.App`)**: Uses Windows DPAPI (`CurrentUser`) in `%LOCALAPPDATA%\PGPMint\`.
  - **CLI, Linux (`PGPMint.Desktop`), & Windows Portable Validation**: Use Argon2id + AES-256-GCM password vaults in `$XDG_DATA_HOME/PGPMint` / `~/.local/share/PGPMint` on Linux, or `%LOCALAPPDATA%\PGPMint-Portable\` on Windows (separate from the WinUI DPAPI directory). Existing formats are never silently converted, and an exclusive `VaultSession` file lock prevents simultaneous desktop + CLI access to the same directory.
  - **macOS (`PGPMint.MacApp`)**: Stores data under `~/Library/Application Support/PGPMint`. New macOS desktop vaults store a random unlock secret in the user's Login Keychain (no password prompt needed), while existing password vaults stay password-protected.
- **Backup & Recovery**: Always back up the complete vault directory, including `vault.key.json` and `vault.unlock.json` when present. macOS keychain vault backups also require preserving the original Login Keychain secret; PGPMint refuses to replace missing or inaccessible keychain secrets. Export passphrase-protected OpenPGP `.asc` files for portable cross-machine recovery.
- **Unix `0700` & macOS ACL Enforcement**: Use a dedicated vault directory. On Unix, an existing custom directory must already have mode `0700`—PGPMint refuses broader permissions without modifying them (only PGPMint's fixed default directory may have legacy cache permissions tightened). On macOS, private vault and export paths with explicit or inherited ACL entries are refused without altering parent ACLs.

---

## CLI Reference (`src/PGPMint.Cli`)

On Windows, run `PGPMint-cli-*-win-x64.exe` (or `win-arm64`). On macOS and Linux, run `./cli/pgpmint` inside the extracted release archive. CLI usage does not require a desktop graphical session, and CLI output stays in English for consistent scripting and diagnostics.

```sh
# Enumerate detected GPUs and backends as JSON
./pgpmint devices --json

# Verify GPU SHA-1 results against CPU reference
./pgpmint verify --backend vulkan       # Windows, Linux Vulkan, or macOS MoltenVK
./pgpmint verify --backend metal        # macOS native Metal

# Run isolated microbenchmarks + coordinator check (disposable data; never opens vault)
./pgpmint benchmark --backend vulkan --seconds 60 --rounds 3

# Sweep 21-point power efficiency curve (0%-100%)
./pgpmint curve --backend opencl --pattern BEEF

# Search with pattern/wildcards, thermal guard, continuous mode, and hit hook
./pgpmint search --pattern "CAFE*123" --tier efficiency --max-temp 80 \
  --keep-running --status-interval 5 --json --on-hit-exec ./notify.sh

# Search with custom UTC key creation timestamp bounds (>= 7 days span)
./pgpmint search --pattern DEAD************ --since 2025-01-01T00:00:00Z --until 2026-01-01T00:00:00Z

# Export / import idempotent cross-machine .pgpmint-sync bundles
./pgpmint sync export --output ./bundle.pgpmint-sync
./pgpmint sync import --input ./bundle.pgpmint-sync

# Export GnuPG/Git-ready ASCII-armored keypair (includes Cv25519 encryption subkey by default)
./pgpmint export --key YOUR_KEY_ID --name "Your Name" --email "you@example.com" --output ./exported
./pgpmint export --key YOUR_KEY_ID --name "Your Name" --no-encrypt-subkey --expires 2028-12-31 --output ./exported
```

### Password, Automation & Diagnostics Rules

- **Interactive & Automated Passwords**: Interactive prompts suppress terminal echo. For automation, pass `--password-stdin`: `search`, `sync`, and password-vault commands read the vault password on line 1; `export` reads the vault password on line 1 and the OpenPGP key passphrase on line 2 (for macOS keychain vaults, `export` consumes line 1 directly as the OpenPGP passphrase). Never pass passwords as CLI arguments or print them into logs.
- **macOS Keychain Headless Note**: Unlocking an existing macOS keychain vault requires an unlocked desktop login session for the owning user and may trigger a system authorization prompt.
- **Stop & Save Behavior**: `search` stops after its first saved match by default unless `--keep-running` is specified. Pressing `Ctrl+C` stops gracefully after the active batch and persists progress.
- **Diagnostic Scope**: `devices` reports hardware discovery only and does not verify correctness; `verify` checks GPU outputs against CPU SHA-1; `benchmark` reports microbenchmarks separately from `MultiGpuCoordinator` runs and never touches your user vault. Standard CI runs verify compilation and CPU unit tests, not physical GPU throughput.
