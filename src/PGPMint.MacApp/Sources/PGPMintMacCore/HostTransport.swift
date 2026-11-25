import Foundation
import Darwin

public protocol HostTransport: AnyObject, Sendable {
    var events: AsyncStream<HostEventMessage> { get }
    func start() async throws
    func send<P: Encodable, R: Decodable>(method: String, params: P?, as type: R.Type) async throws -> R
    func sendNoParams<R: Decodable>(method: String, as type: R.Type) async throws -> R
    func shutdown() async
}

private struct RawRequest<P: Encodable>: Encodable {
    let id: String
    let method: String
    let params: P?
}

private struct EmptyRequest: Encodable {
    let id: String
    let method: String
}

private struct EnvelopeHeader: Decodable {
    let type: String
    let id: String?
    let ok: Bool?
    let errorCode: String?
    let error: String?
    let event: String?
}

private struct ResponsePayload<R: Decodable>: Decodable {
    let result: R?
}

private struct TelemetryEventEnvelope: Decodable {
    let payload: HostTelemetryDto
}

private struct MatchEventEnvelope: Decodable {
    let payload: HostMatchDto
}

public final class ProcessHostTransport: HostTransport, @unchecked Sendable {
    public let events: AsyncStream<HostEventMessage>
    private let eventContinuation: AsyncStream<HostEventMessage>.Continuation
    private let executableOverride: URL?
    private let extraArguments: [String]

    private let lock = NSLock()
    private let ioQueue = DispatchQueue(label: "org.pgpmint.host.io", qos: .utility)
    private let writeQueue = DispatchQueue(label: "org.pgpmint.host.write", qos: .userInitiated)
    private var process: Process?
    private var stdinHandle: FileHandle?
    private var pending: [String: CheckedContinuation<Data, Error>] = [:]
    private var nextId: Int = 1
    private var stderrBuffer: String = ""
    private var started = false
    private var stopping = false

    public init(executableOverride: URL? = nil, extraArguments: [String] = []) {
        self.executableOverride = executableOverride
        self.extraArguments = extraArguments
        var cont: AsyncStream<HostEventMessage>.Continuation!
        self.events = AsyncStream { continuation in
            cont = continuation
        }
        self.eventContinuation = cont
    }

    public static func resolveHostExecutable(override: URL? = nil) -> URL? {
        if let override, FileManager.default.isExecutableFile(atPath: override.path) {
            return override
        }
        if let envPath = ProcessInfo.processInfo.environment["PGPMINT_HOST_PATH"],
           !envPath.isEmpty,
           FileManager.default.isExecutableFile(atPath: envPath) {
            return URL(fileURLWithPath: envPath)
        }
        let directHelper = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Helpers/pgpmint-host")
        if FileManager.default.isExecutableFile(atPath: directHelper.path) {
            return directHelper
        }
        let nestedHelper = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Helpers/pgpmint-host/pgpmint-host")
        if FileManager.default.isExecutableFile(atPath: nestedHelper.path) {
            return nestedHelper
        }
        let sibling = Bundle.main.executableURL?
            .deletingLastPathComponent()
            .appendingPathComponent("pgpmint-host")
        if let sibling, FileManager.default.isExecutableFile(atPath: sibling.path) {
            return sibling
        }
        return nil
    }

    public func start() async throws {
        let alreadyStarted = lock.withLock { () -> Bool in
            if started { return true }
            started = true
            return false
        }
        if alreadyStarted { return }

        guard let hostUrl = Self.resolveHostExecutable(override: executableOverride) else {
            lock.withLock { started = false }
            throw HostError(code: "host_missing", message: "PGPMint host helper executable was not found inside the application bundle.")
        }

        let proc = Process()
        proc.executableURL = hostUrl
        var args = extraArguments
        let env = ProcessInfo.processInfo.environment
        if let dataDir = env["PGPMINT_DATA_DIR"], !dataDir.isEmpty, !args.contains("--data-dir") {
            args.append(contentsOf: ["--data-dir", dataDir])
        }
        if let backend = env["PGPMINT_BACKEND"], !backend.isEmpty, !args.contains("--backend") {
            args.append(contentsOf: ["--backend", backend])
        }
        proc.arguments = args
        proc.currentDirectoryURL = hostUrl.deletingLastPathComponent()

        let inPipe = Pipe()
        let outPipe = Pipe()
        let errPipe = Pipe()
        proc.standardInput = inPipe
        proc.standardOutput = outPipe
        proc.standardError = errPipe

        proc.terminationHandler = { [weak self] terminated in
            self?.handleTermination(status: terminated.terminationStatus)
        }

        do {
            try proc.run()
        } catch {
            lock.withLock { started = false }
            throw HostError(code: "host_launch_failed", message: error.localizedDescription)
        }

        lock.withLock {
            self.process = proc
            self.stdinHandle = inPipe.fileHandleForWriting
        }

        startReadingStdout(outPipe.fileHandleForReading)
        startReadingStderr(errPipe.fileHandleForReading)
    }

    public func send<P: Encodable, R: Decodable>(method: String, params: P?, as type: R.Type) async throws -> R {
        let requestId = allocateId()
        let lineData: Data
        if let params {
            lineData = try JSONEncoder().encode(RawRequest(id: requestId, method: method, params: params))
        } else {
            lineData = try JSONEncoder().encode(EmptyRequest(id: requestId, method: method))
        }
        return try await executeRequest(id: requestId, payload: lineData, as: type)
    }

    public func sendNoParams<R: Decodable>(method: String, as type: R.Type) async throws -> R {
        let requestId = allocateId()
        let lineData = try JSONEncoder().encode(EmptyRequest(id: requestId, method: method))
        return try await executeRequest(id: requestId, payload: lineData, as: type)
    }

    public func shutdown() async {
        lock.withLock { stopping = true }
        let (proc, handle) = lock.withLock { () -> (Process?, FileHandle?) in
            (process, stdinHandle)
        }
        if let proc, proc.isRunning, let handle {
            let reqId = allocateId()
            if var payload = try? JSONEncoder().encode(EmptyRequest(id: reqId, method: "shutdown")) {
                payload.append(0x0A)
                try? handle.write(contentsOf: payload)
            }
        }
        lock.withLock { stdinHandle = nil }
        try? handle?.close()
        if let proc, proc.isRunning {
            let deadline = Date().addingTimeInterval(2.5)
            while proc.isRunning && Date() < deadline {
                try? await Task.sleep(nanoseconds: 50_000_000)
            }
            if proc.isRunning {
                proc.terminate()
            }
        }
        eventContinuation.finish()
    }

    private func allocateId() -> String {
        lock.withLock {
            let id = String(nextId)
            nextId += 1
            return id
        }
    }

    private func executeRequest<R: Decodable>(id: String, payload: Data, as type: R.Type) async throws -> R {
        var framed = payload
        framed.append(0x0A)

        let rawResponse: Data = try await withCheckedThrowingContinuation { continuation in
            let handle: FileHandle? = lock.withLock {
                guard let h = stdinHandle, process?.isRunning == true else {
                    return nil
                }
                pending[id] = continuation
                return h
            }

            guard let handle else {
                continuation.resume(throwing: HostError(code: "host_not_running", message: "PGPMint host helper is not running."))
                return
            }

            writeQueue.async { [weak self] in
                do {
                    try handle.write(contentsOf: framed)
                } catch {
                    if let pendingCont = self?.lock.withLock({ self?.pending.removeValue(forKey: id) }) {
                        pendingCont.resume(throwing: HostError(code: "host_write_failed", message: error.localizedDescription))
                    }
                }
            }
        }

        let decoder = JSONDecoder()
        let header = try decoder.decode(EnvelopeHeader.self, from: rawResponse)
        if header.ok != true {
            throw HostError(
                code: header.errorCode ?? "host_error",
                message: header.error ?? "Operation failed."
            )
        }
        let wrapped = try decoder.decode(ResponsePayload<R>.self, from: rawResponse)
        guard let result = wrapped.result else {
            throw HostError(code: "empty_response", message: "Host returned an empty result.")
        }
        return result
    }

    private func startReadingStdout(_ handle: FileHandle) {
        ioQueue.async { [weak self] in
            let fd = handle.fileDescriptor
            var buffer = Data()
            var chunk = [UInt8](repeating: 0, count: 16384)
            while true {
                let n = Darwin.read(fd, &chunk, chunk.count)
                if n > 0 {
                    buffer.append(contentsOf: chunk[0..<n])
                    while let newlineIndex = buffer.firstIndex(of: 0x0A) {
                        let lineData = Data(buffer[..<newlineIndex])
                        buffer.removeSubrange(...newlineIndex)
                        if !lineData.isEmpty {
                            self?.handleStdoutLine(lineData)
                        }
                    }
                } else if n == 0 {
                    break
                } else if errno == EINTR {
                    continue
                } else {
                    break
                }
            }
            if !buffer.isEmpty {
                self?.handleStdoutLine(buffer)
            }
            try? handle.close()
        }
    }

    private func startReadingStderr(_ handle: FileHandle) {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let fd = handle.fileDescriptor
            var chunk = [UInt8](repeating: 0, count: 4096)
            while true {
                let n = Darwin.read(fd, &chunk, chunk.count)
                if n > 0 {
                    if let text = String(bytes: chunk[0..<n], encoding: .utf8) {
                        self?.lock.withLock {
                            self?.stderrBuffer.append(text)
                            if let count = self?.stderrBuffer.count, count > 4096 {
                                self?.stderrBuffer = String(self!.stderrBuffer.suffix(4096))
                            }
                        }
                    }
                } else if n == 0 {
                    break
                } else if errno == EINTR {
                    continue
                } else {
                    break
                }
            }
            try? handle.close()
        }
    }

    private func handleStdoutLine(_ data: Data) {
        let decoder = JSONDecoder()
        guard let header = try? decoder.decode(EnvelopeHeader.self, from: data) else {
            return
        }
        if header.type == "response" {
            if let id = header.id,
               let cont = lock.withLock({ pending.removeValue(forKey: id) }) {
                cont.resume(returning: data)
            }
            return
        }
        if header.type == "event" {
            switch header.event {
            case "telemetry":
                if let env = try? decoder.decode(TelemetryEventEnvelope.self, from: data) {
                    eventContinuation.yield(.telemetry(env.payload))
                }
            case "matchFound":
                if let env = try? decoder.decode(MatchEventEnvelope.self, from: data) {
                    eventContinuation.yield(.matchFound(env.payload))
                }
            default:
                break
            }
        }
    }

    private func handleTermination(status: Int32) {
        let (waiters, wasStopping, stderrTail) = lock.withLock { () -> ([CheckedContinuation<Data, Error>], Bool, String) in
            let list = Array(pending.values)
            pending.removeAll()
            stdinHandle = nil
            return (list, stopping, stderrBuffer)
        }
        let err = HostError(code: "host_terminated", message: "Host exited with code \(status).")
        for cont in waiters {
            cont.resume(throwing: err)
        }
        if !wasStopping {
            eventContinuation.yield(.terminated(status, stderrTail))
        }
        eventContinuation.finish()
    }
}
