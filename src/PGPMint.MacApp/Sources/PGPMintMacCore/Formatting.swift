import Foundation

public enum KeyIdValidationStatus: Equatable {
    case valid(suffix: String, canonicalMask: String, formattedDisplay: String, fixedHexLength: Int)
    case empty
    case invalidCharacters
    case tooLong
}

public struct EtaFormatStrings: Equatable, Sendable {
    public let lessThanTenthSec: String
    public let secondsFormat: String
    public let minutesFormat: String
    public let hoursFormat: String
    public let daysFormat: String
    public let yearsFormat: String
    public let kiloYearsFormat: String
    public let overMillionYears: String

    public init(
        lessThanTenthSec: String,
        secondsFormat: String,
        minutesFormat: String,
        hoursFormat: String,
        daysFormat: String,
        yearsFormat: String,
        kiloYearsFormat: String,
        overMillionYears: String
    ) {
        self.lessThanTenthSec = lessThanTenthSec
        self.secondsFormat = secondsFormat
        self.minutesFormat = minutesFormat
        self.hoursFormat = hoursFormat
        self.daysFormat = daysFormat
        self.yearsFormat = yearsFormat
        self.kiloYearsFormat = kiloYearsFormat
        self.overMillionYears = overMillionYears
    }

    public static let english = EtaFormatStrings(
        lessThanTenthSec: "<0.1s",
        secondsFormat: "~{0}s",
        minutesFormat: "~{0}m",
        hoursFormat: "~{0}h",
        daysFormat: "~{0}d",
        yearsFormat: "~{0}y",
        kiloYearsFormat: "~{0}ky",
        overMillionYears: ">1My"
    )

    public static let traditionalChinese = EtaFormatStrings(
        lessThanTenthSec: "<0.1 \u{79D2}",
        secondsFormat: "~{0} \u{79D2}",
        minutesFormat: "~{0} \u{5206}",
        hoursFormat: "~{0} \u{5C0F}\u{6642}",
        daysFormat: "~{0} \u{5929}",
        yearsFormat: "~{0} \u{5E74}",
        kiloYearsFormat: "~{0} \u{5343}\u{5E74}",
        overMillionYears: ">100 \u{842C}\u{5E74}"
    )
}

public enum Formatting {
    private static let superscriptDigits: [Character] = [
        "\u{2070}", "\u{00B9}", "\u{00B2}", "\u{00B3}", "\u{2074}",
        "\u{2075}", "\u{2076}", "\u{2077}", "\u{2078}", "\u{2079}"
    ]

    public static func toSuperscript(_ value: Int) -> String {
        let raw = String(value)
        var result = ""
        for ch in raw {
            if ch == "-" {
                result.append("\u{207B}")
            } else if let digit = ch.wholeNumberValue, digit >= 0, digit <= 9 {
                result.append(superscriptDigits[digit])
            }
        }
        return result
    }

    public static func validateKeyIdSuffix(_ input: String) -> KeyIdValidationStatus {
        if input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return .empty
        }
        var cleaned = ""
        var hasInvalid = false
        var hasSeparatorOnly = false
        var fixedCount = 0
        for scalar in input.unicodeScalars {
            if CharacterSet.whitespacesAndNewlines.contains(scalar) {
                continue
            }
            let ch = Character(scalar)
            if ch == "-" || ch == "_" {
                hasSeparatorOnly = true
                continue
            }
            let upper = Character(String(ch).uppercased())
            if upper == "*" {
                cleaned.append("*")
            } else if isHex(upper) {
                cleaned.append(upper)
                fixedCount += 1
            } else {
                hasInvalid = true
                cleaned.append(upper)
            }
        }
        if cleaned.isEmpty {
            return hasSeparatorOnly ? .invalidCharacters : .empty
        }
        if hasInvalid || fixedCount == 0 {
            return .invalidCharacters
        }
        if cleaned.count > 16 {
            return .tooLong
        }
        let padCount = max(0, 16 - cleaned.count)
        let canonical = String(repeating: "*", count: padCount) + cleaned
        let suffix = String(canonical.drop(while: { $0 == "*" }))
        let formatted = formatSpaced16(canonical)
        return .valid(suffix: suffix, canonicalMask: canonical, formattedDisplay: formatted, fixedHexLength: fixedCount)
    }

    public static func previewForDraft(_ input: String) -> String {
        switch validateKeyIdSuffix(input) {
        case .valid(_, _, let formatted, _):
            return formatted
        default:
            return "**** **** **** ****"
        }
    }

    public static func formatKeyIdSpaced(_ keyId16: String) -> String {
        guard keyId16.count == 16 else { return keyId16 }
        return formatSpaced16(keyId16)
    }

    private static func formatSpaced16(_ s: String) -> String {
        let chars = Array(s)
        guard chars.count == 16 else { return s }
        return "\(String(chars[0..<4])) \(String(chars[4..<8])) \(String(chars[8..<12])) \(String(chars[12..<16]))"
    }

    private static func isHex(_ ch: Character) -> Bool {
        ("0"..."9").contains(ch) || ("A"..."F").contains(ch)
    }

    public static func formatHashRateValue(_ hps: Double) -> String {
        if !hps.isFinite || hps <= 0 { return "0.00" }
        let ghs = hps / 1_000_000_000.0
        if ghs >= 100.0 { return String(format: "%.1f", ghs) }
        if ghs >= 1.0 { return String(format: "%.2f", ghs) }
        if ghs >= 0.01 { return String(format: "%.3f", ghs) }
        return String(format: "%.1f", hps / 1_000_000.0)
    }

    public static func formatHashRateUnit(_ hps: Double) -> String {
        (hps > 0 && hps < 10_000_000.0) ? "MH/s" : "GH/s"
    }

    public static func formatHashRateCompact(_ hps: Double) -> String {
        if !hps.isFinite || hps <= 0 { return "0.00 GH/s" }
        if hps < 10_000_000.0 {
            return String(format: "%.1f MH/s", hps / 1_000_000.0)
        }
        let ghs = hps / 1_000_000_000.0
        return ghs >= 100.0 ? String(format: "%.1f GH/s", ghs) : String(format: "%.2f GH/s", ghs)
    }

    public static func formatWatts(_ watts: Double) -> String {
        if !watts.isFinite || watts <= 0 { return "0 W" }
        return watts >= 100.0 ? String(format: "%.0f W", watts) : String(format: "%.1f W", watts)
    }

    public static func formatEfficiencyPerWatt(_ hashesPerWatt: Double) -> String {
        if !hashesPerWatt.isFinite || hashesPerWatt <= 0 { return "0.0 MH/W" }
        let mhpw = hashesPerWatt / 1_000_000.0
        if (mhpw * 10.0).rounded() / 10.0 >= 100.0 {
            let ghpw = hashesPerWatt / 1_000_000_000.0
            if (ghpw * 100.0).rounded() / 100.0 >= 100.0 {
                return String(format: "%.1f GH/W", ghpw)
            }
            return String(format: "%.2f GH/W", ghpw)
        }
        if (mhpw * 100.0).rounded() / 100.0 >= 1.0 {
            return String(format: "%.1f MH/W", mhpw)
        }
        return String(format: "%.2f MH/W", mhpw)
    }

    public static func formatCheckedCount(_ count: UInt64) -> String {
        if count < 10_000 {
            return "\(formatDecimalInteger(count)) H"
        }
        var exp = Int(floor(log10(Double(count))))
        var mantissa = Double(count) / pow(10.0, Double(exp))
        if (mantissa * 100.0).rounded() / 100.0 >= 10.0 {
            exp += 1
            mantissa = 1.0
        }
        return String(format: "%.2f \u{00D7} 10^%d H", mantissa, exp)
    }

    public static func formatCheckedCountExact(_ count: UInt64) -> String {
        "\(formatDecimalInteger(count)) H"
    }

    private static func formatDecimalInteger(_ count: UInt64) -> String {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.usesGroupingSeparator = true
        formatter.groupingSeparator = ","
        formatter.groupingSize = 3
        formatter.locale = Locale(identifier: "en_US")
        return formatter.string(from: NSNumber(value: count)) ?? "\(count)"
    }

    public static func formatProbability(_ probability: Double) -> String {
        if !probability.isFinite || probability <= 0 { return "0.00%" }
        let pct = min(max(probability, 0.0), 1.0) * 100.0
        if pct >= 99.995 { return "99.99%" }
        if pct >= 10.0 { return String(format: "%.2f%%", pct) }
        if pct >= 0.01 { return String(format: "%.3f%%", pct) }
        if pct >= 0.0001 { return String(format: "%.4f%%", pct) }
        var exp = Int(floor(log10(pct)))
        var mantissa = exp < -300
            ? (pct * 1e20) / pow(10.0, Double(exp + 20))
            : pct / pow(10.0, Double(exp))
        if (mantissa * 100.0).rounded() / 100.0 >= 10.0 {
            exp += 1
            mantissa = 1.0
        }
        return String(format: "%.2f\u{00D7}10^%d%%", mantissa, exp)
    }

    public static func formatLabeledProbability(_ probability: Double, prefix: String) -> String {
        let value = formatProbability(probability)
        let trimmed = prefix.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? value : "\(trimmed) \(value)"
    }

    public static func formatSingleHashProbabilityDetail(_ fixedHexLength: Int) -> String {
        let k = min(max(fixedHexLength, 1), 16)
        let singleProb = pow(16.0, Double(-k))
        var exp = Int(floor(log10(singleProb)))
        var mantissa = singleProb / pow(10.0, Double(exp))
        if (mantissa * 100.0).rounded() / 100.0 >= 10.0 {
            exp += 1
            mantissa = 1.0
        }
        return String(format: "1 / 16%@ (\u{2248} %.2f \u{00D7} 10%@)", toSuperscript(k), mantissa, toSuperscript(exp))
    }

    public static func formatEta(seconds: Double?, isChinese: Bool) -> String {
        formatEta(seconds: seconds, formats: isChinese ? .traditionalChinese : .english)
    }

    public static func formatEta(seconds: Double?, formats: EtaFormatStrings) -> String {
        guard let seconds, seconds.isFinite, seconds >= 0 else { return "\u{2014}" }
        if seconds < 0.1 { return formats.lessThanTenthSec }
        if seconds < 60.0 { return formatEtaNumber(formats.secondsFormat, value: seconds) }
        let minutes = seconds / 60.0
        if minutes < 60.0 { return formatEtaNumber(formats.minutesFormat, value: minutes) }
        let hours = minutes / 60.0
        if hours < 24.0 { return formatEtaNumber(formats.hoursFormat, value: hours) }
        let days = hours / 24.0
        if days < 365.25 { return formatEtaNumber(formats.daysFormat, value: days) }
        let years = days / 365.25
        if years < 1_000.0 { return formatEtaNumber(formats.yearsFormat, value: years) }
        if years < 1_000_000.0 { return formatEtaNumber(formats.kiloYearsFormat, value: years / 1_000.0) }
        return formats.overMillionYears
    }

    private static func formatEtaNumber(_ template: String, value: Double) -> String {
        let num = String(format: "%.1f", locale: Locale(identifier: "en_US_POSIX"), value)
        return template.replacingOccurrences(of: "{0}", with: num)
    }
}
