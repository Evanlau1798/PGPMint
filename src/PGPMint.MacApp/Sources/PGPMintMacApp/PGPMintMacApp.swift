import AppKit
import SwiftUI
import PGPMintMacCore

@main
@MainActor
enum PGPMintMacMain {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        withExtendedLifetime(delegate) {
            app.run()
        }
    }
}

struct ContentHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        let next = nextValue()
        if next > 0 { value = next }
    }
}

struct RootContentView: View {
    @Bindable var model: AppModel
    let onOpenSettings: () -> Void
    let onMeasuredHeight: (CGFloat) -> Void

    var body: some View {
        VStack(spacing: 0) {
            Group {
                if !model.isUnlocked {
                    UnlockView(model: model)
                } else if model.showSetupWizard {
                    SetupWizardView(model: model)
                } else {
                    DashboardView(model: model, onOpenSettings: onOpenSettings)
                }
            }
            .fixedSize(horizontal: false, vertical: true)
            .background(
                GeometryReader { geo in
                    Color.clear.preference(key: ContentHeightKey.self, value: geo.size.height)
                }
            )
        }
        .frame(maxWidth: .infinity, alignment: .top)
        .background(Color(nsColor: .windowBackgroundColor))
        .onPreferenceChange(ContentHeightKey.self) { height in
            if height > 100 {
                onMeasuredHeight(height)
            }
        }
    }
}
