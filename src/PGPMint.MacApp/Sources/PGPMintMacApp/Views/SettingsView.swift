import SwiftUI
import PGPMintMacCore

struct SettingsView: View {
    @Bindable var model: AppModel

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
    }
}
