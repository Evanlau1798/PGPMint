import Foundation
import Testing
@testable import PGPMintMacCore

@Suite("PGPMintMacCore")
struct PGPMintMacCoreTests {
    @Test
    func formattingParityMatchesCoreHelpers() {
        #expect(Formatting.formatHashRateValue(0) == "0.00")
        #expect(Formatting.formatHashRateUnit(0) == "GH/s")
        #expect(Formatting.formatHashRateValue(2_242_700_000) == "2.24")
        #expect(Formatting.formatHashRateUnit(2_242_700_000) == "GH/s")
        #expect(Formatting.formatHashRateCompact(5_400_000) == "5.4 MH/s")
        #expect(Formatting.formatCheckedCount(9_999) == "9,999 H")
        #expect(Formatting.formatCheckedCount(10_000) == "1.00 \u{00D7} 10^4 H")
        #expect(Formatting.formatProbability(0.12345) == "12.35%")
        #expect(Formatting.formatEta(seconds: nil, isChinese: false) == "\u{2014}")
        #expect(Formatting.formatEta(seconds: 42.3, isChinese: false) == "~42.3s")
        #expect(Formatting.formatEta(seconds: 42.3, isChinese: true) == "~42.3 \u{79D2}")
        #expect(Formatting.formatKeyIdSpaced("0123456789ABCDEF") == "0123 4567 89AB CDEF")

        #expect(
            Formatting.validateKeyIdSuffix("dead beef")
                == .valid(suffix: "DEADBEEF", canonicalMask: "********DEADBEEF", formattedDisplay: "**** **** DEAD BEEF", fixedHexLength: 8)
        )
        #expect(Formatting.validateKeyIdSuffix("XYZ") == .invalidCharacters)
        #expect(Formatting.validateKeyIdSuffix("0123456789ABCDEF0") == .tooLong)
    }

    @Test
    @MainActor
    func localizationLoadsEnglishAndTraditionalChinese() {
        let loc = LocalizationStore()
        loc.applySetting("en-US")
        #expect(loc.text("Start") == "Start")
        #expect(loc.text("SystemVaultHint").contains("login keychain"))

        loc.applySetting("zh-Hant")
        #expect(loc.text("Start") == "\u{958B}\u{59CB}")
        #expect(loc.isChinese)
    }

    @Test
    @MainActor
    func appModelHonestPowerBadgeAndMenuBarOneHzThrottle() async {
        let mock = MockHostTransport()
        let power = NullPowerActivityManager()
        let loc = LocalizationStore()
        loc.applySetting("en-US")
        let model = AppModel(transport: mock, loc: loc, powerManager: power)

        await model.bootstrap()
        #expect(model.isUnlocked)
        #expect(model.showSetupWizard)

        model.draftTargetInput = "CAFE"
        #expect(model.canFinishSetup)
        await model.completeSetupAndStart()
        #expect(!model.showSetupWizard)

        let t0 = Date(timeIntervalSince1970: 1000)
        let runningNoSensor = HostTelemetryDto(
            state: "Running",
            totalActualHps: 2_240_000_000,
            totalBaselineHps: 2_250_000_000,
            allEnabledFullyCalibrated: true,
            activeTier: "Max",
            totalPowerWatts: 0,
            hasPowerSensor: false
        )
        model.updateMenuBarTitle(runningNoSensor, now: t0)
        #expect(model.menuBarTitle == "2.24 GH/s")

        let runningFaster = HostTelemetryDto(
            state: "Running",
            totalActualHps: 2_500_000_000,
            totalBaselineHps: 2_250_000_000,
            allEnabledFullyCalibrated: true,
            activeTier: "Max",
            totalPowerWatts: 0,
            hasPowerSensor: false
        )
        model.updateMenuBarTitle(runningFaster, now: t0.addingTimeInterval(0.3))
        #expect(model.menuBarTitle == "2.24 GH/s")

        model.updateMenuBarTitle(runningFaster, now: t0.addingTimeInterval(1.1))
        #expect(model.menuBarTitle == "2.50 GH/s")

        model.telemetry = runningFaster
        #expect(model.efficiencyBadgeText == "Max")
        #expect(model.efficiencyBadgeTooltip == nil)
        #expect(!model.efficiencyBadgeText.contains("W"))
    }
}

private final class MockHostTransport: HostTransport, @unchecked Sendable {
    let events: AsyncStream<HostEventMessage>
    private let continuation: AsyncStream<HostEventMessage>.Continuation
    private var unlocked = false
    private var settings = HostSettingsDto()
    private var state = "Idle"

    init() {
        var cont: AsyncStream<HostEventMessage>.Continuation!
        self.events = AsyncStream { cont = $0 }
        self.continuation = cont
    }

    func start() async throws {}
    func shutdown() async { continuation.finish() }

    func sendNoParams<R: Decodable>(method: String, as type: R.Type) async throws -> R {
        try await send(method: method, params: Optional<Int>.none, as: type)
    }

    func send<P: Encodable, R: Decodable>(method: String, params: P?, as type: R.Type) async throws -> R {
        switch method {
        case "getStatus":
            return try recast(makeStatus(), to: type)
        case "unlockAutomatic":
            unlocked = true
            return try recast(UnlockAutomaticResult(unlocked: true, status: makeStatus()), to: type)
        case "applySettings":
            settings = HostSettingsDto(hasCompletedSetup: true, targetInput: "CAFE", language: "en-US", autoPauseOnHit: true, minimizeToTrayOnClose: true, powerTier: "Max")
            return try recast(makeStatus(), to: type)
        case "startSearch":
            state = "Running"
            return try recast(makeStatus(), to: type)
        case "stop":
            state = "Idle"
            return try recast(makeStatus(), to: type)
        default:
            return try recast(makeStatus(), to: type)
        }
    }

    private func makeStatus() -> HostStatusSnapshot {
        let gpu = HostDeviceDto(deviceKey: "vulkan:m3", name: "Apple M3", backendName: "Vulkan")
        return HostStatusSnapshot(
            dataDirectory: "/tmp/pgpmint-test",
            isUnlocked: unlocked,
            supportsAutomaticUnlock: true,
            vaultMode: unlocked ? "automatic" : "none",
            settings: settings,
            devices: unlocked ? [gpu] : [],
            telemetry: unlocked ? HostTelemetryDto(state: state, devices: [gpu]) : nil
        )
    }

    private func recast<T: Encodable, R: Decodable>(_ value: T, to type: R.Type) throws -> R {
        let data = try JSONEncoder().encode(value)
        return try JSONDecoder().decode(type, from: data)
    }
}
