import AppKit
import SwiftUI
import os
import PGPMintMacCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private static let defaultWidth: CGFloat = 400
    private static let minWidth: CGFloat = 360
    private static let minHeight: CGFloat = 220
    private static let logger = Logger(subsystem: "org.pgpmint.desktop", category: "window")

    let powerManager = MacPowerActivityManager()
    let loc = LocalizationStore()
    lazy var model = AppModel(transport: ProcessHostTransport(), loc: loc, powerManager: powerManager)

    private var menuBar: MenuBarController?
    private var mainWindow: NSWindow?
    private var mainHostingController: NSHostingController<RootContentView>?
    private var settingsWindow: NSWindow?
    private var terminating = false
    private var lastMeasuredHeight: CGFloat = 0
    private var hasPresentedInitialWindow = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        installMainMenu()

        menuBar = MenuBarController(
            model: model,
            onShowWindow: { [weak self] in self?.showMainWindow() },
            onOpenSettings: { [weak self] in self?.openSettingsWindow() },
            onQuit: { [weak self] in self?.requestQuit() }
        )

        model.onMenuBarStateChanged = { [weak self] in
            self?.menuBar?.refresh()
        }

        createMainWindowOffscreen()

        Task { [weak self] in
            await self?.bootstrapAndPresentMainWindow()
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag {
            showMainWindow()
        }
        return true
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if terminating { return true }
        if sender === settingsWindow {
            sender.orderOut(nil)
            return false
        }
        if sender === mainWindow {
            if model.shouldHideToMenuBarOnClose {
                Self.logger.info("Main window closed -> hiding to menu bar (searching=\(self.model.isSearching))")
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

    func updateWindowHeight(to measuredHeight: CGFloat) {
        let rounded = ceil(measuredHeight)
        guard rounded > 100 else { return }
        let changed = abs(rounded - lastMeasuredHeight) >= 2
        lastMeasuredHeight = rounded
        guard hasPresentedInitialWindow, changed else { return }
        applyWindowSize(contentHeight: rounded, forceDefaultWidth: false, animate: true)
    }

    func showMainWindow() {
        guard let window = mainWindow else { return }
        if !hasPresentedInitialWindow {
            presentInitialMainWindow()
            return
        }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }

    func hideMainWindow() {
        mainWindow?.orderOut(nil)
        menuBar?.refresh()
    }

    @objc func openSettingsWindow() {
        let window: NSWindow
        if let existing = settingsWindow {
            window = existing
        } else {
            let hosting = NSHostingController(
                rootView: SettingsView(model: model)
                    .frame(minWidth: 440, maxWidth: .infinity, minHeight: 480, maxHeight: .infinity)
                    .background(Color(nsColor: .windowBackgroundColor))
            )
            hosting.sizingOptions = []
            let win = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 480, height: 560),
                styleMask: [.titled, .closable, .miniaturizable, .resizable],
                backing: .buffered,
                defer: false
            )
            win.contentViewController = hosting
            win.contentMinSize = NSSize(width: 440, height: 480)
            win.setContentSize(NSSize(width: 480, height: 560))
            win.title = model.loc.text("Settings")
            win.delegate = self
            win.isReleasedWhenClosed = false
            win.isRestorable = false
            win.backgroundColor = .windowBackgroundColor
            win.center()
            settingsWindow = win
            window = win
        }

        window.title = model.loc.text("Settings")
        let size = window.contentRect(forFrameRect: window.frame).size
        Self.logger.info("Present settings window at \(Int(size.width))x\(Int(size.height))")
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }

    func requestQuit() {
        NSApp.terminate(nil)
    }

    private func createMainWindowOffscreen() {
        let rootView = RootContentView(
            model: model,
            onOpenSettings: { [weak self] in self?.openSettingsWindow() },
            onMeasuredHeight: { [weak self] height in self?.updateWindowHeight(to: height) }
        )
        let hosting = NSHostingController(rootView: rootView)
        hosting.sizingOptions = []

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: Self.defaultWidth, height: 410),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.contentViewController = hosting
        window.delegate = self
        window.title = "PGPMint"
        window.titlebarAppearsTransparent = false
        window.isReleasedWhenClosed = false
        window.isRestorable = false
        window.backgroundColor = .windowBackgroundColor
        window.contentMinSize = NSSize(width: Self.minWidth, height: Self.minHeight)

        mainHostingController = hosting
        mainWindow = window
    }

    private func bootstrapAndPresentMainWindow() async {
        // Fallback reveal only if bootstrap blocks (e.g. interactive OS Keychain dialog).
        let fallbackTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_200_000_000)
            guard !Task.isCancelled else { return }
            self?.presentInitialMainWindow()
        }

        await model.bootstrap()
        fallbackTask.cancel()
        menuBar?.refresh()

        // Flush SwiftUI layout offscreen for the resolved view (Dashboard / Setup / Unlock)
        // so ContentHeightKey reports the exact inner height before showing the window.
        mainHostingController?.view.frame = NSRect(x: 0, y: 0, width: Self.defaultWidth, height: 420)
        mainHostingController?.view.layoutSubtreeIfNeeded()
        try? await Task.sleep(nanoseconds: 45_000_000)
        presentInitialMainWindow()
    }

    private func presentInitialMainWindow() {
        guard !hasPresentedInitialWindow, let window = mainWindow else { return }
        let height = measureCurrentContentHeight(width: Self.defaultWidth)
        lastMeasuredHeight = height
        applyWindowSize(contentHeight: height, forceDefaultWidth: true, animate: false)
        window.center()
        hasPresentedInitialWindow = true
        Self.logger.info("Present initial main window at \(Int(Self.defaultWidth))x\(Int(height))")
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    private func measureCurrentContentHeight(width: CGFloat) -> CGFloat {
        guard let hosting = mainHostingController else {
            return max(lastMeasuredHeight, 380)
        }
        hosting.view.frame = NSRect(x: 0, y: 0, width: width, height: max(lastMeasuredHeight, 380))
        hosting.view.layoutSubtreeIfNeeded()
        if lastMeasuredHeight > 100 {
            Self.logger.info("Using PreferenceKey contentHeight=\(Int(self.lastMeasuredHeight))")
            return lastMeasuredHeight
        }
        let safeTop = hosting.view.safeAreaInsets.top
        let fitted = ceil(hosting.sizeThatFits(in: CGSize(width: width, height: .greatestFiniteMagnitude)).height - safeTop)
        Self.logger.info("Using sizeThatFits contentHeight=\(Int(fitted)) (safeTop=\(Int(safeTop)))")
        return fitted > 100 ? fitted : 390
    }

    private func applyWindowSize(contentHeight: CGFloat, forceDefaultWidth: Bool, animate: Bool) {
        guard let window = mainWindow else { return }
        let screenMax = (window.screen ?? NSScreen.main)?.visibleFrame.height ?? 800
        let clampedHeight = min(max(contentHeight, Self.minHeight), screenMax - 80)
        let currentContent = window.contentRect(forFrameRect: window.frame)
        let targetWidth = forceDefaultWidth
            ? Self.defaultWidth
            : max(Self.minWidth, currentContent.width)

        window.contentMinSize = NSSize(width: Self.minWidth, height: clampedHeight)
        if abs(currentContent.height - clampedHeight) < 2 && abs(currentContent.width - targetWidth) < 2 {
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
    }

    private func installMainMenu() {
        let mainMenu = NSMenu()

        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About PGPMint", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        let settingsItem = NSMenuItem(title: "Settings...", action: #selector(openSettingsWindow), keyEquivalent: ",")
        settingsItem.target = self
        appMenu.addItem(settingsItem)
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide PGPMint", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let hideOthers = NSMenuItem(title: "Hide Others", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(hideOthers)
        appMenu.addItem(withTitle: "Show All", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit PGPMint", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        mainMenu.addItem(appItem)

        let editItem = NSMenuItem()
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        mainMenu.addItem(editItem)

        let windowItem = NSMenuItem()
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowItem.submenu = windowMenu
        mainMenu.addItem(windowItem)

        NSApp.mainMenu = mainMenu
        NSApp.windowsMenu = windowMenu
    }
}
