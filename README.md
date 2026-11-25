<div align="center">

# PGPMint

**GPU generator for custom OpenPGP v4 (`Ed25519`) vanity Key IDs, with a native Windows app and Linux/macOS test builds.**

[![CI](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml/badge.svg)](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Evanlau1798/PGPMint?label=Release&color=0078D6)](https://github.com/Evanlau1798/PGPMint/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Windows-10%2F11%20(x64%20%7C%20ARM64)-0078D6.svg)](#system-requirements)
[![.NET 10 WinUI 3](https://img.shields.io/badge/.NET%2010-WinUI%203-512BD4.svg)](#building-from-source)

[Download](#download--quick-start) · [Features](#features) · [Git Signing](#using-exported-keys-with-git) · [Benchmarks](#benchmarks) · [Build from Source](#building-from-source)

<br />

<img src="assets/screenshot-dashboard.png" alt="PGPMint main window on Windows 11 running at 23.08 GH/s" width="446" />

*PGPMint running on Windows 11 (Dark Mica) searching for a 12-hex Key ID suffix at full speed.*

</div>

---

## What is PGPMint?

PGPMint lets you mint custom **OpenPGP v4 (`Ed25519Legacy`)** keys whose 16-character Key ID ends with any hexadecimal pattern you choose (for example `6767 6767 6767` or `0123 4567 89AB`).

Unlike command-line tools that require installing Python or the NVIDIA CUDA Toolkit—or browser tabs that stall in the background—PGPMint ships as a **single portable Windows `.exe`** (~34 MB) with native Fluent / Mica UI, stepless GPU throttling, system tray keep-alive, and ready-to-sign `.asc` export.

---

## Features

### Linux and macOS test builds

The Windows WinUI application remains available. Linux and macOS use a shared
Avalonia desktop application and the same search coordinator, CPU verification,
OpenPGP export and GPU algorithms. Linux uses native Vulkan/OpenCL; macOS uses
Metal or MoltenVK. Software CPU renderers are excluded. WSL is a Linux testing
environment, not a separately supported GPU backend.

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

The CLI prompts for passwords without echoing them. For automation,
`--password-stdin` reads the vault password from standard input; export reads a
second line for the OpenPGP passphrase. Never pass a password as an argument or
print it in a script log. Search stops after its first saved match by default;
Ctrl+C stops safely and saves progress.

Linux/macOS vaults use password-derived Argon2id and AES-256-GCM protection and
user-only file permissions. Keep both `hits.vault.json` and `vault.key.json` when
backing up a vault. Use a dedicated vault directory. On Unix, an existing directory
must already have mode `0700`; PGPMint refuses broader permissions without changing
the directory. Explicitly secure your dedicated vault directory before reopening it.
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

The new desktop supports English/Traditional Chinese, encrypted results, export,
GPU selection, calibration, four power tiers and background operation where a
working tray/menu bar is available. The floating Mini HUD remains Windows-only.
Hardware validation and sustained performance on each Mac/GPU family remain
separate acceptance checks.

### Windows application

- **Single-File Portable Executable** — Self-contained compressed `.exe` for `win-x64` and `win-arm64`. No Python, CUDA Toolkit, .NET Runtime, or Windows App Runtime installation required.
- **Universal Windows Hardware GPU Engine** — Zero-dependency three-tier compute cascade: runs via native Vulkan Compute (`vulkan-1.dll` + programmatic SPIR-V 1.3, `vulkan-sha1-four-v4`) when a Vulkan 1.1+ compute GPU is present for peak sustained throughput, cascades to native `OpenCL.dll` (`opencl-sha1-four-v4`) when Vulkan is unavailable, and falls back transparently to DirectCompute (`D3D11 cs_5_0`, `d3d11-sha1-four-v4`) on any Direct3D 11 Feature Level 11.0+ GPU (including Windows on ARM). Supports multi-GPU parallel search while excluding CPU software renderers and virtual display adapters.
- **Measured Speed & Two-Stage Throttle**: PGPMint measures each GPU's throughput while searching and reuses valid calibration for the same workload execution path (`1`-`4` hex individually, `5`-`8` hex low-word group, `9`-`16` hex 64-bit group) and GPU configuration. Batch sizes adjust to measured dispatch times. Once calibrated, use `1%-99%` to limit speed or select `MAX` for full speed.
- **Zero-Overhead System Tray & Pure-CPU Floating HUD** — Closing the main window minimizes PGPMint to the notification area so WinUI 3 stops consuming GPU rendering cycles. An optional pure-CPU Direct2D/DirectWrite floating Mini HUD tracks live speed, probability, ETA, and progress (auto-hides during fullscreen apps/games).
- **Minimalist Dashboard & Separate Settings** — Live hash rate, smooth progress bar, cumulative hit probability (hover for single-hash odds; switches cleanly to `Found` once a matching key is stored), scientific-notation checked count, live ETA, and an adaptive results card that expands for up to 5 saved keys.
- **DPAPI Encrypted Vault & Signing-Ready `.asc` Export** — Every GPU match is double-verified on CPU, encrypted locally using Windows DPAPI (`CurrentUser`), and exported as RFC 9580 / GnuPG-compatible `.secret.asc` and `.public.asc` files with `Certify | Sign` key flags and optional AES-256-CFB passphrase protection.
- **Extensible Localization** — Built-in English and Traditional Chinese, plus automatic loading of custom `Strings/*.json` locale files placed next to `PGPMint.exe`.

---

## Download & Quick Start

1. Download the latest single-file executable from [GitHub Releases](https://github.com/Evanlau1798/PGPMint/releases) (`PGPMint-*-win-x64.exe` or `win-arm64.exe`) and run it directly.
2. **Initial Setup**: Enter your desired 1-16 hex Key ID suffix (`0-9`, `A-F`, e.g. `01 2345 6789`), choose hardware GPU(s), select your language, and click **Start**.
3. **Automatic Calibration & Search**: Start searching with a small batch. PGPMint runs the 30-second calibration when the current workload needs a new measurement, and can save a match during calibration. Adjust the throttle slider (`1%-99%` or `MAX`) once calibrated. Press **Stop** to finish the active batch and save progress, or close the window to continue in the system tray.

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
| Multi-GPU + Stepless Rate Cap | Yes (`1%-99%` + `MAX`) | Full speed only | Full speed only | Limited |
| Encrypted Local Vault + Git-Ready `.asc` | Yes (DPAPI + self-sig) | Raw export | Raw export | Manual download |

---

## System Requirements

- **OS**: Windows 10 (build 19041 / 2004 or later) or Windows 11 (`x64` or `ARM64`)
- **GPU**: One or more hardware GPUs supporting Direct3D 11 Feature Level 11.0+ (Vulkan 1.1+ driver or OpenCL ICD optional for maximum throughput with automatic Direct3D 11 fallback; CPU-only / software rendering is intentionally excluded)

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

PGPMint loads built-in locales from embedded resources and automatically merges any JSON file placed in a `Strings/` folder next to `PGPMint.exe` (for example `Strings/ja-JP.json`). Copy [src/PGPMint.App/Strings/en-US.json](src/PGPMint.App/Strings/en-US.json), update `_meta.code` and `_meta.nativeName`, translate the `strings` entries, and restart PGPMint to select it from Settings.

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

## Contributing

Issues, translations, and pull requests are welcome. Before opening a PR, please run `dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64` and `dotnet build PGPMint.sln -p:Platform=x64 -c Release`, and keep source code and documentation free of emojis (enforced by CI).

## License

Released under the [MIT License](LICENSE).
