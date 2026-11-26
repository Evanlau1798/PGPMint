<div align="center">

# PGPMint

[English](README.md) | 繁體中文 | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

**使用顯示卡算力產生自訂尾綴的 OpenPGP v4（`Ed25519`）個人化 Key ID，提供 Windows、macOS 原生應用、Linux 桌面介面與跨平台命令列工具。**

[![CI](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml/badge.svg)](https://github.com/Evanlau1798/PGPMint/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Evanlau1798/PGPMint?label=Release&color=0078D6)](https://github.com/Evanlau1798/PGPMint/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Windows%20%7C%20macOS%20%7C%20Linux-x64%20%7C%20ARM64-0078D6.svg)](#系統需求)
[![.NET 10](https://img.shields.io/badge/.NET%2010%20%2B%20SwiftUI%20%2B%20Avalonia-512BD4.svg)](#從原始碼建置)

[下載與快速開始](#下載與快速開始) · [功能特色](#功能特色) · [搭配 Git 簽章](#匯出金鑰並用於-git-簽章) · [效能基準](#效能基準) · [從原始碼建置](#從原始碼建置)

<br />

<table>
  <tr>
    <td align="center" width="33%">
      <img src="assets/screenshot-dashboard.png" alt="PGPMint 於 Windows 11 執行達 23.61 GH/s" width="360" /><br />
      <sub><strong>Windows 11（WinUI 3 + Mica）</strong></sub>
    </td>
    <td align="center" width="33%">
      <img src="assets/screenshot-macos-dashboard.png" alt="PGPMint macOS 原生 SwiftUI / AppKit 應用執行達 2.25 GH/s" width="360" /><br />
      <sub><strong>macOS（原生 SwiftUI / AppKit）</strong></sub>
    </td>
    <td align="center" width="33%">
      <img src="assets/screenshot-linux-dashboard.png" alt="PGPMint Linux Avalonia 12.2.0 於 Intel UHD Vulkan 實機執行" width="360" /><br />
      <sub><strong>Linux（Avalonia 12.2.0 / X11）</strong></sub>
    </td>
  </tr>
</table>

*PGPMint 於 Windows、macOS 與 Linux 跨平台以顯示卡搜尋自訂 OpenPGP v4 Key ID 尾綴與校準功耗檔位。*

</div>

---

## 什麼是 PGPMint？

PGPMint 讓您利用顯示卡高速產生 **OpenPGP v4（`Ed25519Legacy`）** 金鑰，使其 16 碼十六進位 Key ID 尾端符合任何指定字樣（例如 `6767 6767 6767` 或 `0123 4567 89AB`）。

不同於需要安裝 Python、CUDA Toolkit 的命令列腳本，或會在背景遭到降速限制的瀏覽器網頁工具，PGPMint 提供 **Windows 免安裝單檔執行檔（GUI 與 CLI）**，以及內含獨立執行環境的 **macOS（`macos-arm64` / `macos-x64`）** 與 **Linux（`linux-x64` / `linux-arm64`）** 套件，支援功耗效率檔位切換、常駐系統匣／選單列背景運行與可直接用於 Git 簽章的 `.asc` 匯出。

---

## 功能特色

### macOS 與 Linux 桌面版及跨平台 CLI

Windows WinUI 3 應用（`src/PGPMint.App`）持續作為 Windows 主力產品，並與 macOS、Linux 同步換上統一的 PGPMint 向量圖示（[assets/pgpmint-icon.svg](assets/pgpmint-icon.svg)）。

- **macOS（`src/PGPMint.MacApp` + `src/PGPMint.Host`）**：專為 macOS 14+ 打造的純原生 **SwiftUI + AppKit** 桌面應用（支援 `macos-arm64` 與 `macos-x64`），底層透過匿名 `stdin`/`stdout` NDJSON 私有管線驅動隨附的自包含 `.NET 10` 背景助手（`pgpmint-host`）。私密種子絕不跨程序傳送至介面層；關閉管線即立即停止 GPU 運算並釋放獨佔 `VaultSession`。新金鑰庫支援透過登入鑰匙圈自動解鎖（既有密碼庫仍維持密碼輸入解鎖），選單列支援即時算力顯示與 4 段檔位切換，並整合 `IOKit` 防休眠鎖定、原生 Metal（`libpgpmint_metal.dylib`）與 MoltenVK Vulkan 驅動。目前採 ad-hoc 簽章（未公證），系統可能會要求在「系統設定」允許執行。
- **Linux（`src/PGPMint.Desktop`）**：全新設計的 **Avalonia 12.2.0** XAML 桌面應用（支援 `linux-x64` 與 `linux-arm64`），具備首次設定精靈、簡約三卡片主頁、獨立設定視窗、含詳細規則提示與原生 NVIDIA NVML（`libnvidia-ml.so.1`）即時功耗及 `MH/W` 遙測的 4 段功耗檔位、靜默 `/usr/bin/systemd-inhibit` 防休眠、`StatusNotifierWatcher` 系統匣支援，以及原生 Vulkan / OpenCL 顯示卡後端（自動排除 CPU 軟體模擬器）。
- **跨平台命令列工具（`src/PGPMint.Cli`）**：

```sh
# Windows 可直接執行 PGPMint-cli-*-win-x64.exe；macOS / Linux 請於解壓後執行 ./cli/pgpmint
./pgpmint devices --json
./pgpmint verify --backend vulkan       # Linux 原生 Vulkan 或 macOS MoltenVK
./pgpmint verify --backend metal        # macOS 原生 Metal
./pgpmint benchmark --backend vulkan --seconds 60 --rounds 3
./pgpmint search --pattern BEEF
./pgpmint export --key YOUR_KEY_ID --name "Your Name" --output ./exported
```

CLI 與可攜式金鑰庫使用 Argon2id + AES-256-GCM 加密儲存，並強制檢查 Unix `0700` 目錄權限與 macOS ACL 規則；所有經由 GPU 命中的候選結果皆會於 CPU 以 SHA-1 雙重核實後方寫入金鑰庫。

### Windows 應用程式

- **單檔免安裝可攜式執行檔**：提供 `win-x64` 與 `win-arm64` 單一壓縮 `.exe`（GUI 與 CLI 各自獨立為單檔），免安裝 Python、CUDA Toolkit、.NET 或 Windows App Runtime。
- **三層通用顯示卡運算引擎**：Zero-dependency 原生三層自動切換架構——優先以原生 **Vulkan Compute**（`vulkan-1.dll` + 程式化產生的 SPIR-V 1.3、`vulkan-sha1-four-v4`）發揮極致持續算力；若無 Vulkan 支援則改用原生 **`OpenCL.dll`**（`opencl-sha1-four-v4`）；再不支援則自動無縫退回 **DirectCompute**（`D3D11 cs_5_0`、`d3d11-sha1-four-v4`），支援多顯卡並行搜尋。
- **實測速率與 4 段功耗效率檔位**：首次執行或工作負載執行路徑（`1–4` 碼、`5–8` 碼、`9–16` 碼分組）或驅動變更時，自動執行 30 秒全曲線功耗效率校準；校準後可即時切換 `節能`、`平衡`、`效率`、`全速`，並於其他 3D 程式搶佔顯卡時自動彈性讓道。
- **零佔用系統匣與純 CPU 浮動微窗**：關閉視窗後自動駐留系統匣釋放 WinUI 介面顯卡資源，可選擇開啟純 CPU Direct2D/DirectWrite 浮動微窗（全螢幕遊戲或播放影片時自動隱藏）。
- **DPAPI 加密金鑰庫與 Git 專用 `.asc` 匯出**：私密種子以 Windows DPAPI（`CurrentUser`）本機加密儲存（最多 50 筆），可匯出符合 RFC 9580 / GnuPG 規格、具備 `Certify | Sign` 旗標的 `.secret.asc` 與 `.public.asc`（支援選填 AES-256-CFB 保護密碼）。
- **內建 10 國語言**：Windows、macOS 與 Linux 三平台桌面介面皆內建 `English`、`繁體中文`、`简体中文`、`日本語`、`한국어`、`Deutsch`、`Français`、`Español`、`Português (Brasil)` 與 `Русский`（Windows 版另支援於 `PGPMint.exe` 同層放置 `Strings/*.json` 擴充語系；CLI 維持英文以確保腳本與診斷一致）。

---

## 下載與快速開始

1. 前往 [GitHub Releases](https://github.com/Evanlau1798/PGPMint/releases) 下載對應平台的檔案：
   - **Windows GUI**：`PGPMint-*-win-x64.exe` 或 `PGPMint-*-win-arm64.exe`（免安裝單檔）
   - **Windows CLI**：`PGPMint-cli-*-win-x64.exe` 或 `PGPMint-cli-*-win-arm64.exe`（免安裝單檔）
   - **macOS**：`PGPMint-*-macos-arm64.tar.gz` 或 `PGPMint-*-macos-x64.tar.gz`（含 `PGPMint.app` 與 CLI）
   - **Linux**：`PGPMint-*-linux-x64.tar.gz` 或 `PGPMint-*-linux-arm64.tar.gz`（含 `PGPMint.Desktop` 與 CLI）
2. **初始設定**：輸入 1–16 碼十六進位 Key ID 尾綴（`0–9`、`A–F`，例如 `01 2345 6789`），勾選欲使用的實體顯示卡與語言後完成精靈設定。
3. **自動校準與搜尋**：點擊「開始」後會依需求自動完成 30 秒校準（校準期間命中一樣會即時核實保存）；完成後即可自由切換四段功耗檔位，點擊「停止」會等當前批次安全結束再儲存進度，關閉視窗可常駐系統匣／選單列繼續背景搜尋。
4. **匯出金鑰**：在結果清單點擊 `.asc` 按鈕即可匯出 `<KEYID>.secret.asc` 與 `<KEYID>.public.asc`。

---

## 匯出金鑰並用於 Git 簽章

匯出的金鑰內含自簽使用者 ID 與 `Certify | Sign`（`0x03`）權限旗標，可直接搭配 GnuPG、Git 與 GitHub Verified 提交驗證使用：

```powershell
# 1. 將匯出的私鑰匯入 GnuPG
gpg --import <KEYID>.secret.asc

# 2. 設定 Git 使用該 Key ID 簽署提交
git config --global user.signingkey <KEYID>
git config --global commit.gpgsign true

# 3. 將 <KEYID>.public.asc 內容新增至 GitHub -> Settings -> SSH and GPG keys
```

---

## 效能基準

以下 v4 引擎實測數據於 **NVIDIA GeForce RTX 3080**（Windows 11 x64、WDDM 驅動 `32.0.16.1088`、Release x64、`全速`）測得，包含 Ed25519 金鑰產生、SHA-1 排程預計算、GPU 派發與結果回讀：

| 後端 | Kernel | 134M 批次中位時間 | 536M 批次中位時間 | 端對端 Coordinator（全速） |
|---|---|---:|---:|---:|
| 原生 Vulkan（`vulkan-1.dll`） | `vulkan-sha1-four-v4`（`ubo-spirv-v4`） | **5.63–6.12 ms**（`21.93–23.82 GH/s`） | **23.11–23.14 ms**（`23.20–23.24 GH/s`） | **23.23 GH/s** |
| 原生 OpenCL（`OpenCL.dll`） | `opencl-sha1-four-v4`（`cmem0-v4`） | **5.68–6.27 ms**（`21.42–23.61 GH/s`） | **23.78–23.92 ms**（`22.44–22.58 GH/s`） | **23.19 GH/s** |
| DirectCompute（`D3D11 cs_5_0`） | `d3d11-sha1-four-v4`（`cb1-v4`） | **9.14–9.44 ms**（`14.21–14.69 GH/s`） | **37.33–37.38 ms**（`14.36–14.38 GH/s`） | 自動後備 |

其他已實測裝置：
- **Apple M3（macOS 15.6.1 `macos-arm64`）**：MoltenVK 實機約 **2.25 GH/s**。
- **Intel Alder Lake UHD（`ADL GT2`，Rocky Linux 9.8 `linux-x64`、Mesa 25.2.7 Vulkan）**：實機校準峰值約 **566.99 MH/s**（中位 **561.90 MH/s**）。

### NVIDIA GeForce RTX 5090（Linux x64，450 W 功耗上限）

於 **NVIDIA GeForce RTX 5090**（32 GiB VRAM、Ubuntu 22.04.5 LTS、驅動 `595.91.07`、容器限制 **450.00 W** 上限／原廠預設 `575.00 W`）搭配 `PGPMint v1.2.1` 與原生 Linux NVML（`libnvidia-ml.so.1`），以 13 位遮罩 `***1145141919810` 進行完整量測（算力支援與雲端規範提醒見[致謝與雲端 GPU 使用警示](#致謝與雲端-gpu-使用警示)）。

| 裝置 | 後端 | Kernel / Workgroup | Micro 中位數 | 端對端 Coordinator（全速） | 備註 |
|---|---|---|---:|---:|---|
| **RTX 5090（450 W）** | **OpenCL** | `opencl-sha1-four-v4` / `64` | **83.93 GH/s** | **76.63 GH/s** | 微基準最高 |
| **RTX 5090（450 W）** | **OpenCL** | `opencl-sha1-four-v4` / `128` | **83.32 GH/s** | **76.18 GH/s** | CLI `search` 預設組態（`cmem0-v4`） |
| **RTX 5090（450 W）** | **OpenCL** | `opencl-sha1-four-v4` / `256` | 81.60 GH/s | 75.97 GH/s | — |
| RTX 5090（450 W） | Vulkan | `vulkan-sha1-four-v4` / `64`–`256` | 56.75–57.42 GH/s | 54.86–55.03 GH/s | 需無頭 EGL ICD（`nvidia_egl_icd.json`） |
| RTX 3090 | OpenCL | `opencl-sha1-four-v4` / `128` | — | 24.97 GH/s | 先前 Linux 容器校準中位數 |
| RTX 3080（Windows） | Vulkan | `vulkan-sha1-four-v4` / `128` | 23.20–23.82 GH/s | 22.51–23.23 GH/s | 滿載 `346.61 W`（`64.95 MH/W`） |

<p align="center">
  <img src="assets/rtx5090-power-curve.svg" alt="PGPMint GPU 功耗能效曲線（功耗 W 對實測算力 GH/s），對比 RTX 5090（OpenCL，450 W 限制）與 RTX 3080（Vulkan）" width="100%" />
</p>

<details>
<summary><strong>展開檢視 RTX 5090 完整 21 點算力／每瓦效率曲線與 180 秒穩定性數據（OpenCL, 4x128）</strong></summary>

每一階梯（`100%` 全速、`95%`–`5%` 為 PGPMint 50 ms 週期切片限速）皆先穩定 3 秒再以 NVML 穩態量測 5 秒（`0%` 為待機 5 秒），所有計算候選皆實際推進進度（零浪費）。

| 目標比例 | 實測算力 (GH/s) | 功耗中位數 (W) | 每瓦獲得 (MH/W) | GPU 使用率 (%) | 核心 / 記憶體時脈 (MHz) | 溫度 (C) | P-State | 限電旗標 |
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
| **0%（待機）** | 0.000 | 28.32 | — | 0 | 195 / 405 | 52 | P8 | `None` |

- **為何 `100%` 全速即為最高每瓦效率點（`172.61 MH/W`）**：在 450 W 功耗牆下，滿載核心壓在 `2460 MHz` 高能效電壓區間，每瓦產出達 RTX 3080（`64.95 MH/W`）的 **2.66 倍**；而 50 ms 時間切片限速會使 GPU 停留在 `P1`（GDDR7 `13801 MHz`，5% 負載仍耗 `104.79 W`），且 `85%–95%` 短脈衝反而衝高至 `2610–2685 MHz` 並仍撞滿 `450 W`。降到 `40%` 時核心由 `2767` 降至 `2580 MHz`，功耗驟降 `32.7 W`，形成小幅效率轉折（`120.56 MH/W`）。
- **四檔位即時切換實測（5 秒穩定 + 10 秒 `MultiGpuCoordinator`）**：`全速` `76.680 GH/s @ 449.99 W`（`170.40 MH/W`）、`效率`（`62.6%`）`49.743 GH/s @ 362.77 W`（`137.12 MH/W`）、`平衡`（`31.3%`）`24.242 GH/s @ 217.69 W`（`111.36 MH/W`）、`節能`（`7.7%`）`6.136 GH/s @ 115.89 W`（`52.95 MH/W`）。
- **180 秒滿載穩定性**：1 秒區間平均 **`74.695 GH/s`**（中位 **`74.726 GH/s`**，UI 視窗由 `76.1` 平順收斂至 `74.4 GH/s`），平均功耗 **`448.71 W`**（**`166.47 MH/W`**），溫度穩定於 `70 C -> 80 C`（`0/180` 熱降頻、`179/180` `SwPowerCap`）。

</details>

### 平均預期等待時間（以 20 GH/s 估算）

| 尾綴長度 | 搜尋空間（$16^k$） | 平均預期時間 |
|---:|---:|---:|
| 8 碼 | $4.29 \times 10^{9}$ | 約 0.2 秒 |
| 10 碼 | $1.10 \times 10^{12}$ | 約 55 秒 |
| 12 碼 | $2.81 \times 10^{14}$ | 約 3.9 小時 |
| 13 碼 | $4.50 \times 10^{15}$ | 約 2.6 天 |
| 14 碼 | $7.21 \times 10^{16}$ | 約 41.7 天 |

### 與同類工具比較

| 功能特性 | PGPMint | CLI CUDA 工具 | CLI OpenCL 工具 | 瀏覽器 WebGL/WebGPU |
|---|---|---|---|---|
| 原生桌面介面與系統匣 | 是（WinUI 3 / SwiftUI / Avalonia） | 否 | 否 | 瀏覽器分頁（背景會降速） |
| 免裝 Python / CUDA Toolkit | 是（獨立套件／單檔執行檔） | 需裝 CUDA | 需裝 Runtime | 是 |
| 多層通用顯卡後備切換 | Vulkan + OpenCL + DirectCompute / Metal | 僅 NVIDIA | 需 OpenCL ICD | 受限於瀏覽器 |
| 多顯卡＋功耗效率檔位 | 是（`節能` / `平衡` / `效率` / `全速`） | 僅全速 | 僅全速 | 有限 |
| 加密金鑰庫與 Git `.asc` 匯出 | 是（DPAPI / Argon2id + 自簽） | 原始匯出 | 原始匯出 | 手動下載 |

---

## 系統需求

- **Windows**：Windows 10（組建 19041 / 2004 以上）或 Windows 11（`x64` 或 `ARM64`），配備支援 Direct3D 11 Feature Level 11.0+ 的實體顯示卡（建議安裝 Vulkan 1.1+ 或 OpenCL 驅動以獲得最高速度）。
- **macOS**：macOS 14 Sonoma 以上（`Apple Silicon arm64` 或 `Intel x64`），支援 Metal / MoltenVK 實體顯示卡。
- **Linux**：具備 X11 或 XWayland 圖形工作階段與硬體 Vulkan 1.1+／OpenCL ICD 驅動（無頭環境亦可單獨執行 CLI；完整相依套件見壓縮包內 `TESTING.txt`）。

---

## 從原始碼建置

需求環境：
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 SDK（`10.0.26100.0`，於 Windows 建置 `PGPMint.App`）或 Xcode Command Line Tools（於 macOS 建置 `PGPMint.MacApp`）

```powershell
# 執行完整測試（含本機實體 GPU 與 GnuPG 驗證）
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64

# 建置與分離執行無 GPU CI 測試（先 build 再 --no-build test）
dotnet build PGPMint.sln -p:Platform=x64 -c Release
dotnet test tests\PGPMint.Tests\PGPMint.Tests.csproj -p:Platform=x64 -c Release --no-build --filter "Category!=HardwareGpu"

# 透過跨平台打包腳本建置單平台發佈包
python scripts/packaging/package.py --rid win-x64 --tag v1.2.1 --platform x64 --scratch C:\pgpmint-dist
```

---

## 致謝與雲端 GPU 使用警示

特別感謝 **[gputw.ai](https://gputw.ai)** 提供本次 RTX 5090（與先前 RTX 3090）效能基準測試與算力／每瓦效率曲線量測所需的 GPU 算力支援。

> [!WARNING]
> **雲端／租用 GPU 反挖礦偵測風險提醒**：PGPMint 在搜尋 OpenPGP Key ID 時會執行長時間、高滿載的 GPU SHA-1 雜湊演算（`opencl-sha1-four-v4` / `vulkan-sha1-four-v4`），其運算負載特徵、核心占用與持續功耗曲線與**加密貨幣挖礦（Proof-of-Work Mining）極為相似**。**請勿在未事先確認供應商服務條款（Terms of Service / Acceptable Use Policy）或取得授權的情況下，直接於租用或雲端 GPU 實例啟動長時間計算**，否則極可能觸發供應商後台的疑似挖礦自動偵測機制而導致**帳號遭到封鎖或停權**。

---

## 參與貢獻

歡迎提交 Issue、翻譯檔與 Pull Request。送出前請確認相關測試與 Release 編譯皆無警告通過，並避免於程式碼與文件中加入表情符號。

## 授權條款

採用 [MIT License](LICENSE) 授權釋出。
