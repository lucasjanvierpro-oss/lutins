// Lutins — un petit perso par session Claude Code, dans une encoche en haut de l'écran.
//
// Claude Code envoie ses événements (hooks déclarés dans ~/.claude/settings.json)
// en HTTP sur 127.0.0.1:3850. Chaque session devient un lutin :
//   bleu = il travaille · orange + « ! » = il t'attend · vert = il a fini · gris = il dort.

import Cocoa
import Network
import ServiceManagement

let hookPort: UInt16 = 3850
let claudeBundleID = "com.anthropic.claudefordesktop"

// MARK: - Sessions

enum LutinState: String {
    case working, waiting, done, idle
}

final class Session {
    let id: String
    let order: Int
    var cwd: String
    var transcriptPath: String?
    var title: String?
    var entrypoint = ""
    var terminal = ""
    var state: LutinState = .idle
    var stateSince = Date()
    var tool: String?
    var waitReason: String?
    var turnStart: Date?
    var turnDuration: TimeInterval?
    var lastEvent = Date()
    var titleCheckedAt = Date.distantPast
    var interrupted = false
    var ended = false

    init(id: String, cwd: String, order: Int) {
        self.id = id
        self.cwd = cwd
        self.order = order
    }

    var name: String {
        if let title, !title.isEmpty { return title }
        let base = (cwd as NSString).lastPathComponent
        return base.isEmpty || base.hasPrefix("scratch-") ? "Sans dossier" : base
    }
}

final class Store {
    private(set) var sessions: [String: Session] = [:]
    private var counter = 0
    var onChange: () -> Void = {}
    var onFinished: (Session) -> Void = { _ in }

    var ordered: [Session] { sessions.values.sorted { $0.order < $1.order } }

    func handle(_ event: String, _ body: [String: Any], _ query: [String: String]) {
        guard let id = body["session_id"] as? String else { return }
        let cwd = body["cwd"] as? String ?? ""

        if event == "SessionEnd" {
            guard let s = sessions[id] else { return }
            // L'app Claude peut arrêter le process entre deux messages : un lutin
            // « fini » reste visible jusqu'à ce qu'il s'endorme.
            if s.state == .done { s.ended = true } else { sessions[id] = nil }
            onChange()
            return
        }

        let s: Session
        if let existing = sessions[id] {
            s = existing
        } else {
            counter += 1
            s = Session(id: id, cwd: cwd, order: counter)
            sessions[id] = s
        }
        if !cwd.isEmpty { s.cwd = cwd }
        if let path = body["transcript_path"] as? String, !path.isEmpty { s.transcriptPath = path }
        if let entry = query["entry"], !entry.isEmpty { s.entrypoint = entry }
        if let term = query["term"], !term.isEmpty { s.terminal = term }
        s.lastEvent = Date()
        s.ended = false
        let tool = body["tool_name"] as? String

        switch event {
        case "UserPromptSubmit":
            s.turnStart = Date()
            s.tool = nil
            set(s, .working)
            refreshTitle(s, force: true)
        case "PreToolUse":
            if s.turnStart == nil || s.state == .done || s.state == .idle { s.turnStart = Date() }
            s.tool = tool
            switch tool {
            case "AskUserQuestion": wait(s, "A une question pour toi")
            case "ExitPlanMode": wait(s, "Attend que tu valides son plan")
            default: set(s, .working)
            }
        case "PostToolUse":
            s.tool = nil
            set(s, .working)
        case "PermissionRequest":
            wait(s, "Attend ta permission")
        case "Notification":
            let type = body["notification_type"] as? String
            let message = body["message"] as? String ?? ""
            let needsYou = type.map { $0 == "permission_prompt" || $0 == "elicitation_dialog" }
                ?? !message.localizedCaseInsensitiveContains("waiting for your input")
            if needsYou && s.state != .waiting { wait(s, "Attend ta permission") }
        case "Stop":
            let wasDone = s.state == .done
            s.tool = nil
            s.turnDuration = s.turnStart.map { Date().timeIntervalSince($0) }
            set(s, .done)
            refreshTitle(s, force: true)
            if !wasDone { onFinished(s) }
        default:
            break
        }
        onChange()
    }

    /// Clic sur un lutin vert : on l'a vu, il peut aller dormir.
    func markSeen(_ id: String) {
        guard let s = sessions[id], s.state == .done else { return }
        set(s, .idle)
        onChange()
    }

    func tidy() {
        for s in sessions.values where s.state == .done || s.state == .idle { sessions[s.id] = nil }
        onChange()
    }

    /// Toutes les 3 s : interruptions, sessions plantées, lutins qui s'endorment puis s'en vont.
    func housekeeping() {
        let now = Date()
        var changed = false
        for s in sessions.values {
            switch s.state {
            case .working, .waiting:
                if let path = s.transcriptPath, Transcript.interrupted(path, after: s.turnStart ?? s.stateSince) {
                    set(s, .idle)
                    s.interrupted = true
                    changed = true
                } else if s.state == .working,
                          now.timeIntervalSince(s.lastEvent) > 15 * 60,
                          now.timeIntervalSince(Transcript.modified(s.transcriptPath) ?? s.lastEvent) > 15 * 60 {
                    set(s, .idle)
                    changed = true
                }
            case .done:
                if now.timeIntervalSince(s.stateSince) > 30 * 60 {
                    set(s, .idle)
                    changed = true
                }
            case .idle:
                if s.ended || now.timeIntervalSince(s.stateSince) > 20 * 60 {
                    sessions[s.id] = nil
                    changed = true
                }
            }
            if s.state != .idle { refreshTitle(s, force: false) }
        }
        if changed { onChange() }
    }

    private func set(_ s: Session, _ state: LutinState) {
        if s.state != state {
            s.state = state
            s.stateSince = Date()
        }
        if state != .waiting { s.waitReason = nil }
        if state != .idle { s.interrupted = false }
    }

    private func wait(_ s: Session, _ reason: String) {
        set(s, .waiting)
        s.waitReason = reason
    }

    private func refreshTitle(_ s: Session, force: Bool) {
        guard let path = s.transcriptPath else { return }
        if !force && Date().timeIntervalSince(s.titleCheckedAt) < 30 { return }
        s.titleCheckedAt = Date()
        DispatchQueue.global(qos: .utility).async {
            let title = Transcript.title(path)
            DispatchQueue.main.async {
                guard let title, title != s.title else { return }
                s.title = title
                self.onChange()
            }
        }
    }
}

// MARK: - Transcripts (~/.claude/projects/…/<session>.jsonl)

enum Transcript {
    private static let dates: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    /// Lignes complètes d'un morceau du fichier, la plus récente en premier.
    private static func lines(_ path: String, fromEnd: Bool, bytes: UInt64) -> [Data] {
        guard let handle = FileHandle(forReadingAtPath: path) else { return [] }
        defer { try? handle.close() }
        guard let size = try? handle.seekToEnd() else { return [] }
        let start = fromEnd && size > bytes ? size - bytes : 0
        try? handle.seek(toOffset: start)
        let chunk: Data?
        if fromEnd { chunk = try? handle.readToEnd() } else { chunk = try? handle.read(upToCount: Int(bytes)) }
        guard let data = chunk else { return [] }
        var parts = data.split(separator: 10, omittingEmptySubsequences: true).map { Data($0) }
        if start > 0, !parts.isEmpty { parts.removeFirst() }
        if !fromEnd, UInt64(data.count) == bytes, !parts.isEmpty { parts.removeLast() }
        return parts.reversed()
    }

    private static func json(_ line: Data) -> [String: Any]? {
        try? JSONSerialization.jsonObject(with: line) as? [String: Any]
    }

    /// Le titre affiché dans l'app Claude pour cette conversation.
    static func title(_ path: String) -> String? {
        let needle = Data("Title\"".utf8)
        for fromEnd in [true, false] {
            for line in lines(path, fromEnd: fromEnd, bytes: 512 * 1024) where line.range(of: needle) != nil {
                guard let obj = json(line) else { continue }
                if let t = (obj["customTitle"] ?? obj["aiTitle"]) as? String, !t.isEmpty { return t }
            }
        }
        return nil
    }

    /// Vrai si le dernier message de la conversation est une interruption faite après `date`.
    static func interrupted(_ path: String, after date: Date) -> Bool {
        let marker = Data("[Request interrupted by user".utf8)
        for line in lines(path, fromEnd: true, bytes: 64 * 1024).prefix(60) {
            guard let obj = json(line) else { continue }
            guard let type = obj["type"] as? String, type == "user" || type == "assistant" else { continue }
            guard type == "user", line.range(of: marker) != nil else { return false }
            guard let stamp = (obj["timestamp"] as? String).flatMap(dates.date(from:)) else { return true }
            return stamp >= date.addingTimeInterval(-1)
        }
        return false
    }

    static func modified(_ path: String?) -> Date? {
        guard let path else { return nil }
        return (try? FileManager.default.attributesOfItem(atPath: path))?[.modificationDate] as? Date
    }
}

// MARK: - Serveur des hooks

struct HTTPRequest {
    let method: String
    let path: String
    let query: [String: String]
    let body: Data

    init?(_ data: Data) {
        guard let end = data.range(of: Data("\r\n\r\n".utf8)) else { return nil }
        let head = String(decoding: data[data.startIndex..<end.lowerBound], as: UTF8.self)
        let lines = head.components(separatedBy: "\r\n")
        let start = lines.first?.split(separator: " ") ?? []
        guard start.count >= 2 else { return nil }
        var length = 0
        for line in lines.dropFirst() {
            let kv = line.split(separator: ":", maxSplits: 1)
            if kv.count == 2, kv[0].lowercased() == "content-length" {
                length = Int(kv[1].trimmingCharacters(in: .whitespaces)) ?? 0
            }
        }
        guard data.distance(from: end.upperBound, to: data.endIndex) >= length else { return nil }
        method = String(start[0])
        let components = URLComponents(string: String(start[1]))
        path = components?.path ?? String(start[1])
        var query: [String: String] = [:]
        for item in components?.queryItems ?? [] { query[item.name] = item.value ?? "" }
        self.query = query
        body = data.subdata(in: end.upperBound..<data.index(end.upperBound, offsetBy: length))
    }
}

final class HookServer {
    private let queue = DispatchQueue(label: "lutins.hooks")
    private var listener: NWListener?
    var onEvent: (String, [String: Any], [String: String]) -> Void = { _, _, _ in }
    var state: () -> Data = { Data() }
    var onFailure: (String) -> Void = { _ in }

    func start() {
        do {
            // Uniquement en local : rien n'est accessible depuis le réseau.
            let params = NWParameters.tcp
            params.allowLocalEndpointReuse = true
            params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: hookPort)!)
            let listener = try NWListener(using: params)
            listener.newConnectionHandler = { [weak self] in self?.serve($0) }
            listener.stateUpdateHandler = { [weak self] state in
                guard case .failed(let error) = state else { return }
                DispatchQueue.main.async { self?.onFailure("Port \(hookPort) indisponible : \(error.localizedDescription)") }
            }
            listener.start(queue: queue)
            self.listener = listener
        } catch {
            onFailure("Port \(hookPort) indisponible : \(error.localizedDescription)")
        }
    }

    private func serve(_ connection: NWConnection) {
        connection.start(queue: queue)
        var buffer = Data()
        func receive() {
            connection.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { [weak self] data, _, isComplete, error in
                if let data { buffer.append(data) }
                if let request = HTTPRequest(buffer) {
                    self?.respond(connection, request)
                } else if isComplete || error != nil || buffer.count > 64 << 20 {
                    connection.cancel()
                } else {
                    receive()
                }
            }
        }
        receive()
    }

    private func respond(_ connection: NWConnection, _ request: HTTPRequest) {
        var status = "204 No Content"
        var body = Data()
        if request.method == "POST", request.path.hasPrefix("/hook/") {
            let event = String(request.path.dropFirst("/hook/".count))
            if let json = try? JSONSerialization.jsonObject(with: request.body) as? [String: Any] {
                DispatchQueue.main.async { self.onEvent(event, json, request.query) }
            }
        } else if request.method == "GET", request.path == "/state" {
            body = DispatchQueue.main.sync { self.state() }
            status = "200 OK"
        } else {
            status = "404 Not Found"
        }
        let head = "HTTP/1.1 \(status)\r\nContent-Type: application/json\r\nContent-Length: \(body.count)\r\nConnection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + body, completion: .contentProcessed { _ in connection.cancel() })
    }
}

// MARK: - Branchement dans ~/.claude/settings.json

enum Hooks {
    static let settings = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".claude/settings.json")
    static let marker = "127.0.0.1:\(hookPort)/hook/"
    static let events = ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "SessionEnd"]

    static var installed: Bool {
        guard let text = try? String(contentsOf: settings, encoding: .utf8) else { return false }
        return events.allSatisfy { text.contains(marker + $0) }
    }

    static func command(_ event: String) -> String {
        "curl -s -m 2 --connect-timeout 0.2 -X POST -H \"Content-Type: application/json\" -H \"Expect:\" --data-binary @- "
            + "\"http://\(marker)\(event)?entry=${CLAUDE_CODE_ENTRYPOINT}&term=${TERM_PROGRAM}\" >/dev/null 2>&1; exit 0"
    }

    // Retire nos hooks sans toucher à ceux des autres outils (VSCodeWorkers, etc.).
    private static let strip = """
        def strip: map(.hooks = ((.hooks // []) | map(select((.command // "") | contains($marker) | not))))
                   | map(select(.hooks | length > 0));
        """

    @discardableResult
    static func install() -> String? {
        let filter = strip + """
             .hooks = ((.hooks // {}) | with_entries(.value |= strip))
             | reduce ($cmds | to_entries[]) as $e (.;
                 .hooks[$e.key] = ((.hooks[$e.key] // [])
                   + [{matcher: "*", hooks: [{type: "command", command: $e.value, async: true, timeout: 5}]}]))
            """
        let commands = Dictionary(uniqueKeysWithValues: events.map { ($0, command($0)) })
        return rewrite(filter, commands: commands)
    }

    @discardableResult
    static func uninstall() -> String? {
        let filter = strip + """
             if .hooks then .hooks |= (with_entries(.value |= strip) | with_entries(select(.value | length > 0))) else . end
            """
        return rewrite(filter, commands: [:])
    }

    /// Passe settings.json dans jq (qui garde l'ordre des clés) puis l'écrit d'un coup. Renvoie une erreur ou nil.
    private static func rewrite(_ filter: String, commands: [String: String]) -> String? {
        let fm = FileManager.default
        try? fm.createDirectory(at: settings.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !fm.fileExists(atPath: settings.path) { try? Data("{}\n".utf8).write(to: settings) }
        let backup = settings.appendingPathExtension("lutins-backup")
        if !fm.fileExists(atPath: backup.path) { try? fm.copyItem(at: settings, to: backup) }

        guard let cmds = try? JSONSerialization.data(withJSONObject: commands),
              fm.isExecutableFile(atPath: "/usr/bin/jq") else { return "jq introuvable" }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/jq")
        process.arguments = ["--arg", "marker", marker, "--argjson", "cmds", String(decoding: cmds, as: UTF8.self), filter, settings.path]
        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err
        do { try process.run() } catch { return error.localizedDescription }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        let errors = err.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0, (try? JSONSerialization.jsonObject(with: data)) != nil else {
            return String(decoding: errors, as: UTF8.self)
        }
        do { try data.write(to: settings, options: .atomic) } catch { return error.localizedDescription }
        return nil
    }
}
