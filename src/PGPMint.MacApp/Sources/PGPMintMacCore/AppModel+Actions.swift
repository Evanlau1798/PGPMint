import Foundation

private struct UnlockPasswordPayload: Encodable { let password: String }
private struct ApplySettingsPayload: Encodable {
    let targetInput: String?
    let language: String?
    let autoPauseOnHit: Bool?
    let minimizeToTrayOnClose: Bool?
    let hasCompletedSetup: Bool?
}
private struct SetPowerTierPayload: Encodable { let tier: String }
private struct SetDeviceEnabledPayload: Encodable { let deviceKey: String; let enabled: Bool }
private struct TargetPayload: Encodable { let targetInput: String? }
private struct RecordIdPayload: Encodable { let recordId: String }
private struct ExportMatchPayload: Encodable {
    let recordId: String
    let uid: String
    let email: String
    let passphrase: String
    let privatePath: String
    let publicPath: String
}

extension AppModel {
    public func bootstrap() async {
        guard !started else { return }
        started = true
        do {
            try await transport.start()
            startEventLoop()
            let snapshot = try await transport.sendNoParams(method: "getStatus", as: HostStatusSnapshot.self)
            applyStatus(snapshot)
            isInitialized = true
            if !snapshot.isUnlocked && snapshot.supportsAutomaticUnlock {
                await attemptAutomaticUnlock()
            }
        } catch {
            isInitialized = true
            handleError(error)
        }
    }

    public func attemptAutomaticUnlock() async {
        guard !isBusy else { return }
        isBusy = true
        attemptingAutoUnlock = true
        errorText = ""
        defer {
            isBusy = false
            attemptingAutoUnlock = false
        }
        do {
            let result = try await transport.sendNoParams(method: "unlockAutomatic", as: UnlockAutomaticResult.self)
            applyStatus(result.status)
        } catch {
            handleError(error)
        }
    }

    public func unlockWithPassword(_ password: String) async -> Bool {
        guard !isBusy else { return false }
        isBusy = true
        errorText = ""
        defer { isBusy = false }
        do {
            let status = try await transport.send(
                method: "unlockPassword",
                params: UnlockPasswordPayload(password: password),
                as: HostStatusSnapshot.self
            )
            applyStatus(status)
            return true
        } catch {
            handleError(error)
            return false
        }
    }

    public func lockVault() async {
        await runAction {
            let status = try await self.transport.sendNoParams(method: "lock", as: HostStatusSnapshot.self)
            self.applyStatus(status)
        }
    }

    public func setLanguage(_ code: String) async {
        loc.applySetting(code)
        selectedLanguageCode = loc.settingValue
        validateDraftTarget()
        onMenuBarStateChanged?()
        guard isUnlocked else { return }
        await runAction {
            let status = try await self.transport.send(
                method: "applySettings",
                params: ApplySettingsPayload(
                    targetInput: nil,
                    language: self.loc.settingValue,
                    autoPauseOnHit: nil,
                    minimizeToTrayOnClose: nil,
                    hasCompletedSetup: nil
                ),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(status)
        }
    }

    public func completeSetupAndStart() async {
        guard canFinishSetup else { return }
        let target = draftTargetInput
        await runAction {
            let saved = try await self.transport.send(
                method: "applySettings",
                params: ApplySettingsPayload(
                    targetInput: target,
                    language: self.loc.settingValue,
                    autoPauseOnHit: self.autoPauseOnHit,
                    minimizeToTrayOnClose: self.minimizeToTrayOnClose,
                    hasCompletedSetup: true
                ),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(saved)
            let started = try await self.transport.send(
                method: "startSearch",
                params: TargetPayload(targetInput: saved.settings.targetInput),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(started)
        }
    }

    public func applyDraftTarget() async {
        guard canApplyDraftTarget else { return }
        let target = draftTargetInput
        await runAction {
            let status = try await self.transport.send(
                method: "applySettings",
                params: ApplySettingsPayload(
                    targetInput: target,
                    language: nil,
                    autoPauseOnHit: nil,
                    minimizeToTrayOnClose: nil,
                    hasCompletedSetup: true
                ),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(status)
            self.flashNotice(self.loc.text("Applied"))
        }
    }

    public func setAutoPauseOnHit(_ enabled: Bool) async {
        autoPauseOnHit = enabled
        guard isUnlocked else { return }
        await runAction {
            let status = try await self.transport.send(
                method: "applySettings",
                params: ApplySettingsPayload(targetInput: nil, language: nil, autoPauseOnHit: enabled, minimizeToTrayOnClose: nil, hasCompletedSetup: nil),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(status)
        }
    }

    public func setMinimizeToTray(_ enabled: Bool) async {
        minimizeToTrayOnClose = enabled
        guard isUnlocked else { return }
        await runAction {
            let status = try await self.transport.send(
                method: "applySettings",
                params: ApplySettingsPayload(targetInput: nil, language: nil, autoPauseOnHit: nil, minimizeToTrayOnClose: enabled, hasCompletedSetup: nil),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(status)
        }
    }

    public func setPowerTier(_ tier: String) async {
        guard isTierSelectorEnabled else { return }
        await runAction {
            let status = try await self.transport.send(
                method: "setPowerTier",
                params: SetPowerTierPayload(tier: tier),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(status)
        }
    }

    public func setDeviceEnabled(_ deviceKey: String, enabled: Bool) async {
        await runAction {
            let status = try await self.transport.send(
                method: "setDeviceEnabled",
                params: SetDeviceEnabledPayload(deviceKey: deviceKey, enabled: enabled),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(status)
        }
    }

    public func primaryAction() async {
        if isSearching || isPaused {
            await stopSearch()
        } else {
            await startSearch()
        }
    }

    public func startSearch() async {
        await runAction {
            let status = try await self.transport.send(
                method: "startSearch",
                params: TargetPayload(targetInput: self.activeTargetSuffix),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(status)
        }
    }

    public func pauseSearch() async {
        await runAction {
            let status = try await self.transport.sendNoParams(method: "pause", as: HostStatusSnapshot.self)
            self.applyStatus(status)
        }
    }

    public func resumeSearch() async {
        await runAction {
            let status = try await self.transport.sendNoParams(method: "resume", as: HostStatusSnapshot.self)
            self.applyStatus(status)
        }
    }

    public func stopSearch() async {
        await runAction {
            let status = try await self.transport.sendNoParams(method: "stop", as: HostStatusSnapshot.self)
            self.applyStatus(status)
        }
    }

    public func recalibrate() async {
        guard canRecalibrate else { return }
        await runAction {
            let status = try await self.transport.send(
                method: "recalibrate",
                params: TargetPayload(targetInput: self.activeTargetSuffix),
                as: HostStatusSnapshot.self
            )
            self.applyStatus(status)
            self.flashNotice(self.loc.text("Recalibrating"))
        }
    }

    public func exportMatch(
        recordId: String,
        uid: String,
        email: String,
        passphrase: String,
        privatePath: String,
        publicPath: String
    ) async -> Bool {
        guard !isBusy else { return false }
        isBusy = true
        errorText = ""
        defer { isBusy = false }
        do {
            _ = try await transport.send(
                method: "exportMatch",
                params: ExportMatchPayload(
                    recordId: recordId,
                    uid: uid,
                    email: email,
                    passphrase: passphrase,
                    privatePath: privatePath,
                    publicPath: publicPath
                ),
                as: ExportMatchResult.self
            )
            flashNotice(loc.text("ExportSuccess"))
            return true
        } catch {
            handleError(error)
            return false
        }
    }

    public func deleteMatch(_ recordId: String) async {
        await runAction {
            let result = try await self.transport.send(
                method: "deleteMatch",
                params: RecordIdPayload(recordId: recordId),
                as: DeleteMatchResult.self
            )
            self.applyStatus(result.status)
        }
    }

    public func clearAllMatches() async {
        await runAction {
            let result = try await self.transport.sendNoParams(method: "clearMatches", as: ClearMatchesResult.self)
            self.applyStatus(result.status)
        }
    }

    public func flashNotice(_ message: String) {
        transientNotice = message
        noticeTask?.cancel()
        noticeTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 2_600_000_000)
            guard !Task.isCancelled else { return }
            if self?.transientNotice == message {
                self?.transientNotice = ""
            }
        }
    }

    public func prepareForQuit() async {
        eventTask?.cancel()
        powerManager.setActive(false)
        sleepProtected = powerManager.sleepProtected
        await transport.shutdown()
    }

    private func runAction(_ body: @escaping () async throws -> Void) async {
        guard !isBusy else { return }
        isBusy = true
        errorText = ""
        defer { isBusy = false }
        do {
            try await body()
        } catch {
            handleError(error)
        }
    }

    private func startEventLoop() {
        eventTask?.cancel()
        eventTask = Task { [weak self] in
            guard let self else { return }
            for await event in self.transport.events {
                if Task.isCancelled { break }
                switch event {
                case .telemetry(let snap):
                    self.applyTelemetry(snap)
                case .matchFound(let match):
                    if !self.matches.contains(where: { $0.id == match.id }) {
                        self.matches.insert(match, at: 0)
                    }
                    self.onMatchNotification?(match.formattedKeyId)
                    self.onMenuBarStateChanged?()
                case .terminated(let code, _):
                    self.powerManager.setActive(false)
                    self.sleepProtected = self.powerManager.sleepProtected
                    self.errorText = "\(self.loc.text("OperationFailed")) (host exit \(code))"
                    self.onMenuBarStateChanged?()
                }
            }
        }
    }

    internal func applyStatus(_ status: HostStatusSnapshot) {
        dataDirectory = status.dataDirectory
        isUnlocked = status.isUnlocked
        supportsAutomaticUnlock = status.supportsAutomaticUnlock
        hasAutomaticMetadata = status.hasAutomaticMetadata
        hasLegacyVault = status.hasLegacyVault
        vaultMode = status.vaultMode
        devices = status.devices
        gpuDiagnostics = status.gpuDiagnostics
        matches = status.matches
        needsFullCalibration = status.needsFullCalibration

        loc.applySetting(status.settings.language)
        selectedLanguageCode = loc.settingValue
        autoPauseOnHit = status.settings.autoPauseOnHit
        minimizeToTrayOnClose = status.settings.minimizeToTrayOnClose

        if case .valid(let suffix, _, let formatted, let len) = Formatting.validateKeyIdSuffix(status.settings.targetInput) {
            activeTargetSuffix = suffix
            activeFormattedPreview = formatted
            activeFixedHexLength = len
            if draftTargetInput.isEmpty || !showSetupWizard {
                draftTargetInput = suffix
            }
            showSetupWizard = status.isUnlocked && !status.settings.hasCompletedSetup
        } else {
            activeTargetSuffix = ""
            activeFormattedPreview = "**** **** **** ****"
            activeFixedHexLength = 0
            showSetupWizard = status.isUnlocked
        }
        validateDraftTarget()

        if let t = status.telemetry {
            applyTelemetry(t)
        } else {
            applyTelemetry(HostTelemetryDto())
        }

        if status.isUnlocked && devices.isEmpty {
            errorText = loc.text("NoGpuError")
        }
    }

    internal func applyTelemetry(_ t: HostTelemetryDto) {
        telemetry = t
        if !t.devices.isEmpty {
            devices = t.devices
        }

        let activeCompute = t.state == "Running" || t.state == "Calibrating"
        powerManager.setActive(activeCompute)
        sleepProtected = powerManager.sleepProtected

        if t.vaultIsFull {
            errorText = loc.text("VaultFullError")
        } else if let msg = t.errorMessage, !msg.isEmpty {
            switch t.failure {
            case "SaveFailed": errorText = loc.text("VaultSaveFailedError")
            case "IncompleteResults": errorText = loc.text("IncompleteSearchResultsError")
            default: errorText = msg
            }
        } else if activeCompute && hasHardwareGpu && t.state != "Faulted" {
            errorText = ""
        }

        updateMenuBarTitle(t, now: Date())
    }

    internal func handleError(_ error: Error) {
        guard let hostErr = error as? HostError else {
            errorText = error.localizedDescription
            return
        }
        switch hostErr.code {
        case "system_vault_failed":
            errorText = loc.text("SystemVaultFailed")
        case "invalid_password":
            errorText = loc.text("UnlockFailed")
        case "backend_error":
            errorText = loc.text("BackendError")
        case "invalid_argument":
            errorText = hostErr.message
        default:
            errorText = hostErr.message.isEmpty ? loc.text("OperationFailed") : hostErr.message
        }
    }
}
