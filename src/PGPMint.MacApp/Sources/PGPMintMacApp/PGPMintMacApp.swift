import SwiftUI
import AppKit
import PGPMintMacCore

@main
struct PGPMintMacApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        Window("PGPMint", id: "main") {
            RootContentView(model: delegate.model) {
                delegate.adjustWindowHeight()
            }
            .background(WindowAccessor { window in
                delegate.attachMainWindow(window)
            })
            .frame(minWidth: 390, idealWidth: 460, minHeight: 320)
        }
        .defaultSize(width: 460, height: 434)
        .windowResizability(.contentMinSize)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }

        Settings {
            SettingsView(model: delegate.model)
                .frame(minWidth: 440, idealWidth: 480, minHeight: 480, idealHeight: 560)
        }
    }
}

private struct RootContentView: View {
    @Bindable var model: AppModel
    let onLayoutStateChanged: () -> Void

    var body: some View {
        Group {
            if !model.isUnlocked {
                UnlockView(model: model)
            } else if model.showSetupWizard {
                SetupWizardView(model: model)
            } else {
                DashboardView(model: model)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(Color(nsColor: .windowBackgroundColor))
        .onChange(of: model.isUnlocked) { _, _ in onLayoutStateChanged() }
        .onChange(of: model.showSetupWizard) { _, _ in onLayoutStateChanged() }
        .onChange(of: model.matches.count) { _, _ in onLayoutStateChanged() }
        .onChange(of: model.errorText.isEmpty) { _, _ in onLayoutStateChanged() }
        .onChange(of: model.isSearching) { _, _ in onLayoutStateChanged() }
        .onChange(of: model.devices.count) { _, _ in onLayoutStateChanged() }
    }
}

private struct WindowAccessor: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async {
            if let window = view.window {
                onWindow(window)
            }
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async {
            if let window = nsView.window {
                onWindow(window)
            }
        }
    }
}
