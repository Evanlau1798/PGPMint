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

    public static let builtInLocaleOrder: [String] = [
        "en-US",
        "zh-Hant",
        "zh-Hans",
        "ja-JP",
        "ko-KR",
        "de-DE",
        "fr-FR",
        "es-ES",
        "pt-BR",
        "ru-RU"
    ]

    public private(set) var settingValue: String = autoCode
    public private(set) var effectiveLanguage: String = "en-US"
    private var catalogs: [String: [String: String]] = [:]
    private var nativeNames: [String: String] = [:]

    public var isChinese: Bool { effectiveLanguage.hasPrefix("zh") }

    public init(bundle: Bundle? = nil) {
        let resolved = bundle ?? Self.resolveResourceBundle()
        loadCatalogs(from: resolved)
        applySetting(Self.autoCode)
    }

    private static func resolveResourceBundle() -> Bundle {
        if let resUrl = Bundle.main.resourceURL?.appendingPathComponent("PGPMintMacApp_PGPMintMacCore.bundle"),
           let appBundle = Bundle(url: resUrl) {
            return appBundle
        }
        return .module
    }

    public func applySetting(_ code: String) {
        let normalized = code.trimmingCharacters(in: .whitespacesAndNewlines)
        if let canonical = Self.builtInLocaleOrder.first(where: { $0.caseInsensitiveCompare(normalized) == .orderedSame }),
           catalogs[canonical] != nil {
            settingValue = canonical
            effectiveLanguage = canonical
        } else {
            settingValue = Self.autoCode
            effectiveLanguage = Self.detectSystemLanguage()
        }
    }

    public var languageOptions: [LanguageOption] {
        var options = [LanguageOption(code: Self.autoCode, displayName: text("LanguageAuto"))]
        for code in Self.builtInLocaleOrder where catalogs[code] != nil {
            options.append(LanguageOption(code: code, displayName: nativeNames[code] ?? code))
        }
        return options
    }

    public var etaFormats: EtaFormatStrings {
        EtaFormatStrings(
            lessThanTenthSec: text("EtaLessThanTenthSec"),
            secondsFormat: text("EtaSecondsFormat"),
            minutesFormat: text("EtaMinutesFormat"),
            hoursFormat: text("EtaHoursFormat"),
            daysFormat: text("EtaDaysFormat"),
            yearsFormat: text("EtaYearsFormat"),
            kiloYearsFormat: text("EtaKiloYearsFormat"),
            overMillionYears: text("EtaOverMillionYears")
        )
    }

    public func text(_ key: String) -> String {
        if let value = catalogs[effectiveLanguage]?[key], !value.isEmpty {
            return value
        }
        if let fallback = catalogs["en-US"]?[key], !fallback.isEmpty {
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

    public static func detectSystemLanguage(preferredLanguages: [String] = Locale.preferredLanguages) -> String {
        for preferred in preferredLanguages {
            let normalized = preferred.trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "_", with: "-")
            let lower = normalized.lowercased()
            if lower.isEmpty { continue }

            if let exact = builtInLocaleOrder.first(where: { $0.lowercased() == lower }) {
                return exact
            }

            if lower == "zh" || lower.hasPrefix("zh-") {
                let preferTraditional =
                    lower == "zh-tw" || lower.hasPrefix("zh-tw-") ||
                    lower == "zh-hk" || lower.hasPrefix("zh-hk-") ||
                    lower == "zh-mo" || lower.hasPrefix("zh-mo-") ||
                    lower == "zh-hant" || lower.hasPrefix("zh-hant-")
                return preferTraditional ? "zh-Hant" : "zh-Hans"
            }

            let subtag = String(lower.split(separator: "-", maxSplits: 1).first ?? Substring(lower))
            if let subtagMatch = builtInLocaleOrder.first(where: {
                let builtInLower = $0.lowercased()
                return builtInLower == subtag || builtInLower.hasPrefix(subtag + "-")
            }) {
                return subtagMatch
            }
        }
        return "en-US"
    }

    private func loadCatalogs(from bundle: Bundle) {
        for lang in Self.builtInLocaleOrder {
            var merged: [String: String] = [:]
            for name in [lang, "Desktop.\(lang)"] {
                guard let url = bundle.url(forResource: name, withExtension: "json", subdirectory: "Resources")
                    ?? bundle.url(forResource: name, withExtension: "json"),
                    let data = try? Data(contentsOf: url),
                    let parsed = try? JSONDecoder().decode(StringCatalogFile.self, from: data)
                else { continue }
                if let nativeName = parsed._meta?.nativeName, !nativeName.isEmpty {
                    nativeNames[lang] = nativeName
                }
                for (k, v) in parsed.strings where !k.isEmpty && !v.isEmpty {
                    merged[k] = v
                }
            }
            if !merged.isEmpty {
                catalogs[lang] = merged
            }
        }
    }

    private struct StringCatalogMeta: Decodable {
        let code: String?
        let nativeName: String?
    }

    private struct StringCatalogFile: Decodable {
        let _meta: StringCatalogMeta?
        let strings: [String: String]
    }
}
