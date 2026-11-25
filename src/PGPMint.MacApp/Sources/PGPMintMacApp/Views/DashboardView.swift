import SwiftUI
import AppKit
import PGPMintMacCore

struct DashboardView: View {
    @Bindable var model: AppModel
    let onOpenSettings: () -> Void

    @State private var exportTarget: HostMatchDto?
    @State private var pendingDelete: HostMatchDto?
    @State private var showClearAllConfirm = false

    private let tiers = ["Eco", "Balanced", "Efficiency", "Max"]

    var body: some View {
        VStack(spacing: 12) {
            headerBar

            ErrorBannerView(message: model.errorText)

            speedAndTierCard

            progressCard

            resultsCard
        }
        .padding(16)
        .sheet(item: $exportTarget) { match in
            ExportSheetView(model: model, match: match)
        }
        .alert(model.loc.text("DeleteConfirmTitle"), isPresented: Binding(
            get: { pendingDelete != nil },
            set: { if !$0 { pendingDelete = nil } }
        )) {
            Button(model.loc.text("Delete"), role: .destructive) {
                if let item = pendingDelete {
                    Task { await model.deleteMatch(item.id) }
                }
                pendingDelete = nil
            }
            Button(model.loc.text("Cancel"), role: .cancel) { pendingDelete = nil }
        } message: {
            Text(pendingDelete?.fingerprint ?? "")
        }
        .alert(model.loc.text("ClearAllConfirmTitle"), isPresented: $showClearAllConfirm) {
            Button(model.loc.text("ClearAll"), role: .destructive) {
                Task { await model.clearAllMatches() }
            }
            Button(model.loc.text("Cancel"), role: .cancel) {}
        } message: {
            Text(model.loc.format("ClearAllConfirmFormat", "\(model.matches.count)"))
        }
    }

    private var headerBar: some View {
        HStack(spacing: 8) {
            StatusDotView(colorName: model.statusIndicatorColorName)
            Text("PGPMint")
                .font(.system(size: 13.5, weight: .semibold))
                .fixedSize()
            if !model.transientNotice.isEmpty {
                Text(model.transientNotice)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(Color.accentColor)
                    .lineLimit(1)
                    .transition(.opacity)
            }
            Spacer(minLength: 6)
            if model.isSearching && model.transientNotice.isEmpty {
                let sleepMessage = model.loc.text(model.sleepProtected ? "SleepProtected" : "SleepUnavailable")
                HStack(spacing: 4) {
                    Image(systemName: model.sleepProtected ? "moon.zzz.fill" : "exclamationmark.circle")
                        .font(.system(size: 10.5))
                        .foregroundStyle(model.sleepProtected ? Color.secondary : Color.orange)
                    Text(sleepMessage)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                }
                .help(sleepMessage)
            }
            Button {
                onOpenSettings()
            } label: {
                Image(systemName: "gearshape")
                    .font(.system(size: 14))
            }
            .buttonStyle(.borderless)
            .help(model.loc.text("Settings"))
            .accessibilityLabel(model.loc.text("Settings"))
        }
        .padding(.horizontal, 2)
    }

    private var speedAndTierCard: some View {
        SurfaceCard {
            VStack(spacing: 12) {
                Text(model.activeFormattedPreview)
                    .font(.system(size: 18, weight: .semibold, design: .monospaced))
                    .tracking(1.1)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)

                HStack(alignment: .lastTextBaseline) {
                    HStack(alignment: .lastTextBaseline, spacing: 5) {
                        Text(model.actualRateNumber)
                            .font(.system(size: 38, weight: .semibold, design: .monospaced))
                            .monospacedDigit()
                        Text(model.actualRateUnit)
                            .font(.system(size: 13.5, weight: .medium, design: .monospaced))
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Text(model.efficiencyBadgeText)
                        .font(.system(size: 12, weight: .semibold, design: .monospaced))
                        .padding(.horizontal, 9)
                        .padding(.vertical, 4)
                        .background(
                            RoundedRectangle(cornerRadius: 6, style: .continuous)
                                .fill(Color.secondary.opacity(0.14))
                        )
                        .help(model.efficiencyBadgeTooltip ?? "")
                }

                tierSelectorBar

                Button {
                    Task { await model.primaryAction() }
                } label: {
                    Text(model.primaryButtonText)
                        .font(.system(size: 13.5, weight: .semibold))
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(!model.canPrimaryAction)
            }
        }
    }

    private var tierSelectorBar: some View {
        HStack(spacing: 3) {
            ForEach(tiers, id: \.self) { tier in
                let isSelected = model.displayTier == tier
                ZStack {
                    Button {
                        Task { await model.setPowerTier(tier) }
                    } label: {
                        Text(model.loc.tierLabel(tier))
                            .font(.system(size: 12.5, weight: isSelected ? .semibold : .regular))
                            .foregroundStyle(isSelected ? Color.white : (model.isTierSelectorEnabled ? Color.primary : Color.secondary))
                            .frame(maxWidth: .infinity, minHeight: 26)
                            .background(
                                RoundedRectangle(cornerRadius: 5, style: .continuous)
                                    .fill(isSelected ? Color.accentColor.opacity(model.isTierSelectorEnabled ? 1.0 : 0.45) : Color.clear)
                            )
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .disabled(!model.isTierSelectorEnabled)
                }
                .contentShape(Rectangle())
                .help(model.loc.tierTooltip(tier))
            }
        }
        .padding(3)
        .background(
            RoundedRectangle(cornerRadius: 7, style: .continuous)
                .fill(Color.secondary.opacity(0.12))
                .overlay(
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .strokeBorder(Color(nsColor: .separatorColor).opacity(0.55), lineWidth: 1)
                )
        )
    }

    private var progressCard: some View {
        SurfaceCard(padding: 14) {
            VStack(spacing: 8) {
                ProgressView(value: model.progressPercent, total: 100.0)
                    .progressViewStyle(.linear)

                HStack(spacing: 4) {
                    Text(model.probabilityText)
                        .font(.system(size: 11.5, design: .monospaced))
                        .monospacedDigit()
                        .lineLimit(1)
                        .minimumScaleFactor(0.85)
                        .help(model.singleHashProbabilityTooltip)
                    Spacer(minLength: 4)
                    Text(model.checkedCountText)
                        .font(.system(size: 11.5, design: .monospaced))
                        .monospacedDigit()
                        .lineLimit(1)
                        .minimumScaleFactor(0.85)
                        .foregroundStyle(.secondary)
                        .help(model.checkedCountTooltip)
                    Spacer(minLength: 4)
                    Text(model.liveEtaText)
                        .font(.system(size: 11.5, design: .monospaced))
                        .monospacedDigit()
                        .lineLimit(1)
                        .minimumScaleFactor(0.85)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private var resultsCard: some View {
        SurfaceCard(padding: 12) {
            if model.matches.isEmpty {
                VStack(spacing: 4) {
                    Text(model.loc.text("NoResults"))
                        .font(.system(size: 13))
                        .foregroundStyle(.secondary)
                    if model.minimizeToTrayOnClose && model.isSearching && model.hasMeasuredBaseline {
                        Text(model.loc.text("TraySpeedupGenericHint"))
                            .font(.system(size: 11.5))
                            .foregroundStyle(.tertiary)
                            .multilineTextAlignment(.center)
                    }
                }
                .frame(maxWidth: .infinity, minHeight: 54)
            } else {
                VStack(spacing: 8) {
                    HStack {
                        Text("\(model.loc.text("Saved")): \(model.matches.count)")
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(.secondary)
                        Spacer()
                        Button(model.loc.text("ClearAll")) {
                            showClearAllConfirm = true
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                    }

                    if model.matches.count <= 5 {
                        VStack(spacing: 6) {
                            ForEach(model.matches) { item in
                                matchRow(item)
                            }
                        }
                    } else {
                        ScrollView {
                            LazyVStack(spacing: 6) {
                                ForEach(model.matches) { item in
                                    matchRow(item)
                                }
                            }
                        }
                        .frame(height: 180)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .top)
            }
        }
    }

    private func matchRow(_ item: HostMatchDto) -> some View {
        HStack(spacing: 6) {
            Text(item.formattedKeyId)
                .font(.system(size: 13.5, weight: .semibold, design: .monospaced))
                .lineLimit(1)
            Spacer()
            Button {
                copyFingerprint(item.fingerprint)
            } label: {
                Image(systemName: "doc.on.doc")
                    .font(.system(size: 12))
            }
            .buttonStyle(.borderless)
            .help(model.loc.text("Copy"))

            Button(".asc") {
                exportTarget = item
            }
            .font(.system(size: 11.5, design: .monospaced))
            .buttonStyle(.bordered)
            .controlSize(.small)

            Button(role: .destructive) {
                pendingDelete = item
            } label: {
                Image(systemName: "trash")
                    .font(.system(size: 12))
            }
            .buttonStyle(.borderless)
            .help(model.loc.text("DeleteKeyTooltip"))
        }
        .frame(minHeight: 28)
    }

    private func copyFingerprint(_ fingerprint: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(fingerprint, forType: .string)
        model.flashNotice(model.loc.text("Copied"))
    }
}
