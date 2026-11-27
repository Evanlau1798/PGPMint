[Back to README](../README.md) | [Platforms & CLI](platforms-and-cli.md) | [Benchmarks](benchmarks.md)

# Architecture, Calibration & Build Guide

## OpenPGP v4 SHA-1 GPU Pipeline

An OpenPGP v4 `Ed25519Legacy` primary key fingerprint is `SHA-1` over a single 54-byte packet (`0x99 || len(51) || 0x04 || uint32_be(timestamp) || Ed25519 OID + MPI header || 32-byte pubkey`), which fits inside one 64-byte SHA-1 compression block.

```text
CPU: Async KeySchedulePool generates 4 Ed25519 keypairs -> Precomputes shared SHA-1 schedule
  |
  v
GPU: Share rounds 0..6 across all 4 keys for each Unix timestamp (2000-01-01 .. now, or custom --since/--until)
  |  Interleave rounds 7..75 across 4 lanes; reconstruct timestamp XORs minimally
  |  After round 75: compare a76_nk directly with folded targetLowNoK3 (deferring rotl & K3)
  |  Round 76 (only on low-word match): evaluate round 76 and compare high pattern/wildcard mask
  |  v5 kernels support arbitrary-position '*' wildcards & prefixes across Vulkan/OpenCL/DirectCompute/Metal
  |  Keep one hit per lane in FindOne mode (or fill bounded buffer) and return for verification
  v
CPU: Recompute full SHA-1 fingerprint -> Verify pattern -> Encrypt seed (DPAPI / Argon2id+AES-256-GCM) -> Notify
```

Exported `.asc` keypairs include a self-signed User ID with `Certify | Sign` (`0x03`) flags, optional AES-256-CFB passphrase protection, an optional expiration timestamp (`--expires`), and a GnuPG-compatible `Cv25519` encryption subkey by default (`--no-encrypt-subkey` to opt out).

---

## Workload Calibration, 4-Tier Pacing & Thermal Guard

- **30-Second Curve Calibration**: PGPMint automatically runs a 30-second efficiency sweep whenever the saved profile lacks a workload identity or when the device, driver, backend, kernel configuration, or workload execution group (`1–4` hex fixed individually, `5–8` hex low-word group, `9–16` hex 64-bit group) changes. Matches discovered during calibration are verified and saved normally; incomplete batches caused by **Stop** or device disconnect are excluded from search progress.
- **4 Power Tiers & Idle-Floor Detection**: Once calibrated, you can switch live between `Eco`, `Balanced`, `Efficiency`, and `Max` (with elastic yielding to competing 3D workloads on desktop). If a GPU's `P2` compute floor remains high at low duty cycles (`IsHighIdlePowerFloor`, e.g., RTX 3090 keeping `46%–50%` of full board draw at 5% load), PGPMint automatically adapts `Eco` or surfaces a `Max`-tier recommendation hint.
- **Closed-Loop Thermal Guard**: On NVML-capable NVIDIA GPUs, PGPMint polls board temperature and power (`W`, `MH/W`) and throttles duty cycles smoothly if temperature approaches the configured ceiling (`70–90 C` in Desktop Settings, `65–90 C` via CLI `--max-temp`).

---

## Cross-Machine Sync (`.pgpmint-sync`)

When running distributed searches across multiple GPUs or machines:

- **Idempotent Progress Ledger**: Sync bundles track progress by `SessionId`, allowing repeated imports without double-counting searched candidates. Desktop Settings and CLI `pgpmint sync` support live hot-importing into an already-running search session.
- **Encrypted Key Exchange**: Matched key records carried inside `.pgpmint-sync` bundles are protected with Argon2id + AES-256-GCM before transport and re-verified on CPU upon import.

---

## Adding a Custom Translation

Desktop UIs ship with 10 built-in languages (`English`, `繁體中文`, `简体中文`, `日本語`, `한국어`, `Deutsch`, `Français`, `Español`, `Português (Brasil)`, and `Русский`). The Windows WinUI app also merges any custom JSON locale file placed in a `Strings/` folder next to `PGPMint.exe` (for example `Strings/it-IT.json`):

1. Copy [src/PGPMint.App/Strings/en-US.json](../src/PGPMint.App/Strings/en-US.json).
2. Update `_meta.code` and `_meta.nativeName`.
3. Translate the `strings` entries and restart PGPMint to select the new language in Settings.

---

## Building, Testing & Packaging from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Windows**: Windows 10/11 SDK (`10.0.26100.0`) for `src/PGPMint.App`
- **macOS**: Xcode Command Line Tools + Metal Toolchain for `src/PGPMint.MacApp`
- **Linux**: X11/XWayland, Vulkan (`libvulkan1`), OpenCL (`ocl-icd-libopencl1`), and standard desktop libraries for `src/PGPMint.Desktop`

### Build & Test (Separate Build and `--no-build` Steps)

```powershell
# Build Release solution
dotnet build PGPMint.sln -p:Platform=x64 -c Release

# Run full core test suite (includes live GPU and real GnuPG / git commit -S tests)
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64 -c Release --no-build

# Run headless CI-safe tests (excludes physical HardwareGpu category)
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64 -c Release --no-build --filter "Category!=HardwareGpu"

# Build and run cross-platform Avalonia Desktop unit tests
dotnet build tests\PGPMint.Desktop.Tests\PGPMint.Desktop.Tests.csproj -c Release -p:Platform=x64
dotnet test tests\PGPMint.Desktop.Tests\PGPMint.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-build
```

### Cross-Platform Release Packaging

Use [scripts/packaging/package.py](../scripts/packaging/package.py) to produce self-contained packages and SHA-256 manifests for any target RID:

```bash
# Windows
python scripts/packaging/package.py --rid win-x64 --tag v1.3.0 --platform x64 --scratch /absolute/path/to/packages
python scripts/packaging/package.py --rid win-arm64 --tag v1.3.0 --platform ARM64 --scratch /absolute/path/to/packages

# Linux
python scripts/packaging/package.py --rid linux-x64 --tag v1.3.0 --platform x64 --scratch /absolute/path/to/packages
python scripts/packaging/package.py --rid linux-arm64 --tag v1.3.0 --platform ARM64 --scratch /absolute/path/to/packages

# macOS (run on a macOS host with Xcode Command Line Tools)
python scripts/packaging/package.py --rid osx-arm64 --tag v1.3.0 --platform ARM64 --scratch /absolute/path/to/packages
python scripts/packaging/package.py --rid osx-x64 --tag v1.3.0 --platform x64 --scratch /absolute/path/to/packages
```

### Contributing

Issues, translations, and pull requests are welcome. Before opening a PR, ensure builds and relevant `--no-build` tests pass cleanly and keep all tracked source files and documentation free of emojis (enforced by CI).
