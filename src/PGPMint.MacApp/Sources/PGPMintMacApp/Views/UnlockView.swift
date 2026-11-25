import SwiftUI
import PGPMintMacCore

struct UnlockView: View {
    @Bindable var model: AppModel
    @State private var password = ""

    private var needsHumanPassword: Bool {
        !model.supportsAutomaticUnlock || (model.hasLegacyVault && !model.hasAutomaticMetadata)
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                HStack {
                    HStack(spacing: 8) {
                        Image(systemName: "key.horizontal.fill")
                            .foregroundStyle(Color.accentColor)
                            .font(.system(size: 18, weight: .semibold))
                        Text("PGPMint")
                            .font(.system(size: 20, weight: .semibold))
                    }
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
                    .frame(width: 140)
                }

                ErrorBannerView(message: model.errorText)

                SurfaceCard {
                    VStack(alignment: .leading, spacing: 14) {
                        Text(model.loc.text("Unlock"))
                            .font(.system(size: 16, weight: .semibold))

                        Text(model.loc.text(model.supportsAutomaticUnlock ? "SystemVaultHint" : "VaultHint"))
                            .font(.system(size: 12.5))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)

                        if model.attemptingAutoUnlock {
                            HStack(spacing: 10) {
                                ProgressView()
                                    .controlSize(.small)
                                Text(model.loc.text("OpeningSystemVault"))
                                    .font(.system(size: 13))
                                    .foregroundStyle(.secondary)
                            }
                            .padding(.vertical, 6)
                        } else if needsHumanPassword {
                            VStack(alignment: .leading, spacing: 8) {
                                SecureField(model.loc.text("VaultPassword"), text: $password)
                                    .textFieldStyle(.roundedBorder)
                                    .onSubmit { submitPassword() }

                                Button(action: submitPassword) {
                                    Text(model.loc.text("Unlock"))
                                        .frame(maxWidth: .infinity)
                                }
                                .buttonStyle(.borderedProminent)
                                .controlSize(.large)
                                .disabled(model.isBusy || password.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                            }
                        } else {
                            Button {
                                Task { await model.attemptAutomaticUnlock() }
                            } label: {
                                Text(model.loc.text("Unlock"))
                                    .frame(maxWidth: .infinity)
                            }
                            .buttonStyle(.borderedProminent)
                            .controlSize(.large)
                            .disabled(model.isBusy)
                        }
                    }
                }

                if !model.dataDirectory.isEmpty {
                    Text("\(model.loc.text("DataDirectory")): \(model.dataDirectory)")
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(.tertiary)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(20)
        }
    }

    private func submitPassword() {
        let candidate = password
        password = ""
        guard !candidate.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        Task {
            _ = await model.unlockWithPassword(candidate)
        }
    }
}
