import SwiftUI
import PGPMintMacCore

struct SetupWizardView: View {
    @Bindable var model: AppModel

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                HStack(spacing: 8) {
                    StatusDotView(colorName: model.statusIndicatorColorName)
                    Text("PGPMint")
                        .font(.system(size: 14, weight: .semibold))
                    Spacer()
                }

                ErrorBannerView(message: model.errorText)

                SurfaceCard {
                    VStack(spacing: 14) {
                        Text(model.loc.text("SetupTitle"))
                            .font(.system(size: 16, weight: .semibold))
                            .frame(maxWidth: .infinity, alignment: .leading)

                        Text(model.draftFormattedPreview)
                            .font(.system(size: 22, weight: .semibold, design: .monospaced))
                            .tracking(1.2)
                            .frame(maxWidth: .infinity)
                            .padding(.top, 4)

                        VStack(spacing: 6) {
                            TextField(model.loc.text("TargetPlaceholder"), text: $model.draftTargetInput)
                                .textFieldStyle(.roundedBorder)
                                .font(.system(size: 15, design: .monospaced))
                                .multilineTextAlignment(.center)
                                .overlay(
                                    RoundedRectangle(cornerRadius: 6)
                                        .strokeBorder(model.hasDraftError ? Color.red : Color.clear, lineWidth: 1.5)
                                )
                                .onSubmit {
                                    if model.canFinishSetup {
                                        Task { await model.completeSetupAndStart() }
                                    }
                                }

                            if model.hasDraftError && !model.draftErrorText.isEmpty {
                                Text(model.draftErrorText)
                                    .font(.system(size: 12))
                                    .foregroundStyle(.red)
                                    .multilineTextAlignment(.center)
                            }

                            Text(model.loc.text("SuffixRuleHint"))
                                .font(.system(size: 12))
                                .foregroundStyle(.secondary)
                                .multilineTextAlignment(.center)
                        }

                        Divider()

                        VStack(alignment: .leading, spacing: 8) {
                            Text(model.loc.text("SectionGpus"))
                                .font(.system(size: 13, weight: .semibold))

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
                                            Text(gpu.name)
                                                .font(.system(size: 13))
                                                .lineLimit(1)
                                        }
                                        .toggleStyle(.checkbox)
                                        .disabled(model.devices.count <= 1 || model.isBusy)

                                        Spacer()
                                        BackendBadgeView(name: gpu.backendName)
                                    }
                                }
                            }
                        }

                        Divider()

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
                            .frame(width: 156)
                        }

                        Button {
                            Task { await model.completeSetupAndStart() }
                        } label: {
                            Text(model.loc.text("Start"))
                                .font(.system(size: 14, weight: .semibold))
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                        .disabled(!model.canFinishSetup)
                    }
                }
            }
            .padding(18)
        }
    }
}
