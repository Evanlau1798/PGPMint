import Foundation

public struct HostSettingsDto: Codable, Equatable, Sendable {
    public let hasCompletedSetup: Bool
    public let targetInput: String
    public let language: String
    public let autoPauseOnHit: Bool
    public let minimizeToTrayOnClose: Bool
    public let powerTier: String
    public let minCreationTimestamp: UInt32?
    public let maxCreationTimestamp: UInt32?

    public init(
        hasCompletedSetup: Bool = false,
        targetInput: String = "",
        language: String = "auto",
        autoPauseOnHit: Bool = true,
        minimizeToTrayOnClose: Bool = true,
        powerTier: String = "Max",
        minCreationTimestamp: UInt32? = nil,
        maxCreationTimestamp: UInt32? = nil
    ) {
        self.hasCompletedSetup = hasCompletedSetup
        self.targetInput = targetInput
        self.language = language
        self.autoPauseOnHit = autoPauseOnHit
        self.minimizeToTrayOnClose = minimizeToTrayOnClose
        self.powerTier = powerTier
        self.minCreationTimestamp = minCreationTimestamp
        self.maxCreationTimestamp = maxCreationTimestamp
    }
}

public struct HostDeviceDto: Codable, Equatable, Identifiable, Sendable {
    public var id: String { deviceKey }
    public let deviceKey: String
    public let name: String
    public let backendName: String
    public let driverVersion: String
    public let isDiscrete: Bool
    public let isEnabled: Bool
    public let actualHps: Double
    public let targetHps: Int64
    public let baselineHps: Double
    public let isFullCalibrated: Bool
    public let error: String?

    public init(
        deviceKey: String,
        name: String,
        backendName: String,
        driverVersion: String = "",
        isDiscrete: Bool = true,
        isEnabled: Bool = true,
        actualHps: Double = 0,
        targetHps: Int64 = 0,
        baselineHps: Double = 0,
        isFullCalibrated: Bool = false,
        error: String? = nil
    ) {
        self.deviceKey = deviceKey
        self.name = name
        self.backendName = backendName
        self.driverVersion = driverVersion
        self.isDiscrete = isDiscrete
        self.isEnabled = isEnabled
        self.actualHps = actualHps
        self.targetHps = targetHps
        self.baselineHps = baselineHps
        self.isFullCalibrated = isFullCalibrated
        self.error = error
    }
}

public struct HostTelemetryDto: Codable, Equatable, Sendable {
    public let state: String
    public let calibrationRemainingSeconds: Double
    public let fullCalibrationDurationSeconds: Double
    public let totalActualHps: Double
    public let totalTargetHps: Int64
    public let isFullSpeed: Bool
    public let totalBaselineHps: Double
    public let allEnabledFullyCalibrated: Bool
    public let sessionChecked: UInt64
    public let totalChecked: UInt64
    public let totalMatchesCount: Int
    public let cumulativeProbability: Double
    public let estimatedMeanWaitSeconds: Double?
    public let errorMessage: String?
    public let vaultIsFull: Bool
    public let failure: String
    public let activeTier: String
    public let totalPowerWatts: Double
    public let hasPowerSensor: Bool
    public let aggregateGpuUtilPercent: Double
    public let hasGpuUtilSensor: Bool
    public let isOverloadYielding: Bool
    public let isGpuContended: Bool
    public let verifiedHits: Int64
    public let savedKeys: Int64
    public let devices: [HostDeviceDto]

    public init(
        state: String = "Idle",
        calibrationRemainingSeconds: Double = 0,
        fullCalibrationDurationSeconds: Double = 30,
        totalActualHps: Double = 0,
        totalTargetHps: Int64 = 0,
        isFullSpeed: Bool = false,
        totalBaselineHps: Double = 0,
        allEnabledFullyCalibrated: Bool = false,
        sessionChecked: UInt64 = 0,
        totalChecked: UInt64 = 0,
        totalMatchesCount: Int = 0,
        cumulativeProbability: Double = 0,
        estimatedMeanWaitSeconds: Double? = nil,
        errorMessage: String? = nil,
        vaultIsFull: Bool = false,
        failure: String = "None",
        activeTier: String = "Max",
        totalPowerWatts: Double = 0,
        hasPowerSensor: Bool = false,
        aggregateGpuUtilPercent: Double = 0,
        hasGpuUtilSensor: Bool = false,
        isOverloadYielding: Bool = false,
        isGpuContended: Bool = false,
        verifiedHits: Int64 = 0,
        savedKeys: Int64 = 0,
        devices: [HostDeviceDto] = []
    ) {
        self.state = state
        self.calibrationRemainingSeconds = calibrationRemainingSeconds
        self.fullCalibrationDurationSeconds = fullCalibrationDurationSeconds
        self.totalActualHps = totalActualHps
        self.totalTargetHps = totalTargetHps
        self.isFullSpeed = isFullSpeed
        self.totalBaselineHps = totalBaselineHps
        self.allEnabledFullyCalibrated = allEnabledFullyCalibrated
        self.sessionChecked = sessionChecked
        self.totalChecked = totalChecked
        self.totalMatchesCount = totalMatchesCount
        self.cumulativeProbability = cumulativeProbability
        self.estimatedMeanWaitSeconds = estimatedMeanWaitSeconds
        self.errorMessage = errorMessage
        self.vaultIsFull = vaultIsFull
        self.failure = failure
        self.activeTier = activeTier
        self.totalPowerWatts = totalPowerWatts
        self.hasPowerSensor = hasPowerSensor
        self.aggregateGpuUtilPercent = aggregateGpuUtilPercent
        self.hasGpuUtilSensor = hasGpuUtilSensor
        self.isOverloadYielding = isOverloadYielding
        self.isGpuContended = isGpuContended
        self.verifiedHits = verifiedHits
        self.savedKeys = savedKeys
        self.devices = devices
    }
}

public struct HostMatchDto: Codable, Equatable, Identifiable, Sendable {
    public let id: String
    public let fingerprint: String
    public let keyId: String
    public let formattedKeyId: String
    public let creationTimestamp: UInt32
    public let matchedPattern: String
    public let deviceName: String
    public let foundAtUtc: String
    public let publicKeyHex: String

    public init(
        id: String,
        fingerprint: String,
        keyId: String,
        formattedKeyId: String,
        creationTimestamp: UInt32,
        matchedPattern: String,
        deviceName: String,
        foundAtUtc: String,
        publicKeyHex: String
    ) {
        self.id = id
        self.fingerprint = fingerprint
        self.keyId = keyId
        self.formattedKeyId = formattedKeyId
        self.creationTimestamp = creationTimestamp
        self.matchedPattern = matchedPattern
        self.deviceName = deviceName
        self.foundAtUtc = foundAtUtc
        self.publicKeyHex = publicKeyHex
    }
}

public struct HostStatusSnapshot: Codable, Equatable, Sendable {
    public let protocolVersion: Int
    public let appVersion: String
    public let dataDirectory: String
    public let isUnlocked: Bool
    public let supportsAutomaticUnlock: Bool
    public let hasAutomaticMetadata: Bool
    public let hasLegacyVault: Bool
    public let vaultMode: String
    public let settings: HostSettingsDto
    public let devices: [HostDeviceDto]
    public let gpuDiagnostics: [String]
    public let telemetry: HostTelemetryDto?
    public let matches: [HostMatchDto]
    public let needsFullCalibration: Bool
    public let fullCalibrationDurationSeconds: Double

    public init(
        protocolVersion: Int = 1,
        appVersion: String = "1.3.0",
        dataDirectory: String = "",
        isUnlocked: Bool = false,
        supportsAutomaticUnlock: Bool = true,
        hasAutomaticMetadata: Bool = false,
        hasLegacyVault: Bool = false,
        vaultMode: String = "none",
        settings: HostSettingsDto = HostSettingsDto(),
        devices: [HostDeviceDto] = [],
        gpuDiagnostics: [String] = [],
        telemetry: HostTelemetryDto? = nil,
        matches: [HostMatchDto] = [],
        needsFullCalibration: Bool = false,
        fullCalibrationDurationSeconds: Double = 30
    ) {
        self.protocolVersion = protocolVersion
        self.appVersion = appVersion
        self.dataDirectory = dataDirectory
        self.isUnlocked = isUnlocked
        self.supportsAutomaticUnlock = supportsAutomaticUnlock
        self.hasAutomaticMetadata = hasAutomaticMetadata
        self.hasLegacyVault = hasLegacyVault
        self.vaultMode = vaultMode
        self.settings = settings
        self.devices = devices
        self.gpuDiagnostics = gpuDiagnostics
        self.telemetry = telemetry
        self.matches = matches
        self.needsFullCalibration = needsFullCalibration
        self.fullCalibrationDurationSeconds = fullCalibrationDurationSeconds
    }
}

public struct UnlockAutomaticResult: Codable, Equatable, Sendable {
    public let unlocked: Bool
    public let status: HostStatusSnapshot

    public init(unlocked: Bool, status: HostStatusSnapshot) {
        self.unlocked = unlocked
        self.status = status
    }
}

public struct DeleteMatchResult: Codable, Equatable, Sendable {
    public let deleted: Bool
    public let status: HostStatusSnapshot

    public init(deleted: Bool, status: HostStatusSnapshot) {
        self.deleted = deleted
        self.status = status
    }
}

public struct ClearMatchesResult: Codable, Equatable, Sendable {
    public let removed: Int
    public let status: HostStatusSnapshot

    public init(removed: Int, status: HostStatusSnapshot) {
        self.removed = removed
        self.status = status
    }
}

public struct ExportMatchResult: Codable, Equatable, Sendable {
    public let privatePath: String
    public let publicPath: String

    public init(privatePath: String, publicPath: String) {
        self.privatePath = privatePath
        self.publicPath = publicPath
    }
}

public struct SyncInspectResultDto: Codable, Equatable, Sendable {
    public let version: Int
    public let createdAtUtc: String
    public let patternMask: String
    public let targetInput: String
    public let totalCheckedCandidates: UInt64
    public let hasEncryptedKeys: Bool
    public let keyCount: Int

    public init(
        version: Int,
        createdAtUtc: String,
        patternMask: String,
        targetInput: String,
        totalCheckedCandidates: UInt64,
        hasEncryptedKeys: Bool,
        keyCount: Int
    ) {
        self.version = version
        self.createdAtUtc = createdAtUtc
        self.patternMask = patternMask
        self.targetInput = targetInput
        self.totalCheckedCandidates = totalCheckedCandidates
        self.hasEncryptedKeys = hasEncryptedKeys
        self.keyCount = keyCount
    }
}

public struct SyncExportResultDto: Codable, Equatable, Sendable {
    public let filePath: String
    public let patternMask: String
    public let totalCheckedCandidates: UInt64
    public let exportedKeysCount: Int
    public let hasEncryptedKeys: Bool

    public init(
        filePath: String,
        patternMask: String,
        totalCheckedCandidates: UInt64,
        exportedKeysCount: Int,
        hasEncryptedKeys: Bool
    ) {
        self.filePath = filePath
        self.patternMask = patternMask
        self.totalCheckedCandidates = totalCheckedCandidates
        self.exportedKeysCount = exportedKeysCount
        self.hasEncryptedKeys = hasEncryptedKeys
    }
}

public struct SyncImportResultDto: Codable, Equatable, Sendable {
    public let patternMask: String
    public let addedCandidates: UInt64
    public let newTotalCheckedCandidates: UInt64
    public let importedKeysCount: Int
    public let skippedDuplicateKeysCount: Int
    public let patternMismatchSkippedProgress: Bool
    public let status: HostStatusSnapshot

    public init(
        patternMask: String,
        addedCandidates: UInt64,
        newTotalCheckedCandidates: UInt64,
        importedKeysCount: Int,
        skippedDuplicateKeysCount: Int,
        patternMismatchSkippedProgress: Bool,
        status: HostStatusSnapshot
    ) {
        self.patternMask = patternMask
        self.addedCandidates = addedCandidates
        self.newTotalCheckedCandidates = newTotalCheckedCandidates
        self.importedKeysCount = importedKeysCount
        self.skippedDuplicateKeysCount = skippedDuplicateKeysCount
        self.patternMismatchSkippedProgress = patternMismatchSkippedProgress
        self.status = status
    }
}

public struct HostError: Error, Equatable, Sendable {
    public let code: String
    public let message: String

    public init(code: String, message: String) {
        self.code = code
        self.message = message
    }
}

public enum HostEventMessage: Sendable {
    case telemetry(HostTelemetryDto)
    case matchFound(HostMatchDto)
    case terminated(Int32, String)
}
