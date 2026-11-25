import Foundation
import Observation

public struct LanguageOption: Identifiable, Equatable, Hashable, Sendable {
    public let code: String
    public let displayName: String
    public var id: String { code }
}

@MainActor
@Observable
public final class LocalizationStore {
    public static let autoCode = "auto"

    public private(set) var settingValue: String = autoCode
    public private(set) var effectiveLanguage: String = "en-US"
    private var catalogs: [String: [String: String]] = [:]

    public var isChinese: Bool { effectiveLanguage == "zh-Hant" }

    public init(bundle: Bundle = .module) {
        loadCatalogs(from: bundle)
        applySetting(Self.autoCode)
    }

    public func applySetting(_ code: String) {
        let normalized = code.trimmingCharacters(in: .whitespacesAndNewlines)
        if normalized == "zh-Hant" || normalized == "en-US" {
            settingValue = normalized
            effectiveLanguage = normalized
        } else {
            settingValue = Self.autoCode
            effectiveLanguage = Self.detectSystemLanguage()
        }
    }

    public var languageOptions: [LanguageOption] {
        [
            LanguageOption(code: Self.autoCode, displayName: text("LanguageAuto")),
            LanguageOption(code: "en-US", displayName: "English"),
            LanguageOption(code: "zh-Hant", displayName: "\u{7E41}\u{9AD4}\u{4E2D}\u{6587}")
        ]
    }

    public func text(_ key: String) -> String {
        if let value = catalogs[effectiveLanguage]?[key] {
            return value
        }
        if let fallback = catalogs["en-US"]?[key] {
            return fallback
        }
        return key
    }

    public func format(_ key: String, _ args: String...) -> String {
        var result = text(key)
        for (index, arg) in args.enumerated() {
            result = result.replacingOccurrences(of: "{\(index)}", with: arg)
        }
        return result
    }

    public func tierLabel(_ tier: String) -> String {
        switch tier {
        case "Eco": return text("TierEco")
        case "Balanced": return text("TierBalanced")
        case "Efficiency": return text("TierEfficiency")
        default: return text("TierMax")
        }
    }

    public func tierTooltip(_ tier: String) -> String {
        switch tier {
        case "Eco": return text("TierEcoTooltip")
        case "Balanced": return text("TierBalancedTooltip")
        case "Efficiency": return text("TierEfficiencyTooltip")
        default: return text("TierMaxTooltip")
        }
    }

    private static func detectSystemLanguage() -> String {
        for preferred in Locale.preferredLanguages {
            let lower = preferred.lowercased()
            if lower.hasPrefix("zh") {
                return "zh-Hant"
            }
            if lower.hasPrefix("en") {
                return "en-US"
            }
        }
        return "en-US"
    }

    private func loadCatalogs(from bundle: Bundle) {
        let pairs: [(String, [String])] = [
            ("en-US", ["en-US", "Desktop.en-US"]),
            ("zh-Hant", ["zh-Hant", "Desktop.zh-Hant"])
        ]
        for (lang, files) in pairs {
            var merged: [String: String] = [:]
            for name in files {
                guard let url = bundle.url(forResource: name, withExtension: "json", subdirectory: "Resources")
                    ?? bundle.url(forResource: name, withExtension: "json"),
                    let data = try? Data(contentsOf: url),
                    let parsed = try? JSONDecoder().decode(StringCatalogFile.self, from: data)
                else { continue }
                for (k, v) in parsed.strings {
                    merged[k] = v
                }
            }
            catalogs[lang] = merged
        }
    }

    private struct StringCatalogFile: Decodable {
        let strings: [String: String]
    }
}
