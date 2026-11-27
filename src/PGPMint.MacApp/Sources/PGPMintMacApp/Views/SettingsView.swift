import SwiftUI
import AppKit
import UniformTypeIdentifiers
import PGPMintMacCore

private enum SyncSheetMode: Identifiable {
    case exportOptions(outputPath: String)
    case importPassphrase(bundlePath: String)

    var id: String {
        switch self {
        case .exportOptions(let p): return "export:\(p)"
        case .importPassphrase(let p): return "import:\(p)"
        }
    }
}

struct SettingsView: View {
    @Bindable var model: AppModel
    @State private var syncSheetMode: SyncSheetMode?
    @State private var syncIncludeKeys = true
    @State private var syncPassphrase = ""

    var body: some View {
        ScrollView {
            VStack(spacing: 12) {
                SurfaceCard {
                    HStack {
                        Text(model.loc.text("SectionLanguage"))
                            .font(.system(size: 13, weight: .semibold))
                        Spacer()
                        Picker(model.loc.text("SectionLanguage"), selection: Binding(
                            get: { model.selectedLanguageCode },
                            set: { code in Task { await model.setLanguage(code) } }
                        )) {
                            ForEach(model.loc.languageOptions) { opt in
                                Text(opt.displayName).tag(opt.code)
                            }
                        }
                        .labelsHidden()
                        .frame(width: 184)
                    }
                }

                if model.isUnlocked {
                    SurfaceCard {
                        VStack(spacing: 10) {
                            Text(model.loc.text("SectionTarget"))
                                .font(.system(size: 13, weight: .semibold))
                                .frame(maxWidth: .infinity, alignment: .leading)

                            Text(model.draftFormattedPreview)
                                .font(.system(size: 20, weight: .semibold, design: .monospaced))
                                .tracking(1.1)

                            HStack(spacing: 8) {
                                TextField(model.loc.text("TargetPlaceholder"), text: $model.draftTargetInput)
                                    .textFieldStyle(.roundedBorder)
                                    .font(.system(size: 14, design: .monospaced))
                                    .multilineTextAlignment(.center)
                                    .disabled(!model.canEditTarget)
                                    .overlay(
                                        RoundedRectangle(cornerRadius: 6)
                                            .strokeBorder(model.hasDraftError ? Color.red : Color.clear, lineWidth: 1.5)
                                    )

                                Button(model.loc.text("Apply")) {
                                    Task { await model.applyDraftTarget() }
                                }
                                .buttonStyle(.borderedProminent)
                                .disabled(!model.canApplyDraftTarget)
                            }

                            if model.hasDraftError && !model.draftErrorText.isEmpty {
                                Text(model.draftErrorText)
                                    .font(.system(size: 12))
                                    .foregroundStyle(.red)
                            }

                            if !model.canEditTarget {
                                Text(model.loc.text("StopToEditHint"))
                                    .font(.system(size: 12))
                                    .foregroundStyle(.secondary)
                            }

                            Text(model.loc.text("SuffixRuleHint"))
                                .font(.system(size: 12))
                                .foregroundStyle(.secondary)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }

                    SurfaceCard {
                        VStack(alignment: .leading, spacing: 10) {
                            Text(model.loc.text("SectionPreferences"))
                                .font(.system(size: 13, weight: .semibold))

                            Toggle(model.loc.text("SettingAutoPauseOnHit"), isOn: Binding(
                                get: { model.autoPauseOnHit },
                                set: { enabled in Task { await model.setAutoPauseOnHit(enabled) } }
                            ))

                            Toggle(model.loc.text("SettingMinimizeToTray"), isOn: Binding(
                                get: { model.minimizeToTrayOnClose },
                                set: { enabled in Task { await model.setMinimizeToTray(enabled) } }
                            ))
                        }
                    }

                    SurfaceCard {
                        VStack(alignment: .leading, spacing: 10) {
                            HStack {
                                Text(model.loc.text("SectionGpus"))
                                    .font(.system(size: 13, weight: .semibold))
                                Spacer()
                                Button(model.loc.text("Recalibrate")) {
                                    Task { await model.recalibrate() }
                                }
                                .buttonStyle(.bordered)
                                .controlSize(.small)
                                .disabled(!model.canRecalibrate)
                            }

                            if model.devices.isEmpty {
                                Text(model.loc.text("NoGpuError"))
                                    .font(.system(size: 12.5))
                                    .foregroundStyle(.red)
                            } else {
                                ForEach(model.devices) { gpu in
                                    HStack(spacing: 8) {
                                        Toggle(isOn: Binding(
                                            get: { gpu.isEnabled },
                                            set: { enabled in Task { await model.setDeviceEnabled(gpu.deviceKey, enabled: enabled) } }
                                        )) {
                                            VStack(alignment: .leading, spacing: 2) {
                                                Text(gpu.name)
                                                    .font(.system(size: 13))
                                                    .lineLimit(1)
                                                if gpu.actualHps > 0 || gpu.baselineHps > 0 {
                                                    Text("\(Formatting.formatHashRateCompact(gpu.actualHps)) / \(Formatting.formatHashRateCompact(gpu.baselineHps))")
                                                        .font(.system(size: 11, design: .monospaced))
                                                        .foregroundStyle(.secondary)
                                                }
                                            }
                                        }
                                        .toggleStyle(.checkbox)
                                        .disabled(model.devices.count <= 1 || model.isBusy || model.telemetry.state == "Calibrating" || model.telemetry.state == "Stopping")

                                        Spacer()
                                        BackendBadgeView(name: gpu.backendName)
                                    }
                                }
                            }

                            if !model.gpuDiagnostics.isEmpty {
                                Text(model.gpuDiagnostics.joined(separator: "\n"))
                                    .font(.system(size: 11, design: .monospaced))
                                    .foregroundStyle(.secondary)
                                    .textSelection(.enabled)
                            }
                        }
                    }

                    SurfaceCard {
                        VStack(alignment: .leading, spacing: 10) {
                            Text(model.loc.text("SectionSync"))
                                .font(.system(size: 13, weight: .semibold))

                            HStack(spacing: 10) {
                                Button(model.loc.text("SyncExportButton")) {
                                    startExportSync()
                                }
                                .buttonStyle(.bordered)
                                .disabled(model.isBusy)

                                Button(model.loc.text("SyncImportButton")) {
                                    startImportSync()
                                }
                                .buttonStyle(.bordered)
                                .disabled(model.isBusy)
                            }
                        }
                    }

                    SurfaceCard {
                        VStack(alignment: .leading, spacing: 8) {
                            Text(model.loc.text("DataDirectory"))
                                .font(.system(size: 13, weight: .semibold))
                            Text(model.dataDirectory)
                                .font(.system(size: 11.5, design: .monospaced))
                                .foregroundStyle(.secondary)
                                .textSelection(.enabled)
                            Button(model.loc.text("Lock")) {
                                Task { await model.lockVault() }
                            }
                            .buttonStyle(.bordered)
                            .disabled(model.isBusy)
                        }
                    }
                }
            }
            .padding(16)
        }
        .sheet(item: $syncSheetMode) { mode in
            SyncPassphraseSheetView(
                model: model,
                mode: mode,
                includeKeys: $syncIncludeKeys,
                passphrase: $syncPassphrase
            )
        }
    }

    private func startExportSync() {
        let panel = NSSavePanel()
        panel.title = model.loc.text("SyncExportButton")
        panel.nameFieldStringValue = "pgpmint-sync.pgpmint-sync"
        panel.allowedContentTypes = [UTType(filenameExtension: "pgpmint-sync") ?? .data]
        panel.allowsOtherFileTypes = true
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url, url.isFileURL else {
            return
        }

        if model.matches.isEmpty {
            Task {
                _ = await model.exportSyncBundle(outputPath: url.path, includeKeys: false, passphrase: nil)
            }
        } else {
            syncIncludeKeys = true
            syncPassphrase = ""
            syncSheetMode = .exportOptions(outputPath: url.path)
        }
    }

    private func startImportSync() {
        let panel = NSOpenPanel()
        panel.title = model.loc.text("SyncImportButton")
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.allowedContentTypes = [UTType(filenameExtension: "pgpmint-sync") ?? .data, .data]
        guard panel.runModal() == .OK, let url = panel.url, url.isFileURL else {
            return
        }

        Task {
            guard let info = await model.inspectSyncBundle(bundlePath: url.path) else {
                return
            }
            if info.hasEncryptedKeys && info.keyCount > 0 {
                syncPassphrase = ""
                syncSheetMode = .importPassphrase(bundlePath: url.path)
            } else {
                _ = await model.importSyncBundle(bundlePath: url.path, passphrase: nil)
            }
        }
    }
}

private struct SyncPassphraseSheetView: View {
    @Bindable var model: AppModel
    let mode: SyncSheetMode
    @Binding var includeKeys: Bool
    @Binding var passphrase: String
    @Environment(\.dismiss) private var dismiss

    private var requiresPassphrase: Bool {
        switch mode {
        case .exportOptions: return includeKeys
        case .importPassphrase: return true
        }
    }

    private var canSubmit: Bool {
        !model.isBusy && (!requiresPassphrase || !passphrase.isEmpty)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            switch mode {
            case .exportOptions:
                Text(model.loc.text("SyncExportButton"))
                    .font(.system(size: 15, weight: .semibold))
                Toggle(model.loc.text("SyncIncludeKeysCheck"), isOn: $includeKeys)
                    .toggleStyle(.checkbox)
                SecureField(model.loc.text("SyncBundlePassphrasePrompt"), text: $passphrase)
                    .textFieldStyle(.roundedBorder)
                    .disabled(!includeKeys)
            case .importPassphrase:
                Text(model.loc.text("SyncImportButton"))
                    .font(.system(size: 15, weight: .semibold))
                SecureField(model.loc.text("SyncBundlePassphrasePrompt"), text: $passphrase)
                    .textFieldStyle(.roundedBorder)
            }

            HStack {
                Spacer()
                Button(model.loc.text("Cancel")) {
                    passphrase = ""
                    dismiss()
                }
                .keyboardShortcut(.cancelAction)

                Button(model.loc.text("Apply")) {
                    let captured = passphrase
                    passphrase = ""
                    dismiss()
                    Task {
                        switch mode {
                        case .exportOptions(let path):
                            _ = await model.exportSyncBundle(
                                outputPath: path,
                                includeKeys: includeKeys,
                                passphrase: includeKeys ? captured : nil
                            )
                        case .importPassphrase(let path):
                            _ = await model.importSyncBundle(bundlePath: path, passphrase: captured)
                        }
                    }
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(!canSubmit)
            }
        }
        .padding(20)
        .frame(width: 360)
    }
}
