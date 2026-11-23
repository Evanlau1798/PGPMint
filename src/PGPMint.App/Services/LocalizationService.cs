using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PGPMint.Core.Search;

namespace PGPMint.App.Services;

public sealed class LocalizationService : ObservableObject
{
    private string _languageCode = "zh-Hant";
    private string _settingValue = "auto";

    public string SettingValue => _settingValue;
    public bool IsChinese => string.Equals(_languageCode, "zh-Hant", StringComparison.OrdinalIgnoreCase);

    public LocalizationService()
    {
        ApplySetting("auto");
    }

    public void ApplySetting(string setting)
    {
        _settingValue = string.IsNullOrWhiteSpace(setting) ? "auto" : setting;
        _languageCode = _settingValue switch
        {
            "zh-Hant" => "zh-Hant",
            "en-US" => "en-US",
            _ => IsSystemTraditionalChinese() ? "zh-Hant" : "en-US"
        };

        OnPropertyChanged(string.Empty);
    }

    private static bool IsSystemTraditionalChinese()
    {
        CultureInfo ui = CultureInfo.CurrentUICulture;
        return ui.Name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
               ui.Name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
               ui.Name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
               ui.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase);
    }

    private string T(string zh, string en) => IsChinese ? zh : en;

    public string GetKeyIdValidationMessage(KeyIdValidationError error) => error switch
    {
        KeyIdValidationError.InvalidCharacters => T("含不合法字元，僅允許 0–9 與 A–F", "Invalid characters. Only 0-9 and A-F are allowed."),
        KeyIdValidationError.TooLong => T("最多僅支援 16 碼十六進位字元", "Maximum length is 16 hex characters."),
        _ => string.Empty
    };

    public string SuffixRuleHint => T(
        "僅支援 1–16 碼十六進位字元（0–9、A–F），比對 16 碼 Key ID 尾端",
        "1-16 hex characters (0-9, A-F), matched against the end of the 16-char Key ID");

    public string TargetPlaceholder => T("例如 1145141919810", "e.g. DEADBEEF");

    public string SetupStep1Title => T("顯示卡估速", "GPU Benchmark");
    public string SetupStep2Title => T("設定 Key ID 尾碼", "Set Key ID Suffix");
    public string Next => T("下一步", "Next");
    public string Back => T("上一步", "Back");
    public string Finish => T("完成", "Finish");
    public string Apply => T("套用", "Apply");
    public string Applied => T("已套用", "Applied");

    public string Settings => T("設定", "Settings");
    public string SectionTarget => T("目標 Key ID 尾碼", "Target Key ID Suffix");
    public string SectionGpus => T("顯示卡", "GPUs");
    public string SectionPreferences => T("系統", "System");
    public string StopToEditHint => T("停止搜尋後可修改", "Stop search to edit");

    public string Start => T("開始", "Start");
    public string Pause => T("暫停", "Pause");
    public string Resume => T("繼續", "Resume");
    public string Stop => T("停止", "Stop");
    public string FullSpeed => T("全速", "MAX");
    public string Paused => T("已暫停", "PAUSED");
    public string Idle => T("待機", "IDLE");
    public string Probing => T("估速", "PROBE");
    public string Calibrating => T("校準", "CAL");
    public string Stopping => T("停止中", "STOPPING");
    public string Error => T("錯誤", "ERROR");

    public string Copy => T("複製", "Copy");
    public string Copied => T("已複製", "Copied");
    public string Delete => T("刪除", "Delete");
    public string DeleteConfirmTitle => T("刪除已保存私鑰？", "Delete saved key?");
    public string Open => T("開啟", "Open");
    public string Quit => T("結束", "Quit");

    public string ExportTitle => T("匯出 OpenPGP 金鑰", "Export OpenPGP Key");
    public string NamePlaceholder => T("姓名", "Name");
    public string EmailPlaceholder => T("Email", "Email");
    public string PassphrasePlaceholder => T("保護密碼（選填）", "Passphrase (optional)");
    public string Save => T("儲存", "Save");
    public string Cancel => T("取消", "Cancel");

    public string SettingLanguage => T("語言", "Language");
    public string SettingAutoPauseOnHit => T("命中後自動暫停", "Pause on match");
    public string SettingMinimizeToTray => T("關閉時常駐系統匣", "Keep in tray on close");
    public string SettingShowMiniHud => T("背景顯示浮動微窗", "Floating HUD in tray");
    public string SettingStartWithWindows => T("開機自動啟動", "Launch at startup");
    public string SettingRecalibrate30s => T("重新執行 30 秒校準", "Recalibrate (30s)");
    public string TrayMiniHud => T("浮動微窗", "Floating HUD");

    public string NoGpuError => T("未偵測到實體顯示卡", "No hardware GPU detected");
    public string HitNotificationTitle => T("PGPMint 命中金鑰", "PGPMint Key Matched");
    public string ExportSuccess => T("已匯出私鑰與公鑰 (.asc)", "Exported secret & public key (.asc)");
}
