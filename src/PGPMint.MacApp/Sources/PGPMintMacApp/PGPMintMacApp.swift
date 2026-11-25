import SwiftUI
import AppKit
import PGPMintMacCore

@main
struct PGPMintMacApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        Window("PGPMint", id: "main") {
            RootContentView(model: delegate.model)
                .background(WindowAccessor { window in
                    delegate.attachMainWindow(window)
                })
                .frame(minWidth: 440, idealWidth: 480, minHeight: 580, idealHeight: 700)
        }
        .defaultSize(width: 480, height: 700)
        .windowResizability(.contentMinSize)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }

        Settings {
            SettingsView(model: delegate.model)
                .frame(minWidth: 460, idealWidth: 500, minHeight: 500, idealHeight: 580)
        }
    }
}

private struct RootContentView: View {
    @Bindable var model: AppModel

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
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
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
