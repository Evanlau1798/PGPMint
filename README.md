# PGPMint

[![CI](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml/badge.svg)](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20(x64%20%7C%20ARM64)-0078D6.svg)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-8.0%20WinUI%203-512BD4.svg)](#building-from-source)

Native Windows WinUI 3 desktop application for minting custom **OpenPGP v4 (Ed25519Legacy)** vanity Key IDs on Windows hardware GPUs.

PGPMint combines a four-key interleaved SHA-1 GPU engine, stepless background throttling, system tray keep-alive, and signing-ready OpenPGP `.asc` export in a single self-contained native Windows binary with zero Python or CUDA Toolkit dependencies.

---

## Table of Contents

- [Why PGPMint](#why-pgpmint)
- [Features](#features)
- [Performance](#performance)
- [How It Works](#how-it-works)
- [Quick Start](#quick-start)
- [Using Exported Keys with Git](#using-exported-keys-with-git)
- [Requirements](#requirements)
- [Building from Source](#building-from-source)
- [Security and Privacy](#security-and-privacy)
- [Contributing](#contributing)
- [License](#license)

---

## Why PGPMint

Most existing OpenPGP vanity key generators are command-line utilities bound to NVIDIA CUDA or browser WebGL tabs that stall in the background and run all-or-nothing at 100% GPU load.

| Capability | PGPMint | CLI CUDA Tools | CLI OpenCL Tools | Browser WebGL/WebGPU |
|---|---|---|---|---|
| Native Windows GUI + System Tray | Yes (WinUI 3 + Mica) | No | No | Tab only (throttled in background) |
| Zero Python / CUDA Toolkit Install | Yes | Requires CUDA | Requires runtime setup | Yes |
| Universal Windows GPU Fallback | OpenCL + DirectCompute D3D11 | NVIDIA only | Needs OpenCL ICD | Browser dependent |
| Multi-GPU Parallel Search | Yes | Manual | Manual | Single GPU |
| Per-GPU Calibration + Stepless Rate Cap | Yes (1% to MAX) | Full speed only | Full speed only | Limited |
| Local Encrypted Vault + Git-Ready `.asc` | Yes (DPAPI + Ed25519 self-sig) | Raw export | Raw export | Manual download |

---

## Features

- **Hybrid Zero-Dependency GPU Engine (GPU-Only)**
  - Uses DXGI + WDDM `D3DKMTQueryAdapterInfo` to enumerate real physical hardware GPUs while strictly excluding CPU software renderers (WARP), remote adapters, and virtual indirect display drivers (such as Virtual Desktop, SuperDisplay, and Parsec).
  - Automatically accelerates via native `OpenCL.dll` P/Invoke (four-key interleaved kernel with hardware `rotate` and `bitselect`) on NVIDIA, AMD, and Intel drivers, and transparently falls back to DirectCompute (`D3D11 cs_5_0`) on any Windows GPU lacking an OpenCL ICD (including Windows on ARM).
- **Multi-GPU Parallelism and Real-Time Median Speed Control**
  - First-launch Setup lets you choose language, enter your desired 1-16 hex Key ID suffix, and select hardware GPUs with zero speculative speed estimates.
  - Starting a search automatically measures each GPU at full speed for 30 seconds on your real target pattern (zero wasted candidates; auto-stops immediately on the first match if enabled) to lock in a true median baseline.
  - A two-stage throttle slider provides `1%-99%` precision rate limiting (`%` + actual rate limit) and a detent-separated `MAX` (full-speed) position that continuously refines the median baseline at low frequency and stops sampling the moment you drag back into `1%-99%`.
- **Minimalist Dashboard, Extensible i18n, and Separate Settings Window**
  - Main window stays focused on current target preview, live measured hash rate, two-stage rate slider, unified Start/Stop action (always persisting progress on stop), GPU-friendly smooth progress bar, cumulative hit probability with single-hash probability tooltip, scientific-notation checked candidate count, live ETA, and an always-visible adaptive results panel with one-click clear-all.
  - Independent Settings window puts an extensible JSON-backed language selector (`Strings/*.json`) at the top, followed by Key ID suffix editing, responsive preferences layout, and hardware GPU selection.
- **System Tray Keep-Alive & Zero-GPU-Overhead Mini HUD**
  - Closing the main window minimizes PGPMint to the Windows notification area so WinUI 3 stops consuming GPU rendering cycles while search runs at full throughput.
  - Optional pure-CPU Win32 layered floating Mini HUD (rendered via Direct2D Software RenderTarget + DirectWrite grayscale antialiasing, auto dark/light theme, draggable with saved position, auto-hides during fullscreen games/presentations) displays live hash rate, cumulative probability, ETA, and progress bar while minimized to tray.
  - Right-click context menu and multi-line tray tooltip show live rate, progress, ETA, active target suffix, and checked count; double-click restores the main window.
  - Prevents system sleep during active search while still allowing the display to power off, throttles notifications on rapid matches, defaults to single-hit auto-stop, enforces single-instance activation, and supports optional launch at Windows startup (`--minimized`).
- **Signing-Ready OpenPGP Export**
  - Matched Ed25519 private seeds and creation timestamps are double-verified on CPU and encrypted locally using Windows DPAPI (`CurrentUser`).
  - Exports RFC 9580 / GnuPG-compatible OpenPGP v4 ASCII-armored secret (`.secret.asc`) and public (`.public.asc`) keys with User ID positive certification self-signature, `Certify | Sign` key flags (`0x03`), and optional AES-256-CFB passphrase protection (Iterated & Salted SHA-256 S2K).

---

## Performance

Measured on an **NVIDIA GeForce RTX 3080** (Windows 11 x64, Driver `32.0.16.1088`) searching 13-hex OpenPGP v4 Ed25519Legacy suffixes including host Ed25519 key generation, SHA-1 schedule precomputation, GPU dispatch, and readback:

| Backend | Kernel | Measured Throughput | Notes |
|---|---|---:|---|
| Native OpenCL (`OpenCL.dll`) | `opencl-sha1-four-v1` (4-key, unroll 2) | **21.06 GH/s** (tray / Mini HUD) / **20.00 GH/s** (foreground) | Default when OpenCL GPU ICD is present |
| DirectCompute (`D3D11 cs_5_0`) | `d3d11-sha1-four-v1` (4-key) | **13.51 GH/s** | Automatic fallback on non-OpenCL GPUs |

### Expected Mean Wait Time by Suffix Length (at 20 GH/s)

| Suffix Length | Search Space ($16^k$) | Expected Average Time |
|---:|---:|---:|
| 8 hex chars | $4.29 \times 10^{9}$ | ~0.2 s |
| 10 hex chars | $1.10 \times 10^{12}$ | ~55 s |
| 12 hex chars | $2.81 \times 10^{14}$ | ~3.9 hours |
| 13 hex chars | $4.50 \times 10^{15}$ | ~2.6 days |
| 14 hex chars | $7.21 \times 10^{16}$ | ~41.7 days |

---

## How It Works

An OpenPGP v4 Ed25519Legacy primary key fingerprint is `SHA-1` over a single 54-byte packet (`0x99 || len(51) || 0x04 || uint32_be(timestamp) || Ed25519 OID + MPI header || 32-byte pubkey`), which fits inside one 64-byte SHA-1 block.

```text
CPU: Generate 4 Ed25519 keypairs -> Precompute timestamp-independent SHA-1 schedule
  |
  v
GPU: Share rounds 0..4 across all 4 keys for each Unix timestamp (1 .. now)
  |  Interleave rounds 5..75 across 4 lanes
  |  Round 76: early reject on low 32 bits of Key ID
  |  Round 77 (survivors only): check high 32 bits -> atomic append hit timestamp
  v
CPU: Recompute full SHA-1 fingerprint -> Verify suffix -> Encrypt seed via DPAPI -> Notify
```

---

## Quick Start

1. Launch `PGPMint.exe`.
2. **Initial Setup**: Pick your language, enter your desired 1-16 hex Key ID suffix (`0-9`, `A-F`, e.g. `01 2345 6789`), select hardware GPU(s), and click **Finish**.
3. Press **Start** on the main dashboard. On the first run, PGPMint automatically runs at full speed for 30 seconds to measure your GPU's real median throughput while searching your target.
4. Once measured, use the two-stage slider anytime (`1%–99%` capped with live limit readout, or push past the right detent for uncapped `MAX` with dynamic median tracking), press **Stop** anytime to halt and persist all checked progress, or close the window to keep searching quietly in the system tray with the pure-CPU floating Mini HUD.
5. Click the gear icon to open **Settings** whenever you want to switch language (or drop additional `Strings/*.json` locale files), edit the target Key ID suffix, adjust preferences, or toggle GPUs.

---

## Using Exported Keys with Git

When PGPMint finds a match, click `.asc` next to the Key ID to export `<KEYID>.secret.asc` and `<KEYID>.public.asc`.

```powershell
# 1. Import your minted secret key into GnuPG
gpg --import <KEYID>.secret.asc

# 2. Configure Git to sign commits with your new Key ID
git config --global user.signingkey <KEYID>
git config --global commit.gpgsign true

# 3. Upload <KEYID>.public.asc to GitHub -> Settings -> SSH and GPG keys
```

---

## Requirements

- Windows 10 (build 19041 or later) or Windows 11 (`x64` or `ARM64`)
- One or more hardware GPUs supporting Direct3D 11 Feature Level 11.0+ (OpenCL ICD optional for maximum throughput)
- CPU-only / software rendering is intentionally not supported

---

## Building from Source

Prerequisites:
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 SDK (`10.0.26100.0`)

```powershell
# Run all tests (including live GPU tests and real GnuPG / git commit -S verification)
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64

# Run headless CI-safe test suite (excludes HardwareGpu category)
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64 --filter "Category!=HardwareGpu"

# Build Release solution
dotnet build PGPMint.sln -p:Platform=x64 -c Release

# Publish standalone portable self-contained build (x64)
dotnet publish src\PGPMint.App\PGPMint.App.csproj -p:Platform=x64 -c Release -r win-x64 --self-contained true
```

---

## Security and Privacy

- **Zero Telemetry / Fully Offline**: PGPMint makes no network requests.
- **Local Key Protection**: Matched Ed25519 private seeds are encrypted on disk with Windows DPAPI (`DataProtectionScope.CurrentUser`) and zeroed in memory after rotation or export.
- **Corrupt Vault Safeguard**: If `hits.vault.json` cannot be parsed, PGPMint creates a `.corrupt.<timestamp>.bak` backup and refuses to overwrite stored keys.
- **CPU Verification Gate**: Every timestamp returned by a GPU is re-hashed on CPU with `System.Security.Cryptography.SHA1` before persistence.

---

## Contributing

Issues and pull requests are welcome. Please run `dotnet test` and `dotnet build -p:Platform=x64 -c Release` before submitting changes, and avoid introducing emojis in source code or documentation.

## License

Released under the [MIT License](LICENSE).
