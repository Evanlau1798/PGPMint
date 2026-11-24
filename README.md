<div align="center">

# PGPMint

**Native Windows GPU generator for custom OpenPGP v4 (`Ed25519`) vanity Key IDs.**

[![CI](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml/badge.svg)](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Evanlau1798/PGPMint?label=Release&color=0078D6)](https://github.com/Evanlau1798/PGPMint/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Windows-10%2F11%20(x64%20%7C%20ARM64)-0078D6.svg)](#system-requirements)
[![.NET 8 WinUI 3](https://img.shields.io/badge/.NET%208-WinUI%203-512BD4.svg)](#building-from-source)

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

- **Single-File Portable Executable** — Self-contained compressed `.exe` for `win-x64` and `win-arm64`. No Python, CUDA Toolkit, .NET Runtime, or Windows App Runtime installation required.
- **Universal Windows Hardware GPU Engine** — Automatically accelerates via native `OpenCL.dll` (four-key interleaved SHA-1 kernel) on NVIDIA, AMD, and Intel GPUs, and transparently falls back to DirectCompute (`D3D11 cs_5_0`) on GPUs without an OpenCL ICD (including Windows on ARM). Supports multi-GPU parallel search while excluding CPU software renderers and virtual display adapters.
- **Measured Speed & Two-Stage Throttle**: PGPMint measures each GPU's throughput while searching and reuses valid calibration for the same suffix length and GPU configuration. Batch sizes adjust to measured dispatch times. Once calibrated, use `1%-99%` to limit speed or select `MAX` for full speed.
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

If a GPU becomes unavailable during search, PGPMint tries to reopen it once and starts again with a small batch. Progress includes only batches completed before the loss and after recovery.

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

Measured on an **NVIDIA GeForce RTX 3080** (Windows 11 x64) searching OpenPGP v4 `Ed25519Legacy` suffixes end-to-end (host Ed25519 keygen, SHA-1 schedule precomputation, GPU dispatch, and readback):

| Backend | Kernel | Measured Throughput | Notes |
|---|---|---:|---|
| Native OpenCL (`OpenCL.dll`) | `opencl-sha1-four-v1` (4-key, unroll 2) | **21.06 GH/s** (tray / Mini HUD) / **20.00 GH/s** (foreground) | Default when OpenCL GPU ICD is available |
| DirectCompute (`D3D11 cs_5_0`) | `d3d11-sha1-four-v1` (4-key) | **13.51 GH/s** | Automatic fallback on non-OpenCL GPUs |

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
| Universal Windows GPU Fallback | OpenCL + DirectCompute | NVIDIA only | Needs OpenCL ICD | Browser dependent |
| Multi-GPU + Stepless Rate Cap | Yes (`1%-99%` + `MAX`) | Full speed only | Full speed only | Limited |
| Encrypted Local Vault + Git-Ready `.asc` | Yes (DPAPI + self-sig) | Raw export | Raw export | Manual download |

---

## System Requirements

- **OS**: Windows 10 (build 19041 / 2004 or later) or Windows 11 (`x64` or `ARM64`)
- **GPU**: One or more hardware GPUs supporting Direct3D 11 Feature Level 11.0+ (OpenCL ICD optional for maximum throughput; CPU-only / software rendering is intentionally excluded)

---

## Technical Details

<details>
<summary><strong>How It Works (OpenPGP v4 SHA-1 GPU Pipeline)</strong></summary>

An OpenPGP v4 `Ed25519Legacy` primary key fingerprint is `SHA-1` over a single 54-byte packet (`0x99 || len(51) || 0x04 || uint32_be(timestamp) || Ed25519 OID + MPI header || 32-byte pubkey`), which fits inside one 64-byte SHA-1 block.

```text
CPU: Generate 4 Ed25519 keypairs -> Precompute timestamp-independent SHA-1 schedule
  |
  v
GPU: Share rounds 0..4 across all 4 keys for each Unix timestamp (1 .. now)
  |  Interleave rounds 5..75 across 4 lanes
  |  After round 75: early reject on low 32 bits of Key ID
  |  Round 76 (survivors only): check high 32 bits -> atomic append hit timestamp
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
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
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
