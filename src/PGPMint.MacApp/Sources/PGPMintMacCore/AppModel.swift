import Foundation
import Observation

@MainActor
public protocol PowerActivityManaging: AnyObject {
    var sleepProtected: Bool { get }
    func setActive(_ active: Bool)
}

@MainActor
public final class NullPowerActivityManager: PowerActivityManaging {
    public private(set) var sleepProtected: Bool = false
    public private(set) var isActive: Bool = false
    public init() {}
    public func setActive(_ active: Bool) {
        isActive = active
        sleepProtected = active
    }
}

@MainActor
@Observable
public final class AppModel {
    public let loc: LocalizationStore
    internal let transport: HostTransport
    internal let powerManager: PowerActivityManaging
    internal var eventTask: Task<Void, Never>?
    internal var noticeTask: Task<Void, Never>?
    internal var started = false
    private var lastMenuBarUpdate: Date = .distantPast
    private var lastMenuBarState: String = "Idle"

    public var isInitialized = false
    public var isBusy = false
    public var isUnlocked = false
    public var supportsAutomaticUnlock = true
    public var hasAutomaticMetadata = false
    public var hasLegacyVault = false
    public var vaultMode = "none"
    public var dataDirectory = ""
    public var showSetupWizard = false
    public var attemptingAutoUnlock = false

    public var activeTargetSuffix = ""
    public var activeFormattedPreview = "**** **** **** ****"
    public var activeFixedHexLength = 0

    public var draftTargetInput = "" { didSet { validateDraftTarget() } }
    public var draftFormattedPreview = "**** **** **** ****"
    public var isDraftValid = false
    public var hasDraftError = false
    public var draftErrorText = ""

    public var autoPauseOnHit = true
    public var minimizeToTrayOnClose = true
    public var selectedLanguageCode = LocalizationStore.autoCode
    public var draftMinCreationDate = ""
    public var draftMaxCreationDate = ""

    public var devices: [HostDeviceDto] = []
    public var gpuDiagnostics: [String] = []
    public var matches: [HostMatchDto] = []
    public var telemetry = HostTelemetryDto()
    public var needsFullCalibration = false

    public var errorText = ""
    public var transientNotice = ""
    public var sleepProtected = false
    public var menuBarTitle = ""

    public var onMenuBarStateChanged: (() -> Void)?
    public var onMatchNotification: ((String) -> Void)?

    public init(
        transport: HostTransport? = nil,
        loc: LocalizationStore? = nil,
        powerManager: PowerActivityManaging? = nil
    ) {
        self.transport = transport ?? ProcessHostTransport()
        self.loc = loc ?? LocalizationStore()
        self.powerManager = powerManager ?? NullPowerActivityManager()
    }

    public var hasHardwareGpu: Bool { !devices.isEmpty }
    public var hasEnabledGpu: Bool { devices.contains(where: { $0.isEnabled }) }
    public var canFinishSetup: Bool { hasHardwareGpu && isDraftValid && !isBusy }
    public var canEditTarget: Bool { telemetry.state == "Idle" || telemetry.state == "Faulted" }
    public var isSearching: Bool { telemetry.state == "Running" || telemetry.state == "Calibrating" }
    public var isPaused: Bool { telemetry.state == "Paused" }
    public var isStopping: Bool { telemetry.state == "Stopping" }
    public var shouldHideToMenuBarOnClose: Bool { isUnlocked && minimizeToTrayOnClose }

    public var hasMeasuredBaseline: Bool {
        telemetry.allEnabledFullyCalibrated && telemetry.totalBaselineHps > 0
    }

    public var displayTier: String {
        (telemetry.state == "Calibrating" || !hasMeasuredBaseline) ? "Max" : telemetry.activeTier
    }

    public var isTierSelectorEnabled: Bool {
        hasHardwareGpu && !isBusy && telemetry.state != "Calibrating" && telemetry.state != "Stopping" && hasMeasuredBaseline
    }

    public var canApplyDraftTarget: Bool {
        guard canEditTarget && !isBusy && isDraftValid else { return false }
        if case .valid(let suffix, _, _, _) = Formatting.validateKeyIdSuffix(draftTargetInput) {
            return suffix.caseInsensitiveCompare(activeTargetSuffix) != .orderedSame
        }
        return false
    }

    public var canRecalibrate: Bool {
        hasHardwareGpu && hasEnabledGpu && canEditTarget && !isBusy && !activeTargetSuffix.isEmpty
    }

    public var activeTargetFound: Bool {
        guard telemetry.state != "Calibrating" && telemetry.state != "Faulted" else { return false }
        guard case .valid(_, let mask, _, _) = Formatting.validateKeyIdSuffix(activeTargetSuffix) else { return false }
        return matches.contains { m in
            m.matchedPattern.caseInsensitiveCompare(mask) == .orderedSame
                || m.keyId.uppercased().hasSuffix(activeTargetSuffix.uppercased())
        }
    }

    public var actualRateNumber: String { Formatting.formatHashRateValue(telemetry.totalActualHps) }
    public var actualRateUnit: String { Formatting.formatHashRateUnit(telemetry.totalActualHps) }

    public var progressPercent: Double {
        if telemetry.state == "Calibrating" {
            let total = max(1.0, telemetry.fullCalibrationDurationSeconds)
            return min(max((1.0 - telemetry.calibrationRemainingSeconds / total) * 100.0, 0.0), 100.0)
        }
        if activeTargetFound { return 100.0 }
        return min(max(telemetry.cumulativeProbability * 100.0, 0.0), 100.0)
    }

    public var probabilityText: String {
        activeTargetFound
            ? loc.text("Found")
            : Formatting.formatLabeledProbability(telemetry.cumulativeProbability, prefix: loc.text("ProbabilityPrefix"))
    }

    public var singleHashProbabilityTooltip: String {
        guard activeFixedHexLength > 0 else { return "" }
        return loc.format("SingleHashProbabilityFormat", Formatting.formatSingleHashProbabilityDetail(activeFixedHexLength))
    }

    public var checkedCountText: String { Formatting.formatCheckedCount(telemetry.totalChecked) }
    public var checkedCountTooltip: String { Formatting.formatCheckedCountExact(telemetry.totalChecked) }

    public var liveEtaText: String {
        activeTargetFound ? "\u{2014}" : Formatting.formatEta(seconds: telemetry.estimatedMeanWaitSeconds, formats: loc.etaFormats)
    }

    public var efficiencyBadgeText: String {
        if telemetry.state == "Stopping" { return loc.text("Stopping") }
        if isSearching {
            if telemetry.state == "Running" && telemetry.isOverloadYielding && telemetry.totalActualHps <= 0 {
                return loc.text("YieldingBadge")
            }
            if telemetry.hasPowerSensor && telemetry.totalPowerWatts > 0 {
                let hpw = telemetry.totalActualHps > 0 ? telemetry.totalActualHps / telemetry.totalPowerWatts : 0
                return Formatting.formatEfficiencyPerWatt(hpw)
            }
            if telemetry.hasGpuUtilSensor {
                let pct = Int(min(max(telemetry.aggregateGpuUtilPercent.rounded(), 0), 100))
                return "\(pct)%"
            }
            return loc.tierLabel(displayTier)
        }
        switch telemetry.state {
        case "Paused": return loc.text("Paused")
        case "Faulted": return loc.text("Error")
        default: return loc.text("Idle")
        }
    }

    public var efficiencyBadgeTooltip: String? {
        guard isSearching else { return nil }
        if telemetry.hasPowerSensor && telemetry.totalPowerWatts > 0 {
            let watts = Formatting.formatWatts(telemetry.totalPowerWatts)
            let hpw = telemetry.totalActualHps > 0 ? telemetry.totalActualHps / telemetry.totalPowerWatts : 0
            let eff = Formatting.formatEfficiencyPerWatt(hpw)
            return loc.format("PowerTooltipFormat", watts, eff)
        }
        if telemetry.hasGpuUtilSensor {
            let pct = Int(min(max(telemetry.aggregateGpuUtilPercent.rounded(), 0), 100))
            return loc.format("GpuLoadTooltipFormat", "\(pct)")
        }
        return nil
    }

    public var primaryButtonText: String {
        switch telemetry.state {
        case "Running", "Paused": return loc.text("Stop")
        case "Calibrating":
            let remain = Int(ceil(max(0, telemetry.calibrationRemainingSeconds)))
            return "\(loc.text("Stop")) \u{00B7} \(loc.format("HudMeasuringFormat", "\(remain)"))"
        case "Stopping": return loc.text("Stopping")
        default: return loc.text("Start")
        }
    }

    public var canPrimaryAction: Bool {
        if isBusy || isStopping { return false }
        if isSearching || isPaused { return true }
        return hasHardwareGpu && hasEnabledGpu && !activeTargetSuffix.isEmpty
    }

    public var statusIndicatorColorName: String {
        switch telemetry.state {
        case "Running": return "green"
        case "Calibrating": return "orange"
        case "Paused": return "yellow"
        case "Faulted": return "red"
        default: return "gray"
        }
    }

    public func validateDraftTarget() {
        switch Formatting.validateKeyIdSuffix(draftTargetInput) {
        case .valid(_, _, let formatted, _):
            draftFormattedPreview = formatted
            isDraftValid = true
            hasDraftError = false
            draftErrorText = ""
        case .empty:
            draftFormattedPreview = "**** **** **** ****"
            isDraftValid = false
            hasDraftError = false
            draftErrorText = ""
        case .invalidCharacters:
            draftFormattedPreview = "**** **** **** ****"
            isDraftValid = false
            hasDraftError = true
            draftErrorText = loc.text("ValidationInvalidChars")
        case .tooLong:
            draftFormattedPreview = "**** **** **** ****"
            isDraftValid = false
            hasDraftError = true
            draftErrorText = loc.text("ValidationTooLong")
        }
    }

    public func updateMenuBarTitle(_ t: HostTelemetryDto, now: Date) {
        let stateChanged = t.state != lastMenuBarState
        let active = t.state == "Running" || t.state == "Calibrating"
        let delta = now.timeIntervalSince(lastMenuBarUpdate)
        if active && !stateChanged && delta >= 0 && delta < 1.0 {
            return
        }
        lastMenuBarState = t.state
        lastMenuBarUpdate = now

        let newTitle: String
        switch t.state {
        case "Running":
            if t.isOverloadYielding && t.totalActualHps <= 0 {
                newTitle = loc.text("YieldingBadge")
            } else {
                newTitle = Formatting.formatHashRateCompact(t.totalActualHps)
            }
        case "Calibrating":
            let remain = Int(ceil(max(0, t.calibrationRemainingSeconds)))
            newTitle = t.totalActualHps > 0
                ? "\(Formatting.formatHashRateCompact(t.totalActualHps)) (\(remain)s)"
                : "\(remain)s"
        default:
            newTitle = ""
        }

        if newTitle != menuBarTitle || stateChanged {
            menuBarTitle = newTitle
            onMenuBarStateChanged?()
        }
    }
}
