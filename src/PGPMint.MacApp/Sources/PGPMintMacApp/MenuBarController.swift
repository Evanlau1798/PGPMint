import AppKit
import PGPMintMacCore

@MainActor
final class MenuBarController: NSObject, NSMenuDelegate {
    private let model: AppModel
    private let onShowWindow: () -> Void
    private let onOpenSettings: () -> Void
    private let onQuit: () -> Void

    private let statusItem: NSStatusItem
    private let menu = NSMenu()
    private let idleBrandImage: NSImage

    init(
        model: AppModel,
        onShowWindow: @escaping () -> Void,
        onOpenSettings: @escaping () -> Void,
        onQuit: @escaping () -> Void
    ) {
        self.model = model
        self.onShowWindow = onShowWindow
        self.onOpenSettings = onOpenSettings
        self.onQuit = onQuit
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        self.idleBrandImage = Self.makeIdleBrandImage()
        super.init()

        menu.delegate = self
        if let button = statusItem.button {
            button.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
            button.toolTip = "PGPMint"
        }

        statusItem.menu = menu
        rebuildMenu()
        refresh()
    }

    func refresh() {
        guard let button = statusItem.button else { return }
        if model.menuBarTitle.isEmpty {
            button.image = idleBrandImage
            button.imagePosition = .imageOnly
            button.title = ""
        } else {
            // Show clean live speed text only (no leading icon beside GH/s).
            button.image = nil
            button.imagePosition = .noImage
            button.title = model.menuBarTitle
        }
        button.toolTip = buildTooltip()
    }

    func menuWillOpen(_ menu: NSMenu) {
        // Populate menu items once on open; never mutate items on 1 Hz speed ticks while hovering submenus.
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

        addItem(title: model.loc.text("Open"), action: #selector(handleShow))

        if model.isUnlocked && !model.showSetupWizard {
            menu.addItem(.separator())
            let toggleKey = (model.isSearching || model.isPaused) ? "Stop" : "Start"
            let toggleItem = addItem(title: model.loc.text(toggleKey), action: #selector(handlePrimaryToggle))
            toggleItem.isEnabled = model.canPrimaryAction

            let tierMenu = NSMenu(title: model.loc.text("PowerTier"))
            for tier in ["Eco", "Balanced", "Efficiency", "Max"] {
                let item = NSMenuItem(title: model.loc.tierLabel(tier), action: #selector(handleSelectTier(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = tier
                item.state = (model.displayTier == tier) ? .on : .off
                item.isEnabled = model.isTierSelectorEnabled
                tierMenu.addItem(item)
            }
            let tierParent = NSMenuItem(title: model.loc.text("PowerTier"), action: nil, keyEquivalent: "")
            tierParent.submenu = tierMenu
            tierParent.isEnabled = model.isTierSelectorEnabled
            menu.addItem(tierParent)
        }

        menu.addItem(.separator())
        addItem(title: model.loc.text("Settings") + "...", action: #selector(handleSettings), keyEquivalent: ",")
        addItem(title: model.loc.text("Quit"), action: #selector(handleQuit), keyEquivalent: "q")
    }

    @discardableResult
    private func addItem(title: String, action: Selector, keyEquivalent: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: keyEquivalent)
        item.target = self
        menu.addItem(item)
        return item
    }

    private static func makeIdleBrandImage() -> NSImage {
        let size = NSSize(width: 18, height: 18)
        let image = NSImage(size: size, flipped: false) { rect in
            NSColor.labelColor.setStroke()
            NSColor.labelColor.setFill()

            // Minimalist chamfered octagon outline + geometric diamond core (matches PGPMint SVG mark).
            let oct = NSBezierPath()
            oct.move(to: NSPoint(x: 6.2, y: 16.0))
            oct.line(to: NSPoint(x: 11.8, y: 16.0))
            oct.line(to: NSPoint(x: 16.0, y: 11.8))
            oct.line(to: NSPoint(x: 16.0, y: 6.2))
            oct.line(to: NSPoint(x: 11.8, y: 2.0))
            oct.line(to: NSPoint(x: 6.2, y: 2.0))
            oct.line(to: NSPoint(x: 2.0, y: 6.2))
            oct.line(to: NSPoint(x: 2.0, y: 11.8))
            oct.close()
            oct.lineWidth = 1.6
            oct.stroke()

            let diamond = NSBezierPath()
            diamond.move(to: NSPoint(x: 9.0, y: 12.6))
            diamond.line(to: NSPoint(x: 12.6, y: 9.0))
            diamond.line(to: NSPoint(x: 9.0, y: 5.4))
            diamond.line(to: NSPoint(x: 5.4, y: 9.0))
            diamond.close()
            diamond.fill()
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "PGPMint"
        return image
    }

    @objc private func handleShow() { onShowWindow() }
    @objc private func handleSettings() { onOpenSettings() }
    @objc private func handleQuit() { onQuit() }
    @objc private func handlePrimaryToggle() { Task { await model.primaryAction() } }
    @objc private func handleSelectTier(_ sender: NSMenuItem) {
        guard let tier = sender.representedObject as? String else { return }
        Task { await model.setPowerTier(tier) }
    }
}
