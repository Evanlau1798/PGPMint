import XCTest
@testable import PGPMintMacCore

final class PGPMintMacCoreTests: XCTestCase {
    func testFormattingParityMatchesCoreHelpers() {
        XCTAssertEqual(Formatting.formatHashRateValue(0), "0.00")
        XCTAssertEqual(Formatting.formatHashRateUnit(0), "GH/s")
        XCTAssertEqual(Formatting.formatHashRateValue(2_242_700_000), "2.24")
        XCTAssertEqual(Formatting.formatHashRateUnit(2_242_700_000), "GH/s")
        XCTAssertEqual(Formatting.formatHashRateCompact(5_400_000), "5.4 MH/s")
        XCTAssertEqual(Formatting.formatCheckedCount(9_999), "9,999 H")
        XCTAssertEqual(Formatting.formatCheckedCount(10_000), "1.00 \u{00D7} 10^4 H")
        XCTAssertEqual(Formatting.formatProbability(0.12345), "12.35%")
        XCTAssertEqual(Formatting.formatEta(seconds: nil, isChinese: false), "\u{2014}")
        XCTAssertEqual(Formatting.formatEta(seconds: 42.3, isChinese: false), "~42.3s")
        XCTAssertEqual(Formatting.formatEta(seconds: 42.3, isChinese: true), "~42.3 \u{79D2}")
        XCTAssertEqual(Formatting.formatKeyIdSpaced("0123456789ABCDEF"), "0123 4567 89AB CDEF")

        XCTAssertEqual(
            Formatting.validateKeyIdSuffix("dead beef"),
            .valid(suffix: "DEADBEEF", canonicalMask: "********DEADBEEF", formattedDisplay: "**** **** DEAD BEEF", fixedHexLength: 8)
        )
        XCTAssertEqual(Formatting.validateKeyIdSuffix("XYZ"), .invalidCharacters)
        XCTAssertEqual(Formatting.validateKeyIdSuffix("0123456789ABCDEF0"), .tooLong)
    }

    @MainActor
    func testLocalizationLoadsEnglishAndTraditionalChinese() {
        let loc = LocalizationStore()
        loc.applySetting("en-US")
        XCTAssertEqual(loc.text("Start"), "Start")
        XCTAssertEqual(loc.text("SystemVaultHint").contains("login keychain"), true)

        loc.applySetting("zh-Hant")
        XCTAssertEqual(loc.text("Start"), "\u{958B}\u{59CB}")
        XCTAssertTrue(loc.isChinese)
    }

    @MainActor
    func testAppModelHonestPowerBadgeAndMenuBarOneHzThrottle() async {
        let mock = MockHostTransport()
        let power = NullPowerActivityManager()
        let loc = LocalizationStore()
        loc.applySetting("en-US")
        let model = AppModel(transport: mock, loc: loc, powerManager: power)

        await model.bootstrap()
        XCTAssertTrue(model.isUnlocked)
        XCTAssertTrue(model.showSetupWizard)

        model.draftTargetInput = "CAFE"
        XCTAssertTrue(model.canFinishSetup)
        await model.completeSetupAndStart()
        XCTAssertFalse(model.showSetupWizard)

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
        XCTAssertEqual(model.menuBarTitle, "2.24 GH/s")

        // Within 1 second window, rate change should be throttled
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
        XCTAssertEqual(model.menuBarTitle, "2.24 GH/s")

        // After 1 second, menu bar updates
        model.updateMenuBarTitle(runningFaster, now: t0.addingTimeInterval(1.1))
        XCTAssertEqual(model.menuBarTitle, "2.50 GH/s")

        // Without power sensor, badge shows tier label, never fake 0 W
        model.telemetry = runningFaster
        XCTAssertEqual(model.efficiencyBadgeText, "Max")
        XCTAssertNil(model.efficiencyBadgeTooltip)
        XCTAssertFalse(model.efficiencyBadgeText.contains("W"))
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
