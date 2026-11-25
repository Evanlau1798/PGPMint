import AppKit
import PGPMintMacCore

@MainActor
final class MenuBarController: NSObject {
    private let model: AppModel
    private let onShowWindow: () -> Void
    private let onHideWindow: () -> Void
    private let onOpenSettings: () -> Void
    private let onQuit: () -> Void

    private let statusItem: NSStatusItem
    private let menu = NSMenu()

    init(
        model: AppModel,
        onShowWindow: @escaping () -> Void,
        onHideWindow: @escaping () -> Void,
        onOpenSettings: @escaping () -> Void,
        onQuit: @escaping () -> Void
    ) {
        self.model = model
        self.onShowWindow = onShowWindow
        self.onHideWindow = onHideWindow
        self.onOpenSettings = onOpenSettings
        self.onQuit = onQuit
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()

        if let button = statusItem.button {
            if let image = NSImage(systemSymbolName: "key.horizontal.fill", accessibilityDescription: "PGPMint") {
                image.isTemplate = true
                button.image = image
                button.imagePosition = .imageLeading
            }
            button.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
            button.toolTip = "PGPMint"
        }

        statusItem.menu = menu
        refresh()
    }

    func refresh() {
        if let button = statusItem.button {
            if model.menuBarTitle.isEmpty {
                button.title = ""
            } else {
                button.title = " " + model.menuBarTitle
            }
            button.toolTip = buildTooltip()
        }
        rebuildMenu()
    }

    private func buildTooltip() -> String {
        if model.isSearching {
            let rate = Formatting.formatHashRateCompact(model.telemetry.totalActualHps)
            return "PGPMint - \(rate) | \(model.probabilityText) | \(model.liveEtaText)"
        }
        return "PGPMint - \(model.efficiencyBadgeText)"
    }

    private func rebuildMenu() {
        menu.removeAllItems()

        let header = NSMenuItem(title: summaryTitle(), action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())

        addItem(title: model.loc.text("Open"), action: #selector(handleShow))
        if model.shouldHideToMenuBarOnClose {
            addItem(title: model.loc.text("Hide"), action: #selector(handleHide))
        }

        if model.isUnlocked && !model.showSetupWizard {
            menu.addItem(.separator())
            if model.isSearching || model.isPaused {
                let pauseKey = model.isPaused ? "Resume" : "Pause"
                let pauseItem = addItem(title: model.loc.text(pauseKey), action: #selector(handlePauseResume))
                pauseItem.isEnabled = !model.isBusy && (model.telemetry.state == "Running" || model.telemetry.state == "Paused")

                let stopItem = addItem(title: model.loc.text("Stop"), action: #selector(handleStop))
                stopItem.isEnabled = !model.isBusy && !model.isStopping
            } else {
                let startItem = addItem(title: model.loc.text("Start"), action: #selector(handleStart))
                startItem.isEnabled = model.canPrimaryAction
            }
        }

        menu.addItem(.separator())
        addItem(title: model.loc.text("Settings") + "...", action: #selector(handleSettings), keyEquivalent: ",")
        addItem(title: model.loc.text("Quit"), action: #selector(handleQuit), keyEquivalent: "q")
    }

    private func summaryTitle() -> String {
        guard model.isUnlocked else { return "PGPMint" }
        if model.isSearching {
            return "\(Formatting.formatHashRateCompact(model.telemetry.totalActualHps)) \u{00B7} \(model.probabilityText)"
        }
        return "PGPMint \u{00B7} \(model.efficiencyBadgeText)"
    }

    @discardableResult
    private func addItem(title: String, action: Selector, keyEquivalent: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: keyEquivalent)
        item.target = self
        menu.addItem(item)
        return item
    }

    @objc private func handleShow() { onShowWindow() }
    @objc private func handleHide() { onHideWindow() }
    @objc private func handleSettings() { onOpenSettings() }
    @objc private func handleQuit() { onQuit() }
    @objc private func handleStart() { Task { await model.startSearch() } }
    @objc private func handleStop() { Task { await model.stopSearch() } }

    @objc private func handlePauseResume() {
        Task {
            if model.isPaused {
                await model.resumeSearch()
            } else {
                await model.pauseSearch()
            }
        }
    }
}
