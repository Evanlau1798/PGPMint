import AppKit
import os
import PGPMintMacCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private static let defaultWidth: CGFloat = 460
    private static let logger = Logger(subsystem: "org.pgpmint.desktop", category: "window")

    let powerManager = MacPowerActivityManager()
    let loc = LocalizationStore()
    lazy var model = AppModel(transport: ProcessHostTransport(), loc: loc, powerManager: powerManager)

    private var menuBar: MenuBarController?
    private weak var mainWindow: NSWindow?
    private var terminating = false
    private var lastMeasuredHeight: CGFloat = 0
    private var hasAppliedInitialSize = false

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
        }
    }

    func attachMainWindow(_ window: NSWindow) {
        guard mainWindow !== window else { return }
        mainWindow = window
        window.delegate = self
        window.title = "PGPMint"
        window.titlebarAppearsTransparent = false
        window.isReleasedWhenClosed = false
        if lastMeasuredHeight > 100 {
            applyWindowHeight(lastMeasuredHeight, animate: false)
        }
    }

    func updateWindowHeight(to measuredHeight: CGFloat) {
        let rounded = ceil(measuredHeight)
        guard rounded > 100 else { return }
        if abs(rounded - lastMeasuredHeight) < 2 && hasAppliedInitialSize {
            return
        }
        lastMeasuredHeight = rounded
        applyWindowHeight(rounded, animate: hasAppliedInitialSize)
    }

    private func applyWindowHeight(_ contentHeight: CGFloat, animate: Bool) {
        guard let window = mainWindow else { return }
        let screenMax = (window.screen ?? NSScreen.main)?.visibleFrame.height ?? 800
        let clampedHeight = min(max(contentHeight, 260), screenMax - 80)
        let currentContent = window.contentRect(forFrameRect: window.frame)
        let targetWidth = max(Self.defaultWidth, currentContent.width)

        window.contentMinSize = NSSize(width: 390, height: clampedHeight)
        if abs(currentContent.height - clampedHeight) < 2 && abs(currentContent.width - targetWidth) < 2 {
            hasAppliedInitialSize = true
            return
        }

        let targetFrameSize = window.frameRect(
            forContentRect: NSRect(origin: .zero, size: NSSize(width: targetWidth, height: clampedHeight))
        ).size

        var frame = window.frame
        let topY = frame.maxY
        frame.size = targetFrameSize
        frame.origin.y = topY - frame.size.height
        if let visible = (window.screen ?? NSScreen.main)?.visibleFrame, frame.origin.y < visible.minY {
            frame.origin.y = visible.minY
        }

        Self.logger.info("Resize main window content to \(Int(targetWidth))x\(Int(clampedHeight))")
        window.setFrame(frame, display: true, animate: animate)
        hasAppliedInitialSize = true
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
