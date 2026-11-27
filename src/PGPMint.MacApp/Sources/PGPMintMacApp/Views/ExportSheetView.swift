import SwiftUI
import AppKit
import UniformTypeIdentifiers
import PGPMintMacCore

struct ExportSheetView: View {
    @Bindable var model: AppModel
    let match: HostMatchDto
    @Environment(\.dismiss) private var dismiss

    @State private var uid = ""
    @State private var email = ""
    @State private var passphrase = ""
    @State private var includeEncryptionSubkey = true
    @State private var expirationDays: Int = 0
    @State private var validationMessage = ""

    private var canSubmit: Bool {
        (!uid.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            || !email.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            && !passphrase.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !model.isBusy
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(model.loc.text("ExportTitle"))
                .font(.system(size: 16, weight: .semibold))

            Text(match.formattedKeyId)
                .font(.system(size: 13, weight: .medium, design: .monospaced))
                .foregroundStyle(.secondary)

            VStack(spacing: 10) {
                TextField(model.loc.text("NamePlaceholder"), text: $uid)
                    .textFieldStyle(.roundedBorder)
                TextField(model.loc.text("EmailPlaceholder"), text: $email)
                    .textFieldStyle(.roundedBorder)
                SecureField(model.loc.text("ExportPassword"), text: $passphrase)
                    .textFieldStyle(.roundedBorder)
                Toggle(model.loc.text("ExportIncludeEncryptSubkey"), isOn: $includeEncryptionSubkey)
                    .toggleStyle(.checkbox)
                    .frame(maxWidth: .infinity, alignment: .leading)
                HStack {
                    Text(model.loc.text("ExportExpirationLabel"))
                        .font(.system(size: 12.5))
                    Spacer()
                    Picker(model.loc.text("ExportExpirationLabel"), selection: $expirationDays) {
                        Text(model.loc.text("ExportExpirationNever")).tag(0)
                        Text(model.loc.text("ExportExpiration1Year")).tag(365)
                        Text(model.loc.text("ExportExpiration2Years")).tag(730)
                        Text(model.loc.text("ExportExpiration5Years")).tag(1825)
                    }
                    .labelsHidden()
                    .frame(width: 140)
                }
            }

            if !validationMessage.isEmpty {
                Text(validationMessage)
                    .font(.system(size: 12))
                    .foregroundStyle(.red)
            }

            HStack {
                Spacer()
                Button(model.loc.text("Cancel")) {
                    passphrase = ""
                    dismiss()
                }
                .keyboardShortcut(.cancelAction)

                Button(model.loc.text("Save")) {
                    performExport()
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(!canSubmit)
            }
        }
        .padding(20)
        .frame(width: 400)
    }

    private func performExport() {
        validationMessage = ""
        guard canSubmit else {
            validationMessage = model.loc.text("ExportRequired")
            return
        }

        guard let privateUrl = chooseSavePath(
            title: model.loc.text("ExportPrivate"),
            suggestedName: "\(match.keyId).secret.asc"
        ) else { return }

        guard let publicUrl = chooseSavePath(
            title: model.loc.text("ExportPublic"),
            suggestedName: "\(match.keyId).public.asc"
        ) else { return }

        if privateUrl.standardizedFileURL == publicUrl.standardizedFileURL {
            validationMessage = model.loc.text("OperationFailed")
            return
        }

        let capturedUid = uid
        let capturedEmail = email
        let capturedPassphrase = passphrase
        let capturedIncludeSubkey = includeEncryptionSubkey
        let capturedExpUnix: Int64? = expirationDays > 0
            ? Int64(Date().timeIntervalSince1970) + Int64(expirationDays) * 86_400
            : nil
        passphrase = ""

        Task {
            let ok = await model.exportMatch(
                recordId: match.id,
                uid: capturedUid,
                email: capturedEmail,
                passphrase: capturedPassphrase,
                privatePath: privateUrl.path,
                publicPath: publicUrl.path,
                includeEncryptionSubkey: capturedIncludeSubkey,
                expiresAtUnix: capturedExpUnix
            )
            if ok {
                dismiss()
            } else {
                validationMessage = model.errorText
            }
        }
    }

    private func chooseSavePath(title: String, suggestedName: String) -> URL? {
        let panel = NSSavePanel()
        panel.title = title
        panel.nameFieldStringValue = suggestedName
        panel.allowedContentTypes = [UTType(filenameExtension: "asc") ?? .plainText]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url, url.isFileURL else {
            return nil
        }
        if FileManager.default.fileExists(atPath: url.path) {
            validationMessage = model.loc.text("OperationFailed")
            return nil
        }
        return url
    }
}
