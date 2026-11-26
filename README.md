<div align="center">

# PGPMint

English | [繁體中文](README.zh-TW.md) | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

**GPU generator for custom OpenPGP v4 (`Ed25519`) vanity Key IDs, with native Windows, native macOS, and redesigned Linux desktop apps plus a cross-platform CLI.**

[![CI](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml/badge.svg)](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Evanlau1798/PGPMint?label=Release&color=0078D6)](https://github.com/Evanlau1798/PGPMint/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Windows%20%7C%20macOS%20%7C%20Linux-x64%20%7C%20ARM64-0078D6.svg)](#system-requirements)
[![.NET 10](https://img.shields.io/badge/.NET%2010%20%2B%20SwiftUI%20%2B%20Avalonia-512BD4.svg)](#building-from-source)

[Download](#download--quick-start) · [Features](#features) · [Git Signing](#using-exported-keys-with-git) · [Benchmarks](#benchmarks) · [Build from Source](#building-from-source)

<br />

<table>
  <tr>
    <td align="center" width="33%">
      <img src="assets/screenshot-dashboard.png" alt="PGPMint on Windows 11 running at 23.61 GH/s" width="360" /><br />
      <sub><strong>Windows 11 (WinUI 3 + Mica)</strong></sub>
    </td>
    <td align="center" width="33%">
      <img src="assets/screenshot-macos-dashboard.png" alt="PGPMint native SwiftUI/AppKit app on macOS running at 2.25 GH/s" width="360" /><br />
      <sub><strong>macOS (Native SwiftUI / AppKit)</strong></sub>
    </td>
    <td align="center" width="33%">
      <img src="assets/screenshot-linux-dashboard.png" alt="PGPMint redesigned Avalonia app on Linux running on Intel UHD Vulkan" width="360" /><br />
      <sub><strong>Linux (Avalonia 12.2.0 / X11)</strong></sub>
    </td>
  </tr>
</table>

*PGPMint across Windows, macOS, and Linux searching custom OpenPGP v4 Key ID suffixes with calibrated power tiers.*

</div>

---

## What is PGPMint?

PGPMint lets you mint custom **OpenPGP v4 (`Ed25519Legacy`)** keys whose 16-character Key ID ends with any hexadecimal pattern you choose (for example `6767 6767 6767` or `0123 4567 89AB`).

Unlike command-line tools that require installing Python or the NVIDIA CUDA Toolkit—or browser tabs that stall in the background—PGPMint ships as **standalone single-file Windows executables** (GUI + CLI) alongside self-contained **macOS (`macos-arm64` / `macos-x64`)** and **Linux (`linux-x64` / `linux-arm64`)** bundles with power-efficiency tiers, tray/menu bar keep-alive, and ready-to-sign `.asc` export.

---

## Features

### macOS and Linux desktop builds

The Windows WinUI application (`src/PGPMint.App`) remains the flagship Windows product, now updated alongside macOS and Linux with the unified PGPMint brand icon (`assets/pgpmint-icon.svg`).

- **macOS (`src/PGPMint.MacApp` + `src/PGPMint.Host`)**: Native **SwiftUI + AppKit** desktop app (`macOS 14+`, `macos-arm64` and `macos-x64` packages) backed by a bundled self-contained `.NET 10` sidecar helper (`pgpmint-host`) communicating over private anonymous `stdin`/`stdout` NDJSON pipes. Private key seeds never cross IPC to the UI process, and closing `stdin` stops GPU workers and releases the exclusive `VaultSession`. Supports Login Keychain automatic unlock for new vaults (keeping password unlock for existing vaults), menu bar live hash rate readout with 4-tier switching, `IOKit` idle sleep assertion (`System sleep is inhibited while searching`), native Metal (`libpgpmint_metal.dylib`) and MoltenVK Vulkan drivers.
- **Linux (`src/PGPMint.Desktop`)**: Redesigned **Avalonia 12.2.0** XAML desktop app (`linux-x64` and `linux-arm64`) featuring a Setup Wizard, minimalist 3-card dashboard, separate Settings window, 4 calibrated power tiers (`Eco`, `Balanced`, `Efficiency`, `Max`) with per-tier hover tooltips and native NVIDIA NVML (`libnvidia-ml.so.1`) live board power and `MH/W` telemetry, silent `/usr/bin/systemd-inhibit` sleep prevention during active search, `StatusNotifierWatcher` system tray integration, and native Vulkan/OpenCL compute backends. Software CPU renderers are excluded; WSL is a Linux testing environment rather than a supported GPU target.

Download the platform test packages from the successful **CI** run on `main` in
[GitHub Actions](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml).
These are test artifacts, not a published release. Each package contains a
self-contained CLI; Linux/macOS packages also contain the desktop app. The Mac
package includes its Metal bridge and MoltenVK. macOS test apps are ad-hoc signed,
not Developer ID signed or notarized.

Linux desktop builds require X11 or XWayland and the native libraries listed in
the package instructions. CLI testing does not require a desktop session. A
working hardware Vulkan/OpenCL driver is required; bundling .NET does not replace
GPU drivers. macOS GPU availability depends on the host, including on CI runners.

```sh
# Use the pgpmint executable in the extracted CLI directory.
./pgpmint devices --json
./pgpmint verify --backend vulkan       # Linux, or MoltenVK on macOS
./pgpmint verify --backend metal        # macOS
./pgpmint benchmark --backend vulkan --seconds 60 --rounds 3
./pgpmint search --pattern BEEF
./pgpmint export --key YOUR_KEY_ID --name "Your Name" --output ./exported
```

The CLI prompts for passwords without echoing them. Existing macOS keychain vaults
unlock through the original user's login keychain; this requires an unlocked
desktop login session and may request system authorization. For automation,
`--password-stdin` reads the vault password from standard input; export reads a
second line for the OpenPGP passphrase. A keychain vault consumes only the export
passphrase line. Never pass a password as an argument or
print it in a script log. Search stops after its first saved match by default;
Ctrl+C stops safely and saves progress.

New macOS desktop vaults store a random unlock secret in the login keychain, so
no extra vault password is needed. Existing password vaults remain password
protected; new CLI and Linux vaults use a user password for headless operation.
All portable vaults use Argon2id and AES-256-GCM and user-only file permissions.
Keep the complete data directory when backing up, including `vault.key.json` and
`vault.unlock.json` when present. Keychain vault backups also need the original
keychain secret; export passphrase-protected OpenPGP keys for portable recovery.
PGPMint refuses to replace missing or inaccessible keychain secrets.
Use a dedicated vault directory. On Unix, an existing custom directory
must already have mode `0700`; PGPMint refuses broader permissions without changing
the directory. Explicitly secure your dedicated vault directory before reopening it.
Only PGPMint's fixed default directory may have legacy cache permissions tightened.
On macOS, private vault and export paths with explicit or inherited ACL entries
are refused; choose a dedicated location without ACLs. Parent ACLs are never changed.
The desktop and CLI exclude simultaneous sessions using the
same data directory. Windows WinUI keeps its existing DPAPI vault; it is not
silently converted into a password vault. The cross-platform CLI uses a password
vault and refuses to initialize over an existing vault missing its password header.
On Windows, the CLI and Avalonia validation app default to
`%LOCALAPPDATA%\PGPMint-Portable`, separately from the WinUI DPAPI directory.

`verify` checks GPU results against CPU SHA-1. `devices` only reports discovery;
it does not claim GPU correctness. Standard CI builds and unit tests do not prove
hardware throughput. Benchmark reports microbenchmarks separately from full
coordinator measurements, uses disposable isolated data, and does not open the
user's vault. Compare identical devices, workloads, modes and UI states.

The redesigned desktop apps support 10 built-in languages (`English`, `繁體中文`, `简体中文`, `日本語`, `한국어`, `Deutsch`, `Français`, `Español`, `Português (Brasil)`, and `Русский`), encrypted vault storage, `.asc` export, GPU selection, automatic workload calibration, four power tiers with hover tooltips, and background operation where a working tray or menu bar is available. The CLI stays in English for consistent scripting and diagnostics, and the floating Mini HUD remains Windows-only.
Hardware validation and sustained performance on each Mac/GPU family remain
separate acceptance checks.

### Windows application

- **Single-File Portable Executable** — Self-contained compressed `.exe` for `win-x64` and `win-arm64`. No Python, CUDA Toolkit, .NET Runtime, or Windows App Runtime installation required.
- **Universal Windows Hardware GPU Engine** — Zero-dependency three-tier compute cascade: runs via native Vulkan Compute (`vulkan-1.dll` + programmatic SPIR-V 1.3, `vulkan-sha1-four-v4`) when a Vulkan 1.1+ compute GPU is present for peak sustained throughput, cascades to native `OpenCL.dll` (`opencl-sha1-four-v4`) when Vulkan is unavailable, and falls back transparently to DirectCompute (`D3D11 cs_5_0`, `d3d11-sha1-four-v4`) on any Direct3D 11 Feature Level 11.0+ GPU (including Windows on ARM). Supports multi-GPU parallel search while excluding CPU software renderers and virtual display adapters.
- **Measured Speed & 4 Power-Efficiency Tiers**: PGPMint measures a 30-second full efficiency curve when the current workload path (`1`-`4` hex individually, `5`-`8` hex low-word group, `9`-`16` hex 64-bit group) or GPU configuration changes. Batch sizes adapt automatically. Once calibrated, switch live between `Eco`, `Balanced`, `Efficiency`, and `Max` with per-tier hover rules and elastic yielding to competing 3D apps.
- **Zero-Overhead System Tray & Pure-CPU Floating HUD** — Closing the main window minimizes PGPMint to the notification area so WinUI 3 stops consuming GPU rendering cycles. An optional pure-CPU Direct2D/DirectWrite floating Mini HUD tracks live speed, probability, ETA, and progress (auto-hides during fullscreen apps/games).
- **Minimalist Dashboard & Separate Settings** — Live hash rate, smooth progress bar, cumulative hit probability (hover for single-hash odds; switches cleanly to `Found` once a matching key is stored), scientific-notation checked count, live ETA, and an adaptive results card that expands for up to 5 saved keys.
- **DPAPI Encrypted Vault & Signing-Ready `.asc` Export** — Every GPU match is double-verified on CPU, encrypted locally using Windows DPAPI (`CurrentUser`), and exported as RFC 9580 / GnuPG-compatible `.secret.asc` and `.public.asc` files with `Certify | Sign` key flags and optional AES-256-CFB passphrase protection.
- **Extensible Localization** — Built-in support for 10 languages (`English`, `繁體中文`, `简体中文`, `日本語`, `한국어`, `Deutsch`, `Français`, `Español`, `Português (Brasil)`, and `Русский`), plus automatic loading of custom `Strings/*.json` locale files placed next to `PGPMint.exe`.

---

## Download & Quick Start

1. Download the package for your platform from [GitHub Releases](https://github.com/Evanlau1798/PGPMint/releases):
   - **Windows GUI**: `PGPMint-*-win-x64.exe` or `PGPMint-*-win-arm64.exe` (single portable `.exe`)
   - **Windows CLI**: `PGPMint-cli-*-win-x64.exe` or `PGPMint-cli-*-win-arm64.exe` (single portable `.exe`)
   - **macOS**: `PGPMint-*-macos-arm64.tar.gz` or `PGPMint-*-macos-x64.tar.gz` (`PGPMint.app` + CLI)
   - **Linux**: `PGPMint-*-linux-x64.tar.gz` or `PGPMint-*-linux-arm64.tar.gz` (`PGPMint.Desktop` + CLI)
2. **Initial Setup**: Enter your desired 1-16 hex Key ID suffix (`0-9`, `A-F`, e.g. `01 2345 6789`), choose hardware GPU(s), select your language, and finish setup.
3. **Automatic Calibration & Search**: Start searching with a small batch. PGPMint runs a 30-second curve calibration when needed, and can still save matches during calibration. Once calibrated, switch between `Eco`, `Balanced`, `Efficiency`, and `Max`. Press **Stop** to finish the active batch and save progress, or close the window to continue in the tray / menu bar.

4. **Export Your Key**: When a match appears in the results panel, click `.asc` to export `<KEYID>.secret.asc` and `<KEYID>.public.asc`.

**Stop** works during calibration and search, including from the tray. PGPMint finishes the active batch and can save its verified match before returning to idle. If a GPU becomes unavailable, PGPMint tries to reopen it once and resumes with a small batch. Incomplete batches are excluded from search progress.

Existing vault records and preferences load when upgrading. Calibration is measured again when the saved profile lacks a workload identity or the device, driver, backend, kernel configuration, or workload execution group (`1`-`4` hex individually, `5`-`8` hex low-word, `9`-`16` hex 64-bit) has changed.

---

## Using Exported Keys with Git

Exported keys include a self-signed User ID with `Certify | Sign` (`0x03`) flags and work directly with GnuPG, Git, and GitHub Verified commits:

```powershell
# 1. Import your minted secret key into GnuPG
gpg --import <KEYID>.secret.asc

# 2. Configure Git to sign commits with your new Key ID
git config --global user.signingkey <KEYID>
git config --global commit.gpgsign true

# 3. Paste <KEYID>.public.asc into GitHub -> Settings -> SSH and GPG keys
```

---

## Benchmarks

The historical v1 baseline below was measured on an **NVIDIA GeForce RTX 3080** (Windows 11 x64). It includes host Ed25519 key generation, SHA-1 schedule precomputation, GPU dispatch, and readback.

| Backend | Kernel | Measured Throughput | Notes |
|---|---|---:|---|
| Native OpenCL (`OpenCL.dll`) | `opencl-sha1-four-v1` (4-key, unroll 2) | **21.06 GH/s** (tray / Mini HUD) / **20.00 GH/s** (foreground) | Default when OpenCL GPU ICD is available |
| DirectCompute (`D3D11 cs_5_0`) | `d3d11-sha1-four-v1` (4-key) | **13.51 GH/s** | Automatic fallback on non-OpenCL GPUs |

The v3 kernels introduced in `ad24a05` use four lanes, up to 128 threads per workgroup, and unroll 2. Both passed fingerprint oracle tests on the RTX 3080 with WDDM driver `32.0.16.1088` and OpenCL ICD `610.88`. In paired FindOne tests of 8, 9, and 16 hex digits at 134,217,728 candidates per batch, v3 median host wall times were 6.97–7.44 ms on OpenCL and 10.40–10.74 ms on DirectCompute. These measurements cover dispatch and blocking readback with warmed device sessions. The 64- and 256-thread alternatives showed no consistent gain. AMD, Intel, and native ARM64 GPU coverage is pending.

The current **v4 engine** eliminates constant-cache register spill (workgroup 128, outer unroll 1), places key schedules and phase constants in constant memory / UBOs, extends cross-lane sharing to rounds `0..6`, reconstructs timestamp XORs minimally, folds `-K3` into `targetLowNoK3` to defer `rotl(b, 30)` and round `76` out of the hot path, and runs via three zero-dependency backends (`vulkan-1.dll` + programmatic SPIR-V 1.3, `OpenCL.dll`, and `D3D11 cs_5_0`). Paired RTX 3080 (`32.0.16.1088`, Release x64, 16 warmup + 31 timed FindOne dispatches, 8-hex and 16-hex suffixes) and 5-second end-to-end `MultiGpuCoordinator` + `KeySchedulePool` measurements at `MAX` throttle:

| Backend | Kernel | 134M Batch Median | 536M Batch Median | End-to-End Coordinator (`MAX`) |
|---|---|---:|---:|---:|
| Native Vulkan (`vulkan-1.dll`) | `vulkan-sha1-four-v4` (`ubo-spirv-v4`) | **5.63–6.12 ms** (`21.93–23.82 GH/s`) | **23.11–23.14 ms** (`23.20–23.24 GH/s`) | **23.23 GH/s** |
| Native OpenCL (`OpenCL.dll`) | `opencl-sha1-four-v4` (`cmem0-v4`) | **5.68–6.27 ms** (`21.42–23.61 GH/s`) | **23.78–23.92 ms** (`22.44–22.58 GH/s`) | **23.19 GH/s** |
| DirectCompute (`D3D11 cs_5_0`) | `d3d11-sha1-four-v4` (`cb1-v4`) | **9.14–9.44 ms** (`14.21–14.69 GH/s`) | **37.33–37.38 ms** (`14.36–14.38 GH/s`) | Fallback tier |

On the same RTX 3080 under concurrent dual-stream NVDEC hardware video decoding (3%–11% decoder load), counterbalanced 2-round `MultiGpuCoordinator` averages held **21.35 GH/s** (`-8.08%`) on Vulkan vs **18.98 GH/s** (`-18.14%`) on OpenCL, making Vulkan the default Tier-1 backend.

### NVIDIA GeForce RTX 5090 (Linux x64, 450 W Enforced Cap)

Measured on an **NVIDIA GeForce RTX 5090** (32 GiB VRAM, Ubuntu 22.04.5 LTS, Driver `595.91.07`, enforced **450.00 W** cap vs `575.00 W` default) using `PGPMint v1.2.1` + native Linux NVML (`libnvidia-ml.so.1`) on the 13-hex suffix `***1145141919810` (see [Acknowledgements & Cloud GPU Warning](#acknowledgements--cloud-gpu-usage-notice)).

| Device | Backend | Kernel / Workgroup | Micro Median | End-to-End (`MAX`) | Notes |
|---|---|---|---:|---:|---|
| **RTX 5090 (450 W)** | **OpenCL** | `opencl-sha1-four-v4` / `64` | **83.93 GH/s** | **76.63 GH/s** | Peak micro throughput |
| **RTX 5090 (450 W)** | **OpenCL** | `opencl-sha1-four-v4` / `128` | **83.32 GH/s** | **76.18 GH/s** | CLI `search` default (`cmem0-v4`) |
| **RTX 5090 (450 W)** | **OpenCL** | `opencl-sha1-four-v4` / `256` | 81.60 GH/s | 75.97 GH/s | — |
| RTX 5090 (450 W) | Vulkan | `vulkan-sha1-four-v4` / `64`–`256` | 56.75–57.42 GH/s | 54.86–55.03 GH/s | Headless EGL ICD (`nvidia_egl_icd.json`) |
| RTX 3090 | OpenCL | `opencl-sha1-four-v4` / `128` | — | 24.97 GH/s | Earlier Linux container calibration |
| RTX 3080 (Windows) | Vulkan | `vulkan-sha1-four-v4` / `128` | 23.20–23.82 GH/s | 22.51–23.23 GH/s | `346.61 W` (`64.95 MH/W` full load) |

<p align="center">
  <img src="assets/rtx5090-power-curve.svg" alt="PGPMint RTX 5090 throughput, board power, and MH/W curve across 0% to 100% target rate" width="100%" />
</p>

<details>
<summary><strong>Complete 21-Point Settled RTX 5090 Power-Efficiency Curve &amp; 180s Stability Data (OpenCL, 4x128)</strong></summary>

Each point (`100%` `FullSpeed`, `95%`–`5%` under PGPMint 50 ms duty-cycle pacing) settled for 3 s before 5 s steady-state NVML measurement (`0%` = 5 s idle). All search steps counted directly toward persisted progress.

| Target Load | Throughput (GH/s) | Median Power (W) | Yield (MH/W) | GPU Util (%) | Core / Mem Clock (MHz) | Temp (C) | P-State | Power Cap Flags |
|---:|---:|---:|---:|---:|---|---:|:---:|---|
| **100%** | **77.671** | **449.99** | **172.61** | 100 | 2460 / 13801 | 67 | P1 | `SwPowerCap(50/50)` |
| **95%** | 72.912 | 449.94 | 162.05 | 89 | 2610 / 13801 | 69 | P1 | `SwPowerCap(51/51)` |
| **90%** | 69.595 | 450.05 | 154.64 | 85 | 2640 / 13801 | 71 | P1 | `SwPowerCap(52/52)` |
| **85%** | 66.138 | 450.22 | 146.90 | 78 | 2685 / 13801 | 72 | P1 | `SwPowerCap(52/52)` |
| **80%** | 62.075 | 434.58 | 142.84 | 74 | 2707 / 13801 | 73 | P1 | `SwPowerCap(51/51)` |
| **75%** | 58.264 | 414.47 | 140.57 | 69 | 2715 / 13801 | 72 | P1 | `SwPowerCap(52/52)` |
| **70%** | 54.541 | 395.45 | 137.92 | 65 | 2722 / 13801 | 72 | P1 | `SwPowerCap(52/52)` |
| **65%** | 50.677 | 375.36 | 135.01 | 60 | 2737 / 13801 | 72 | P1 | `SwPowerCap(36/52)` |
| **60%** | 46.848 | 355.71 | 131.70 | 55 | 2745 / 13801 | 71 | P1 | `SwPowerCap(15/52)` |
| **55%** | 42.859 | 335.41 | 127.78 | 51 | 2752 / 13801 | 71 | P1 | `SwPowerCap(5/52)` |
| **50%** | 39.055 | 315.02 | 123.98 | 46 | 2767 / 13801 | 69 | P1 | `None` |
| **45%** | 35.103 | 291.53 | 120.41 | 40 | 2767 / 13801 | 70 | P1 | `None` |
| **40%** | 31.205 | 258.84 | 120.56 | 35 | 2580 / 13801 | 67 | P1 | `None` |
| **35%** | 27.370 | 235.32 | 116.31 | 32 | 2565 / 13801 | 65 | P1 | `None` |
| **30%** | 23.404 | 213.20 | 109.78 | 27 | 2550 / 13801 | 64 | P1 | `None` |
| **25%** | 19.584 | 190.85 | 102.61 | 23 | 2542 / 13801 | 61 | P1 | `None` |
| **20%** | 15.601 | 169.27 | 92.17 | 19 | 2527 / 13801 | 60 | P1 | `None` |
| **15%** | 11.745 | 148.12 | 79.30 | 13 | 2520 / 13801 | 58 | P1 | `None` |
| **10%** | 7.832 | 126.09 | 62.12 | 9 | 2505 / 13801 | 57 | P1 | `None` |
| **5%** | 3.879 | 104.79 | 37.02 | 5 | 2497 / 13801 | 55 | P1 | `None` |
| **0% (Idle)** | 0.000 | 28.32 | — | 0 | 195 / 405 | 52 | P8 | `None` |

- **Why `100%` (`Max`) peaks at `172.61 MH/W`**: Under the 450 W power limit, continuous full load settles around `2460 MHz` with ~2.66x the RTX 3080's per-watt yield (`64.95 MH/W`). Below `100%`, PGPMint's 50 ms duty-cycle pacing keeps GDDR7 at `13801 MHz` in `P1` (~105 W static floor at 5%), while `85%–95%` short bursts boost higher (`2610–2685 MHz`) and still saturate `450 W`. At `40%`, the core drops from `2767` to `2580 MHz`, cutting `32.7 W` and forming a slight efficiency knee (`120.56 MH/W`).
- **Live 4-Tier Validation (5 s settle + 10 s `MultiGpuCoordinator`)**: `Max` `76.680 GH/s @ 449.99 W` (`170.40 MH/W`), `Efficiency` (`62.6%`) `49.743 GH/s @ 362.77 W` (`137.12 MH/W`), `Balanced` (`31.3%`) `24.242 GH/s @ 217.69 W` (`111.36 MH/W`), `Eco` (`7.7%`) `6.136 GH/s @ 115.89 W` (`52.95 MH/W`).
- **180-Second Sustained `Max` Stability**: Mean **`74.695 GH/s`** (median **`74.726 GH/s`**, UI rolling window `76.1 -> 74.4 GH/s`), mean power **`448.71 W`** (**`166.47 MH/W`**), temperature stabilizing `70 C -> 80 C` (`0/180` thermal throttling, `179/180` `SwPowerCap`).

</details>

### Expected Average Wait Time (at 20 GH/s)

| Suffix Length | Search Space ($16^k$) | Expected Average Time |
|---:|---:|---:|
| 8 hex chars | $4.29 \times 10^{9}$ | ~0.2 s |
| 10 hex chars | $1.10 \times 10^{12}$ | ~55 s |
| 12 hex chars | $2.81 \times 10^{14}$ | ~3.9 hours |
| 13 hex chars | $4.50 \times 10^{15}$ | ~2.6 days |
| 14 hex chars | $7.21 \times 10^{16}$ | ~41.7 days |

### Comparison with Alternatives

| Capability | PGPMint | CLI CUDA Tools | CLI OpenCL Tools | Browser WebGL/WebGPU |
|---|---|---|---|---|
| Native Windows 11 UI + System Tray | Yes (WinUI 3 + Mica) | No | No | Browser tab (throttled in background) |
| Zero Python / CUDA Toolkit Setup | Yes (single `.exe`) | Requires CUDA | Requires runtime | Yes |
| Universal Windows GPU Fallback | Vulkan + OpenCL + DirectCompute | NVIDIA only | Needs OpenCL ICD | Browser dependent |
| Multi-GPU + Power-Efficiency Tiers | Yes (`Eco` / `Balanced` / `Efficiency` / `Max`) | Full speed only | Full speed only | Limited |
| Encrypted Local Vault + Git-Ready `.asc` | Yes (DPAPI + self-sig) | Raw export | Raw export | Manual download |

---

## System Requirements

- **Windows**: Windows 10 (build 19041 / 2004 or later) or Windows 11 (`x64` or `ARM64`), with Direct3D 11 Feature Level 11.0+ hardware GPU (Vulkan 1.1+ / OpenCL optional for peak speed).
- **macOS**: macOS 14 Sonoma or later (`Apple Silicon arm64` or `Intel x64`) with Metal / MoltenVK hardware GPU support.
- **Linux**: X11 or XWayland session with hardware Vulkan 1.1+ or OpenCL ICD GPU drivers and standard desktop runtime libraries (see `TESTING.txt` inside the archive). CLI usage does not require a graphical session.

---

## Technical Details

<details>
<summary><strong>How It Works (OpenPGP v4 SHA-1 GPU Pipeline)</strong></summary>

An OpenPGP v4 `Ed25519Legacy` primary key fingerprint is `SHA-1` over a single 54-byte packet (`0x99 || len(51) || 0x04 || uint32_be(timestamp) || Ed25519 OID + MPI header || 32-byte pubkey`), which fits inside one 64-byte SHA-1 block.

```text
CPU: Async KeySchedulePool generates 4 Ed25519 keypairs -> Precomputes shared SHA-1 schedule
  |
  v
GPU: Share rounds 0..6 across all 4 keys for each Unix timestamp (2000-01-01 .. now)
  |  Interleave rounds 7..75 across 4 lanes; reconstruct timestamp XORs minimally
  |  After round 75: compare a76_nk directly with folded targetLowNoK3 (deferring rotl & K3)
  |  Round 76 (only on low-word match): evaluate round 76 and compare high suffix target
  |  Keep one hit per lane in FindOne mode (or fill bounded buffer) and return for verification
  v
CPU: Recompute full SHA-1 fingerprint -> Verify suffix -> Encrypt seed via DPAPI -> Notify
```

</details>

<details>
<summary><strong>Security, Privacy &amp; Local Data Storage</strong></summary>

- **Fully Offline / Zero Telemetry**: PGPMint makes no network requests.
- **Local Data Path**: Settings (`state.json`) and encrypted matches (`hits.vault.json`) are stored strictly in `%LOCALAPPDATA%\PGPMint\`.
- **Local Key Protection**: Matched Ed25519 private seeds are encrypted on disk with Windows DPAPI (`DataProtectionScope.CurrentUser`) and zeroed in memory after key rotation or export.
- **Vault Capacity**: The vault keeps up to 50 records. When it is full, minting stops and existing records stay intact. Export keys you want to keep before removing any records. Continuous minting uses a fresh key group after each saved key.
- **Corrupt Vault Safeguard**: If `hits.vault.json` cannot be parsed, PGPMint creates a `.corrupt.<timestamp>.bak` backup and refuses to overwrite stored keys.
- **CPU Verification Gate**: Every timestamp returned by a GPU is re-hashed on CPU with `System.Security.Cryptography.SHA1` before persistence.

</details>

<details>
<summary><strong>Adding a Custom Translation</strong></summary>

PGPMint loads built-in locales from embedded resources and automatically merges any JSON file placed in a `Strings/` folder next to `PGPMint.exe` (for example `Strings/it-IT.json`). Copy [src/PGPMint.App/Strings/en-US.json](src/PGPMint.App/Strings/en-US.json), update `_meta.code` and `_meta.nativeName`, translate the `strings` entries, and restart PGPMint to select it from Settings.

</details>

---

## Building from Source

Prerequisites:
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 SDK (`10.0.26100.0`)

```powershell
# Run full test suite (includes live GPU and real GnuPG / git commit -S tests)
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64

# Run headless CI-safe test suite (excludes HardwareGpu category)
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64 --filter "Category!=HardwareGpu"

# Build Release solution
dotnet build PGPMint.sln -p:Platform=x64 -c Release

# Publish trimmed standalone compressed single-file executable (x64)
dotnet publish src\PGPMint.App\PGPMint.App.csproj -p:Platform=x64 -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Publish trimmed standalone compressed single-file executable (ARM64)
dotnet publish src\PGPMint.App\PGPMint.App.csproj -p:Platform=ARM64 -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true
```

---

## Acknowledgements & Cloud GPU Usage Notice

Special thanks to **[gputw.ai](https://gputw.ai)** for providing the GPU compute resources used for the RTX 5090 (and RTX 3090) benchmark and power-efficiency curve testing in this repository.

> [!WARNING]
> **Cloud / Rented GPU Anti-Mining Risk Notice**: PGPMint performs sustained, compute-bound GPU SHA-1 hashing (`opencl-sha1-four-v4` / `vulkan-sha1-four-v4`). To cloud infrastructure telemetry and automated abuse-detection systems, its GPU utilization, clock profile, and continuous board power draw closely resemble cryptocurrency proof-of-work mining. **Do not start continuous PGPMint workloads on rented or cloud GPU instances without first reviewing the provider's Terms of Service / Acceptable Use Policy and obtaining explicit confirmation where required**, as unverified prolonged runs may trigger automated anti-mining enforcement and result in account suspension or termination.

---

## Contributing

Issues, translations, and pull requests are welcome. Before opening a PR, please run `dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64` and `dotnet build PGPMint.sln -p:Platform=x64 -c Release`, and keep source code and documentation free of emojis (enforced by CI).

## License

Released under the [MIT License](LICENSE).
