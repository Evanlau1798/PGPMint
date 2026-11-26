<div align="center">

# PGPMint

[English](README.md) | [繁體中文](README.zh-TW.md) | 简体中文 | [日本語](README.ja.md)

**使用显卡算力生成自定义后缀的 OpenPGP v4（`Ed25519`）个性化 Key ID，提供 Windows、macOS 原生应用、Linux 桌面界面及跨平台命令行工具。**

[![CI](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml/badge.svg)](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Evanlau1798/PGPMint?label=Release&color=0078D6)](https://github.com/Evanlau1798/PGPMint/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Windows%20%7C%20macOS%20%7C%20Linux-x64%20%7C%20ARM64-0078D6.svg)](#系统需求)
[![.NET 10](https://img.shields.io/badge/.NET%2010%20%2B%20SwiftUI%20%2B%20Avalonia-512BD4.svg)](#从源码构建)

[下载与快速开始](#下载与快速开始) · [功能特性](#功能特性) · [用于 Git 签名](#导出密钥并用于-git-签名) · [性能基准](#性能基准) · [从源码构建](#从源码构建)

<br />

<table>
  <tr>
    <td align="center" width="33%">
      <img src="assets/screenshot-dashboard.png" alt="PGPMint 在 Windows 11 上以 23.61 GH/s 运行" width="360" /><br />
      <sub><strong>Windows 11（WinUI 3 + Mica）</strong></sub>
    </td>
    <td align="center" width="33%">
      <img src="assets/screenshot-macos-dashboard.png" alt="PGPMint macOS 原生 SwiftUI / AppKit 应用以 2.25 GH/s 运行" width="360" /><br />
      <sub><strong>macOS（原生 SwiftUI / AppKit）</strong></sub>
    </td>
    <td align="center" width="33%">
      <img src="assets/screenshot-linux-dashboard.png" alt="PGPMint Linux Avalonia 12.2.0 在 Intel UHD Vulkan 上实机运行" width="360" /><br />
      <sub><strong>Linux（Avalonia 12.2.0 / X11）</strong></sub>
    </td>
  </tr>
</table>

*PGPMint 在 Windows、macOS 和 Linux 上使用显卡搜索自定义 OpenPGP v4 Key ID 后缀并支持校准功耗档位。*

</div>

---

## 什么是 PGPMint？

PGPMint 利用显卡算力高速生成 **OpenPGP v4（`Ed25519Legacy`）** 密钥，使 16 位十六进制 Key ID 结尾匹配您指定的任意字符模式（例如 `6767 6767 6767` 或 `0123 4567 89AB`）。

相比需要配置 Python、CUDA Toolkit 的脚本工具或后台会被限速的浏览器页面，PGPMint 提供 **Windows 免安装单文件可执行程序（GUI 与 CLI）**，以及开箱即用的 **macOS（`macos-arm64` / `macos-x64`）** 与 **Linux（`linux-x64` / `linux-arm64`）** 独立发行包，支持四档功耗效率调节、系统托盘/菜单栏后台运行与可直接用于 Git 签名的 `.asc` 导出。

---

## 功能特性

### macOS 与 Linux 桌面版及跨平台 CLI

Windows WinUI 3 应用（`src/PGPMint.App`）继续作为 Windows 主力版本，并与 macOS、Linux 同步采用统一的 PGPMint 矢量品牌图标（[assets/pgpmint-icon.svg](assets/pgpmint-icon.svg)）。

- **macOS（`src/PGPMint.MacApp` + `src/PGPMint.Host`）**：专为 macOS 14+ 打造的纯原生 **SwiftUI + AppKit** 桌面应用（提供 `macos-arm64` 与 `macos-x64`），通过私有匿名 `stdin`/`stdout` NDJSON 管道驱动随包内置的 `.NET 10` 独立后台助手（`pgpmint-host`）。私钥种子绝不跨进程传给界面；关闭管道后立即停止 GPU 并释放独占 `VaultSession`。新密钥库可通过登录钥匙串自动解锁（已有密码库保持密码解锁），菜单栏支持实时算力读取与 4 档切换，并内置 `IOKit` 防休眠、原生 Metal（`libpgpmint_metal.dylib`）与 MoltenVK Vulkan 支持。当前采用 ad-hoc 签名（未经过 Apple 公证），首次打开可能需在「系统设置」中手动放行。
- **Linux（`src/PGPMint.Desktop`）**：重新设计的 **Avalonia 12.2.0** XAML 桌面应用（提供 `linux-x64` 与 `linux-arm64`），包含初始设置向导、极简三卡片主界面、独立设置窗口、带悬停规则说明及原生 NVIDIA NVML（`libnvidia-ml.so.1`）实时功耗和 `MH/W` 遥测的四档功耗选择器、静默 `/usr/bin/systemd-inhibit` 防休眠、`StatusNotifierWatcher` 系统托盘集成及原生 Vulkan / OpenCL 后端（排除 CPU 软件渲染器）。
- **跨平台命令行工具（`src/PGPMint.Cli`）**：

```sh
# Windows 可直接运行 PGPMint-cli-*-win-x64.exe；macOS / Linux 解压后运行 ./cli/pgpmint
./pgpmint devices --json
./pgpmint verify --backend vulkan       # Linux 原生 Vulkan 或 macOS MoltenVK
./pgpmint verify --backend metal        # macOS 原生 Metal
./pgpmint benchmark --backend vulkan --seconds 60 --rounds 3
./pgpmint search --pattern BEEF
./pgpmint export --key YOUR_KEY_ID --name "Your Name" --output ./exported
```

CLI 与便携密钥库使用 Argon2id + AES-256-GCM 加密，严格校验 Unix `0700` 目录权限与 macOS ACL 规则；每次 GPU 命中的结果都会先在 CPU 上完成 SHA-1 双重校验后再保存。

### Windows 应用程序

- **单文件便携可执行程序**：`win-x64` 与 `win-arm64` 均打包为独立压缩单文件 `.exe`（GUI 与 CLI 各自为单文件），无需安装 Python、CUDA Toolkit、.NET 或 Windows App Runtime。
- **三层通用显卡计算引擎**：零外部依赖的三层自动降级架构——优先使用原生 **Vulkan Compute**（`vulkan-1.dll` + 程序生成的 SPIR-V 1.3、`vulkan-sha1-four-v4`）发挥峰值吞吐；不可用时回退原生 **`OpenCL.dll`**（`opencl-sha1-four-v4`）；再不可用时无缝回退 **DirectCompute**（`D3D11 cs_5_0`、`d3d11-sha1-four-v4`），支持多显卡并行搜索。
- **实测算力与四档功耗效率调节**：针对不同后缀长度分组（`1–4` 位、`5–8` 位、`9–16` 位）与显卡配置变动自动执行 30 秒全曲线校准，校准后可实时切换 `Eco` / `Balanced` / `Efficiency` / `Max`，并在其他 3D 应用高负载时自动弹性让道。
- **零开销系统托盘与纯 CPU 悬浮微窗**：关闭主窗口后最小化至系统托盘以释放 WinUI 渲染占用的显存与算力，并可选开启纯 CPU Direct2D/DirectWrite 悬浮微窗（全屏游戏/视频时自动隐藏）。
- **DPAPI 加密密钥库与 Git 就绪的 `.asc` 导出**：私密种子使用 Windows DPAPI（`CurrentUser`）本地加密存储（上限 50 条），可导出兼容 RFC 9580 / GnuPG 的 `.secret.asc` 与 `.public.asc`（带 `Certify | Sign` 标志，支持 AES-256-CFB 保护密码）。
- **内置 10 种主流语言**：Windows、macOS 与 Linux 三平台桌面界面均内置 `English`、`繁體中文`、`简体中文`、`日本語`、`한국어`、`Deutsch`、`Français`、`Español`、`Português (Brasil)` 与 `Русский`（Windows 版另支持在 `PGPMint.exe` 同级目录放置 `Strings/*.json` 扩展语言；CLI 保持英文以保证脚本与诊断一致性）。

---

## 下载与快速开始

1. 从 [GitHub Releases](https://github.com/Evanlau1798/PGPMint/releases) 下载对应平台的文件：
   - **Windows GUI**：`PGPMint-*-win-x64.exe` 或 `PGPMint-*-win-arm64.exe`（便携单文件）
   - **Windows CLI**：`PGPMint-cli-*-win-x64.exe` 或 `PGPMint-cli-*-win-arm64.exe`（便携单文件）
   - **macOS**：`PGPMint-*-macos-arm64.tar.gz` 或 `PGPMint-*-macos-x64.tar.gz`（含 `PGPMint.app` 与 CLI）
   - **Linux**：`PGPMint-*-linux-x64.tar.gz` 或 `PGPMint-*-linux-arm64.tar.gz`（含 `PGPMint.Desktop` 与 CLI）
2. **初始设置**：输入期望的 1–16 位十六进制 Key ID 后缀（`0–9`、`A–F`，例如 `01 2345 6789`），选择硬件显卡与界面语言并完成向导。
3. **自动校准与搜索**：点击开始后如当前路径未校准将自动进行 30 秒测量（校准过程中同样可保存命中结果）。校准完成后可随时切换四档功耗效率；点击停止会等待当前批次安全完成后保存进度，关闭窗口可常驻托盘／菜单栏后台运行。
4. **导出密钥**：在结果列表中点击 `.asc` 即可导出 `<KEYID>.secret.asc` 与 `<KEYID>.public.asc`。

---

## 导出密钥并用于 Git 签名

导出的密钥包含自签名 User ID 与 `Certify | Sign`（`0x03`）标志，可直接用于 GnuPG、Git 与 GitHub Verified 提交认证：

```powershell
# 1. 将导出的私钥导入 GnuPG
gpg --import <KEYID>.secret.asc

# 2. 配置 Git 使用新 Key ID 签名提交
git config --global user.signingkey <KEYID>
git config --global commit.gpgsign true

# 3. 将 <KEYID>.public.asc 添加到 GitHub -> Settings -> SSH and GPG keys
```

---

## 性能基准

以下 v4 引擎数据实测于 **NVIDIA GeForce RTX 3080**（Windows 11 x64、WDDM 驱动 `32.0.16.1088`、Release x64、全速档位），统计涵盖 Ed25519 生成、SHA-1 预计算、GPU 调度及回读：

| 后端 | Kernel | 134M 批次中位耗时 | 536M 批次中位耗时 | 端到端 Coordinator（全速） |
|---|---|---:|---:|---:|
| 原生 Vulkan（`vulkan-1.dll`） | `vulkan-sha1-four-v4`（`ubo-spirv-v4`） | **5.63–6.12 ms**（`21.93–23.82 GH/s`） | **23.11–23.14 ms**（`23.20–23.24 GH/s`） | **23.23 GH/s** |
| 原生 OpenCL（`OpenCL.dll`） | `opencl-sha1-four-v4`（`cmem0-v4`） | **5.68–6.27 ms**（`21.42–23.61 GH/s`） | **23.78–23.92 ms**（`22.44–22.58 GH/s`） | **23.19 GH/s** |
| DirectCompute（`D3D11 cs_5_0`） | `d3d11-sha1-four-v4`（`cb1-v4`） | **9.14–9.44 ms**（`14.21–14.69 GH/s`） | **37.33–37.38 ms**（`14.36–14.38 GH/s`） | 自动回退 |

其他实机测试平台：
- **Apple M3（macOS 15.6.1 `macos-arm64`）**：MoltenVK 实机约 **2.25 GH/s**。
- **Intel Alder Lake UHD（`ADL GT2`，Rocky Linux 9.8 `linux-x64`、Mesa 25.2.7 Vulkan）**：实机校准峰值约 **566.99 MH/s**（中位 **561.90 MH/s**）。

### NVIDIA GeForce RTX 5090（Linux x64，450 W 功耗上限）

于 **NVIDIA GeForce RTX 5090**（32 GiB VRAM、Ubuntu 22.04.5 LTS、驱动 `595.91.07`、容器限制 **450.00 W** 上限／默认 `575.00 W`）配合 `PGPMint v1.2.1` 与原生 Linux NVML（`libnvidia-ml.so.1`），在 13 位后缀 `***1145141919810` 上完成全套实测（算力致谢与云端风险提示见[致谢与云端 GPU 使用警示](#致谢与云端-gpu-使用警示)）。

| 设备 | 后端 | Kernel / Workgroup | Micro 中位数 | 端到端 Coordinator（Max） | 备注 |
|---|---|---|---:|---:|---|
| **RTX 5090（450 W）** | **OpenCL** | `opencl-sha1-four-v4` / `64` | **83.93 GH/s** | **76.63 GH/s** | 微基准最高 |
| **RTX 5090（450 W）** | **OpenCL** | `opencl-sha1-four-v4` / `128` | **83.32 GH/s** | **76.18 GH/s** | CLI `search` 默认配置（`cmem0-v4`） |
| **RTX 5090（450 W）** | **OpenCL** | `opencl-sha1-four-v4` / `256` | 81.60 GH/s | 75.97 GH/s | — |
| RTX 5090（450 W） | Vulkan | `vulkan-sha1-four-v4` / `64`–`256` | 56.75–57.42 GH/s | 54.86–55.03 GH/s | 需无头 EGL ICD（`nvidia_egl_icd.json`） |
| RTX 3090 | OpenCL | `opencl-sha1-four-v4` / `128` | — | 24.29–25.37 GH/s | 单卡基准（`300 W` 上限、`81.20 MH/W`） |
| **RTX 3090 x2 NVLink** | **OpenCL** | `opencl-sha1-four-v4` / `128` | — | **49.43–50.41 GH/s** | 双卡合计 `600 W` 上限（`82.54 MH/W`；长时热稳态 `46.42 GH/s`） |
| RTX 3080（Windows） | Vulkan | `vulkan-sha1-four-v4` / `128` | 23.20–23.82 GH/s | 22.51–23.23 GH/s | 满载 `346.61 W`（`64.95 MH/W`） |

<p align="center">
  <img src="assets/rtx5090-power-curve.svg" alt="PGPMint GPU 功耗能效曲线（功耗 W 对实测算力 GH/s），对比 RTX 5090（OpenCL，450 W 限制）、RTX 3090 x2 NVLink（OpenCL，600 W 限制）与 RTX 3080（Vulkan）" width="100%" />
</p>

<details>
<summary><strong>展开查看 RTX 5090 完整 21 点算力／每瓦效率曲线与 180 秒稳定性数据（OpenCL, 4x128）</strong></summary>

每个阶梯（`100%` 全速、`95%`–`5%` 为 PGPMint 50 ms 周期切片限速）均先稳定 3 秒再以 NVML 稳态采样 5 秒（`0%` 为待机 5 秒），所有计算候选均计入持久进度（零浪费）。

| 目标比例 | 实测算力 (GH/s) | 功耗中位数 (W) | 每瓦获得 (MH/W) | GPU 利用率 (%) | 核心 / 显存频率 (MHz) | 温度 (C) | P-State | 限电标志 |
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
| **0%（待机）** | 0.000 | 28.32 | — | 0 | 195 / 405 | 52 | P8 | `None` |

- **为何 `100%` 全速反而是最高能效点（`172.61 MH/W`）**：在 450 W 功耗墙约束下，满载核心稳定在 `2460 MHz` 高能效区间，每瓦产出达到 RTX 3080（`64.95 MH/W`）的 **2.66 倍**；而 50 ms 时间切片限速使 GPU 维持在 `P1`（GDDR7 `13801 MHz`，5% 负载仍耗 `104.79 W`），且 `85%–95%` 短脉冲反而拉升核心至 `2610–2685 MHz` 并依旧触及 `450 W` 上限。降至 `40%` 时核心回落至 `2580 MHz`，功耗骤降 `32.7 W` 并出现效率拐点（`120.56 MH/W`）。
- **图表标注四档位落点（`EfficiencyCurveSelector`）**：RTX 5090（启用高待机功耗 `Eco` 适配）：`节能`（`10%`）`7.83 GH/s @ 126.09 W`（`62.12 MH/W`）、`平衡`（`40%`）`31.20 GH/s @ 258.84 W`（`120.56 MH/W`）、`效率`（`70%`）`54.54 GH/s @ 395.45 W`（`137.92 MH/W`）、`全速`（`100%`）`77.67 GH/s @ 450.00 W`（`172.61 MH/W`）；RTX 3080（Windows Vulkan）：`节能`（`11%`）`2.71 GH/s @ 56.47 W`（`48.03 MH/W`）、`平衡`（`23%`）`5.39 GH/s @ 108.99 W`（`49.42 MH/W`）、`效率`（`67%`）`15.88 GH/s @ 301.49 W`（`52.69 MH/W`）、`全速`（`100%`）`22.51 GH/s @ 346.61 W`（`64.95 MH/W`）。
- **四档位实时切换验证（5 秒稳定 + 10 秒 `MultiGpuCoordinator`，v1.2.1 基准）**：`Max` `76.680 GH/s @ 449.99 W`（`170.40 MH/W`）、`Efficiency`（`62.6%`）`49.743 GH/s @ 362.77 W`（`137.12 MH/W`）、`Balanced`（`31.3%`）`24.242 GH/s @ 217.69 W`（`111.36 MH/W`）、`Eco`（`7.7%`）`6.136 GH/s @ 115.89 W`（`52.95 MH/W`）。
- **180 秒满载稳定性**：1 秒区间平均 **`74.695 GH/s`**（中位数 **`74.726 GH/s`**，UI 窗口由 `76.1` 平稳收敛至 `74.4 GH/s`），平均功耗 **`448.71 W`**（**`166.47 MH/W`**），温度稳定在 `70 C -> 80 C`（`0/180` 热降频、`179/180` `SwPowerCap`）。

</details>

<details>
<summary><strong>展开查看 RTX 3090 x2 NVLink 双卡实测摘要（OpenCL, 4x128, 单卡 300 W 限制）</strong></summary>

- **硬件与拓扑**：双张 NVIDIA GeForce RTX 3090（各 24 GiB、4 通道 NVLink `NV4`、容器单卡限制 `300.00 W`／双卡合计 `600.00 W` 上限）、Ubuntu 22.04.5 LTS、驱动 `595.99.02`。
- **单卡对双卡扩展率**：单卡实测 `24.289 GH/s`（`299.1 W`、`81.20 MH/W`）；双卡 30 秒官方校准合计 `50.414 GH/s`（`GPU 0`：`25.042 GH/s`、`GPU 1`：`25.372 GH/s`；21 点满载稳态 `49.429 GH/s @ 598.83 W`、`82.54 MH/W`，扩展率 **`2.03x`**）。
- **四档位落点（`power_curve_settled.csv`）**：`节能`（`5%`、`power-floor-fallback`）`2.52 GH/s @ 277.99 W`（`9.08 MH/W`）、`平衡`（`15%`）`7.56 GH/s @ 319.29 W`（`23.68 MH/W`）、`效率`（`55%`）`27.06 GH/s @ 523.91 W`（`51.64 MH/W`）、`全速`（`100%`）`49.43 GH/s @ 598.83 W`（`82.54 MH/W`）。因 `P2` 低载底噪高达 `278 W`（占满载 `46.4%`），`IsHighIdlePowerFloor` 自动识别并展示全速建议提示。
- **60 秒连续满载稳定性**：堆叠槽位 `GPU 0` 在 `82–83 C` 触发温控降频（`299 -> 242 W`），`GPU 1` 维持 `66 C / 299 W`，双卡连续稳态平均为 **`46.416 GH/s @ 550.6 W`（`84.31 MH/W`）**。

</details>

### 预期平均等待时间（按 20 GH/s 估算）

| 后缀长度 | 搜索空间（$16^k$） | 预期平均时间 |
|---:|---:|---:|
| 8 位 | $4.29 \times 10^{9}$ | ~0.2 秒 |
| 10 位 | $1.10 \times 10^{12}$ | ~55 秒 |
| 12 位 | $2.81 \times 10^{14}$ | ~3.9 小时 |
| 13 位 | $4.50 \times 10^{15}$ | ~2.6 天 |
| 14 位 | $7.21 \times 10^{16}$ | ~41.7 天 |

### 与其他方案对比

| 特性 | PGPMint | CLI CUDA 工具 | CLI OpenCL 工具 | 浏览器 WebGL/WebGPU |
|---|---|---|---|---|
| 原生桌面 UI 与系统托盘 | 是（WinUI 3 / SwiftUI / Avalonia） | 否 | 否 | 浏览器标签页（后台限速） |
| 免装 Python / CUDA Toolkit | 是（单文件 / 自包含包） | 需装 CUDA | 需装 Runtime | 是 |
| 通用显卡自动回退 | Vulkan + OpenCL + DirectCompute / Metal | 仅 NVIDIA | 需 OpenCL ICD | 依赖浏览器支持 |
| 多显卡＋功耗效率档位 | 是（`Eco` / `Balanced` / `Efficiency` / `Max`） | 仅全速 | 仅全速 | 有限 |
| 加密密钥库与 Git 就绪 `.asc` | 是（DPAPI / Argon2id + 自签名） | 原始导出 | 原始导出 | 手动下载 |

---

## 系统需求

- **Windows**：Windows 10（版本 19041 / 2004 及以上）或 Windows 11（`x64` 或 `ARM64`），配备支持 Direct3D 11 Feature Level 11.0+ 的硬件显卡（推荐安装 Vulkan 1.1+ 或 OpenCL 驱动以获取峰值性能）。
- **macOS**：macOS 14 Sonoma 及以上（`Apple Silicon arm64` 或 `Intel x64`），支持 Metal / MoltenVK 硬件显卡。
- **Linux**：具备 X11 或 XWayland 图形会话及 Vulkan 1.1+／OpenCL ICD 硬件驱动（无图形环境可单独使用 CLI；运行库列表见压缩包内 `TESTING.txt`）。

---

## 从源码构建

前置依赖：
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 SDK（`10.0.26100.0`，用于构建 Windows UI）或 Xcode Command Line Tools（用于构建 macOS 原生应用）

```powershell
# 运行完整测试（含本机硬件 GPU 与 GnuPG 签名测试）
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64

# 先编译后运行非 GPU CI 测试
dotnet build PGPMint.sln -p:Platform=x64 -c Release
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64 -c Release --no-build --filter "Category!=HardwareGpu"

# 使用打包脚本生成指定平台发行包
python scripts/packaging/package.py --rid win-x64 --tag v1.2.1 --platform x64 --scratch C:\pgpmint-dist
```

---

## 致谢与云端 GPU 使用警示

特别感谢 **[gputw.ai](https://gputw.ai)** 提供本次 RTX 5090（以及此前 RTX 3090）性能基准测试与算力／每瓦效率曲线测量所需的 GPU 算力支持。

> [!WARNING]
> **云端／租用 GPU 反挖矿检测风险提示**：PGPMint 在搜索 OpenPGP Key ID 时会进行长时间、高满载的 GPU SHA-1 哈希演算（`opencl-sha1-four-v4` / `vulkan-sha1-four-v4`），其计算特征、GPU 占用与持续功耗表现与**加密货币挖矿（Proof-of-Work Mining）极为相似**。**在未提前确认服务商使用条款（Terms of Service / Acceptable Use Policy）或获得许可之前，请勿直接在租用或云端 GPU 实例上启动长时间计算**，否则可能触发后台的反挖矿检测机制并导致**账号被封禁或停用**。

---

## 参与贡献

欢迎提交 Issue、语言翻译与 Pull Request。提交前请确认测试与 Release 构建无警告通过，并保持源码和文档中不含任何表情符号（由 CI 自动校验）。

## 开源许可

基于 [MIT License](LICENSE) 开源发布。
