import AppKit
import PGPMintMacCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private static let defaultWidth: CGFloat = 460
    private static let maxAutoStretchMatches = 5

    let powerManager = MacPowerActivityManager()
    let loc = LocalizationStore()
    lazy var model = AppModel(transport: ProcessHostTransport(), loc: loc, powerManager: powerManager)

    private var menuBar: MenuBarController?
    private weak var mainWindow: NSWindow?
    private var terminating = false
    private var lastAutoHeightKey = ""

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)

        menuBar = MenuBarController(
            model: model,
            onShowWindow: { [weak self] in self?.showMainWindow() },
            onHideWindow: { [weak self] in self?.hideMainWindow() },
            onOpenSettings: { [weak self] in self?.openSettingsWindow() },
            onQuit: { [weak self] in self?.requestQuit() }
        )

        model.onMenuBarStateChanged = { [weak self] in
            self?.menuBar?.refresh()
        }

        Task {
            await model.bootstrap()
            menuBar?.refresh()
            adjustWindowHeight(force: true)
        }
    }

    func attachMainWindow(_ window: NSWindow) {
        guard mainWindow !== window else { return }
        mainWindow = window
        window.delegate = self
        window.title = "PGPMint"
        window.titlebarAppearsTransparent = false
        window.isReleasedWhenClosed = false
        adjustWindowHeight(force: true)
    }

    func adjustWindowHeight(force: Bool = false) {
        guard let window = mainWindow else { return }
        let visibleRows = min(model.matches.count, Self.maxAutoStretchMatches)
        let hasError = !model.errorText.isEmpty
        let mode: String = !model.isUnlocked ? "unlock" : (model.showSetupWizard ? "setup" : "dash")
        let key = "\(mode)|\(visibleRows)|\(hasError)|\(model.isSearching)|\(model.devices.count)"
        if !force && key == lastAutoHeightKey { return }
        lastAutoHeightKey = key

        DispatchQueue.main.async { [weak self, weak window] in
            guard let self, let window, let contentView = window.contentView else { return }
            contentView.layoutSubtreeIfNeeded()
            let currentWidth = max(Self.defaultWidth, window.contentRect(forFrameRect: window.frame).width)
            let fittingHeight = contentView.fittingSize.height
            let screenMax = (window.screen ?? NSScreen.main)?.visibleFrame.height ?? 800
            let clampedHeight = min(max(fittingHeight, 260), screenMax - 80)
            let targetFrameSize = window.frameRect(
                forContentRect: NSRect(origin: .zero, size: NSSize(width: currentWidth, height: clampedHeight))
            ).size

            var frame = window.frame
            let topY = frame.maxY
            frame.size = targetFrameSize
            frame.origin.y = topY - frame.size.height
            window.setFrame(frame, display: true, animate: !force)
        }
    }

    func showMainWindow() {
        NSApp.activate(ignoringOtherApps: true)
        if let window = mainWindow {
            window.makeKeyAndOrderFront(nil)
            window.orderFrontRegardless()
        } else {
            for win in NSApp.windows where win.canBecomeMain {
                mainWindow = win
                win.delegate = self
                win.makeKeyAndOrderFront(nil)
                break
            }
        }
    }

    func hideMainWindow() {
        mainWindow?.orderOut(nil)
    }

    func openSettingsWindow() {
        NSApp.activate(ignoringOtherApps: true)
        NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
    }

    func requestQuit() {
        NSApp.terminate(nil)
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag {
            showMainWindow()
        }
        return true
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if terminating { return true }
        if sender === mainWindow {
            if model.shouldHideToMenuBarOnClose {
                sender.orderOut(nil)
                menuBar?.refresh()
                return false
            }
            if model.isSearching || model.isPaused {
                let alert = NSAlert()
                alert.messageText = model.loc.text("StopExit")
                alert.informativeText = model.loc.text("NoTray")
                alert.alertStyle = .warning
                alert.addButton(withTitle: model.loc.text("Quit"))
                alert.addButton(withTitle: model.loc.text("Cancel"))
                if alert.runModal() == .alertFirstButtonReturn {
                    requestQuit()
                }
                return false
            }
            requestQuit()
            return false
        }
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if terminating { return .terminateNow }
        terminating = true
        Task { @MainActor in
            await model.prepareForQuit()
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }
}
