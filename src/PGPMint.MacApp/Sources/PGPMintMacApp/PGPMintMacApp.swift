import SwiftUI
import AppKit
import PGPMintMacCore

@main
struct PGPMintMacApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        Window("PGPMint", id: "main") {
            RootContentView(model: delegate.model) { measuredHeight in
                delegate.updateWindowHeight(to: measuredHeight)
            }
            .background(WindowAccessor { window in
                delegate.attachMainWindow(window)
            })
            .frame(minWidth: 360, idealWidth: 400)
        }
        .defaultSize(width: 400, height: 420)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }

        Settings {
            SettingsView(model: delegate.model)
                .frame(minWidth: 440, idealWidth: 480, minHeight: 480, idealHeight: 560)
        }
    }
}

private struct ContentHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        let next = nextValue()
        if next > 0 { value = next }
    }
}

private struct RootContentView: View {
    @Bindable var model: AppModel
    let onMeasuredHeight: (CGFloat) -> Void

    var body: some View {
        VStack(spacing: 0) {
            Group {
                if !model.isUnlocked {
                    UnlockView(model: model)
                } else if model.showSetupWizard {
                    SetupWizardView(model: model)
                } else {
                    DashboardView(model: model)
                }
            }
            .fixedSize(horizontal: false, vertical: true)
            .background(
                GeometryReader { geo in
                    Color.clear.preference(key: ContentHeightKey.self, value: geo.size.height)
                }
            )
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(Color(nsColor: .windowBackgroundColor))
        .onPreferenceChange(ContentHeightKey.self) { height in
            if height > 100 {
                onMeasuredHeight(height)
            }
        }
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
