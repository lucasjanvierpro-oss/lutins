// Point d'entrée : relie le serveur des hooks, les sessions et l'encoche.

import Cocoa
import ServiceManagement

final class AppDelegate: NSObject, NSApplicationDelegate {
    let store = Store()
    let server = HookServer()
    let notifier = Notifier()
    lazy var island = Island(store: store)

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let id = Bundle.main.bundleIdentifier,
           NSRunningApplication.runningApplications(withBundleIdentifier: id).count > 1 {
            NSApp.terminate(nil)
            return
        }
        store.onChange = { [weak self] in self?.island.refresh() }
        store.onFinished = { [weak self] session in
            if UserDefaults.standard.bool(forKey: "sound") { NSSound(named: "Glass")?.play() }
            self?.notifier.finished(session)
        }
        notifier.onClick = { [weak self] id in
            guard let self else { return }
            if let session = self.store.sessions[id] { self.open(session) } else { self.openApp(claudeBundleID) }
        }
        notifier.setUp()
        enableOpenAtLoginByDefault()
        server.onEvent = { [weak self] event, body, query in self?.store.handle(event, body, query) }
        server.state = { [weak self] in self?.stateJSON() ?? Data() }
        server.onFailure = { [weak self] message in
            self?.island.problem = message
            self?.island.refresh()
        }
        server.start()
        if !Hooks.installed, let error = Hooks.install() { island.problem = "Branchement à Claude Code impossible : \(error)" }

        island.optionsMenu = { [weak self] in self?.optionsMenu() ?? NSMenu() }
        island.onOpen = { [weak self] session in self?.open(session) }
        island.rebuild()

        let animation = Timer(timeInterval: Effects.frameDuration, repeats: true) { [weak self] _ in self?.island.tick() }
        let chores = Timer(timeInterval: 3, repeats: true) { [weak self] _ in self?.store.housekeeping() }
        RunLoop.main.add(animation, forMode: .common)
        RunLoop.main.add(chores, forMode: .common)
    }

    // MARK: Réglages (clic droit ou « ⋯ »)

    private func optionsMenu() -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let sessions = store.ordered
        if let problem = island.problem { menu.addItem(disabled("⚠️ " + problem)) }
        menu.addItem(disabled(Texts.summary(sessions)))
        menu.addItem(.separator())

        let sizes = NSMenu()
        for size in IslandSize.allCases {
            let item = action(size.label, #selector(pickSize(_:)))
            item.representedObject = size.rawValue
            item.state = island.size == size ? .on : .off
            sizes.addItem(item)
        }
        menu.addItem(submenu("Taille de l'encoche", sizes))

        let skins = NSMenu()
        for id in Skins.ids + [Skins.mix] {
            let item = action(id == Skins.mix ? "Un perso différent par session" : Skins.name(id), #selector(pickSkin(_:)))
            item.representedObject = id
            item.state = island.skinChoice == id ? .on : .off
            if id != Skins.mix { item.image = Skins.icon(id) }
            if id == Skins.mix { skins.addItem(.separator()) }
            skins.addItem(item)
        }
        menu.addItem(submenu("Personnage", skins))

        if NSScreen.screens.count > 1 {
            let current = UserDefaults.standard.string(forKey: "screen") ?? "all"
            let screens = NSMenu()
            for (title, value) in [("Tous les écrans", "all")] + NSScreen.screens.map({ ($0.localizedName, $0.localizedName) }) {
                let item = action(title, #selector(pickScreen(_:)))
                item.representedObject = value
                item.state = current == value ? .on : .off
                screens.addItem(item)
            }
            menu.addItem(submenu("Écran", screens))
        }

        let sound = action("Son quand c'est fini", #selector(toggleSound))
        sound.state = UserDefaults.standard.bool(forKey: "sound") ? .on : .off
        menu.addItem(sound)
        let login = action("Ouvrir au démarrage du Mac", #selector(toggleLogin))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)
        let notify = action("Notification quand c'est fini", #selector(toggleNotify))
        notify.state = notifier.enabled ? .on : .off
        menu.addItem(notify)
        let tidy = action("Ranger les persos finis", #selector(tidyUp))
        tidy.isEnabled = sessions.contains { $0.state == .done || $0.state == .idle }
        menu.addItem(tidy)
        menu.addItem(action("Remettre l'encoche en haut au centre", #selector(resetPlacement)))
        if !Hooks.installed { menu.addItem(action("Brancher à Claude Code", #selector(connect))) }
        menu.addItem(.separator())

        menu.addItem(action("Débrancher de Claude Code et quitter", #selector(unplugAndQuit)))
        let quit = NSMenuItem(title: "Quitter Lutins", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "")
        quit.target = NSApp
        menu.addItem(quit)
        return menu
    }

    private func disabled(_ title: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }

    private func action(_ title: String, _ selector: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: selector, keyEquivalent: "")
        item.target = self
        return item
    }

    private func submenu(_ title: String, _ menu: NSMenu) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.submenu = menu
        return item
    }

    // MARK: Actions

    private func open(_ session: Session) {
        store.markSeen(session.id)
        let terminals = [
            "vscode": "com.microsoft.VSCode",
            "iTerm.app": "com.googlecode.iterm2",
            "Apple_Terminal": "com.apple.Terminal",
            "WarpTerminal": "dev.warp.Warp-Stable",
            "ghostty": "com.mitchellh.ghostty",
        ]
        openApp(session.entrypoint.contains("desktop") ? claudeBundleID : terminals[session.terminal] ?? claudeBundleID)
    }

    private func openApp(_ bundle: String) {
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundle) {
            NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
        }
    }

    /// Lancement au démarrage du Mac : activé d'office, l'option du menu permet de le couper.
    private func enableOpenAtLoginByDefault() {
        let defaults = UserDefaults.standard
        if defaults.object(forKey: "openAtLogin") == nil { defaults.set(true, forKey: "openAtLogin") }
        if defaults.bool(forKey: "openAtLogin") && SMAppService.mainApp.status != .enabled {
            try? SMAppService.mainApp.register()
        }
    }

    @objc private func pickSize(_ item: NSMenuItem) {
        UserDefaults.standard.set(item.representedObject as? String, forKey: "size")
        island.refresh()
    }

    @objc private func pickSkin(_ item: NSMenuItem) {
        UserDefaults.standard.set(item.representedObject as? String, forKey: "skin")
        island.refresh()
    }

    @objc private func pickScreen(_ item: NSMenuItem) {
        UserDefaults.standard.set(item.representedObject as? String, forKey: "screen")
        island.rebuild()
    }

    @objc private func tidyUp() { store.tidy() }

    @objc private func toggleSound() {
        let defaults = UserDefaults.standard
        defaults.set(!defaults.bool(forKey: "sound"), forKey: "sound")
        if defaults.bool(forKey: "sound") { NSSound(named: "Glass")?.play() }
    }

    @objc private func toggleNotify() { notifier.enabled.toggle() }

    @objc private func resetPlacement() { island.resetPlacements() }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                UserDefaults.standard.set(false, forKey: "openAtLogin")
                try SMAppService.mainApp.unregister()
            } else {
                UserDefaults.standard.set(true, forKey: "openAtLogin")
                try SMAppService.mainApp.register()
            }
        } catch {
            alert("Impossible de changer l'ouverture au démarrage", error.localizedDescription)
        }
    }

    @objc private func connect() {
        if let error = Hooks.install() {
            alert("Branchement à Claude Code impossible", error)
        } else {
            island.problem = nil
            island.refresh()
        }
    }

    @objc private func unplugAndQuit() {
        if let error = Hooks.uninstall() {
            alert("Débranchement impossible", error)
            return
        }
        NSApp.terminate(nil)
    }

    private func alert(_ title: String, _ message: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.runModal()
    }

    private func stateJSON() -> Data {
        notifier.refreshStatus()
        let list: [[String: Any]] = store.ordered.map { s in
            [
                "id": s.id,
                "name": s.name,
                "state": s.state.rawValue,
                "tool": s.tool.map { $0 as Any } ?? NSNull(),
                "waitReason": s.waitReason.map { $0 as Any } ?? NSNull(),
                "entrypoint": s.entrypoint,
                "since": Int(Date().timeIntervalSince(s.stateSince)),
            ]
        }
        let login = ["notRegistered", "enabled", "requiresApproval", "notFound"]
        let state: [String: Any] = [
            "problem": island.problem.map { $0 as Any } ?? NSNull(),
            "openAtLogin": login[min(SMAppService.mainApp.status.rawValue, login.count - 1)],
            "notifications": notifier.status,
            "sessions": list,
        ]
        return (try? JSONSerialization.data(withJSONObject: state, options: .prettyPrinted)) ?? Data()
    }
}

// MARK: - Aperçus pour vérifier les dessins sans écran

func writePNG(_ rep: NSBitmapImageRep, _ path: String) {
    try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
}

/// `Lutins --preview fichier.png` : chaque état sur 6 images, fond clair puis sombre.
func renderSpritePreview(to path: String) {
    let looks = [
        LutinLook(state: .working, age: 10), LutinLook(state: .waiting, age: 10),
        LutinLook(state: .done, age: 0), LutinLook(state: .done, age: 10), LutinLook(state: .idle, age: 10),
    ]
    let frames = 6, pixel: CGFloat = 12, pad: CGFloat = 18
    let cell = Sprite.size(pixel)
    let cellW = cell.width + 2 * pad, cellH = cell.height + 2 * pad
    let width = CGFloat(frames) * cellW, height = CGFloat(looks.count) * cellH * 2
    guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(width), pixelsHigh: Int(height),
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                     colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
          let context = NSGraphicsContext(bitmapImageRep: rep) else { return }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(cgContext: context.cgContext, flipped: true)
    context.cgContext.translateBy(x: 0, y: height)
    context.cgContext.scaleBy(x: 1, y: -1)
    NSGraphicsContext.current?.shouldAntialias = false
    for (half, background) in [NSColor(white: 0.93, alpha: 1), NSColor.black].enumerated() {
        let top = CGFloat(half) * CGFloat(looks.count) * cellH
        background.setFill()
        NSRect(x: 0, y: top, width: width, height: CGFloat(looks.count) * cellH).fill()
        for (row, look) in looks.enumerated() {
            for f in 0..<frames {
                Sprite.draw(look, frame: f, x: CGFloat(f) * cellW + pad, y: top + CGFloat(row) * cellH + pad, pixel: pixel)
            }
        }
    }
    NSGraphicsContext.restoreGraphicsState()
    writePNG(rep, path)
}

/// `Lutins --preview-island dossier` : l'encoche repliée et dépliée, avec de fausses sessions.
func renderIslandPreview(to folder: String) {
    let store = Store()
    let fake: [(String, String, String, [String: Any])] = [
        ("a", "/p/serveur-data", "UserPromptSubmit", [:]),
        ("a", "/p/serveur-data", "PreToolUse", ["tool_name": "Bash"]),
        ("b", "/p/api", "UserPromptSubmit", [:]),
        ("b", "/p/api", "PermissionRequest", [:]),
        ("c", "/p/site-vitrine", "UserPromptSubmit", [:]),
        ("c", "/p/site-vitrine", "Stop", [:]),
        ("d", "/p/app-mobile", "UserPromptSubmit", [:]),
        ("d", "/p/app-mobile", "PreToolUse", ["tool_name": "Edit"]),
    ]
    for (id, cwd, event, extra) in fake {
        store.handle(event, extra.merging(["session_id": id, "cwd": cwd]) { a, _ in a }, [:])
    }
    store.sessions["a"]?.title = "Migration Postgres"
    store.sessions["b"]?.title = "Tests de l'API de paiement"
    let island = Island(store: store)
    island.skinOverride = Skins.mix
    guard let screen = NSScreen.screens.first else { return }
    for size in IslandSize.allCases {
        island.sizeOverride = size
        if let rep = island.snapshot(expanded: false, screen: screen) { writePNG(rep, "\(folder)/island-\(size.rawValue).png") }
    }
    island.sizeOverride = .medium
    if let rep = island.snapshot(expanded: true, screen: screen) { writePNG(rep, "\(folder)/island-expanded.png") }
}

/// `Lutins --preview-skins fichier.png` : chaque perso (lignes) dans chaque état (colonnes), sur fond noir.
func renderSkinsPreview(to path: String) {
    let poses: [(LutinState, TimeInterval, Int)] = [
        (.working, 10, 0), (.working, 10, 1), (.waiting, 10, 0), (.done, 0, 2), (.done, 10, 0), (.idle, 10, 0),
    ]
    let pixel: CGFloat = 8, pad: CGFloat = 16
    let cell = Skins.size(pixel)
    let cellW = cell.width + 2 * pad, cellH = cell.height + 2 * pad
    let width = CGFloat(poses.count) * cellW, height = CGFloat(Skins.ids.count) * cellH
    guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(width), pixelsHigh: Int(height),
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                     colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
          let context = NSGraphicsContext(bitmapImageRep: rep) else { return }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(cgContext: context.cgContext, flipped: true)
    context.cgContext.translateBy(x: 0, y: height)
    context.cgContext.scaleBy(x: 1, y: -1)
    NSGraphicsContext.current?.shouldAntialias = false
    NSColor.black.setFill()
    NSRect(x: 0, y: 0, width: width, height: height).fill()
    for (row, id) in Skins.ids.enumerated() {
        for (col, pose) in poses.enumerated() {
            Skins.draw(id, LutinLook(state: pose.0, age: pose.1), frame: pose.2,
                       x: CGFloat(col) * cellW + pad, y: CGFloat(row) * cellH + pad, pixel: pixel)
        }
    }
    NSGraphicsContext.restoreGraphicsState()
    writePNG(rep, path)
}

let arguments = CommandLine.arguments
if arguments.contains("--install-hooks") || arguments.contains("--uninstall-hooks") {
    let error = arguments.contains("--install-hooks") ? Hooks.install() : Hooks.uninstall()
    print(error ?? "ok")
    exit(error == nil ? 0 : 1)
}
if let i = arguments.firstIndex(of: "--preview-skins"), i + 1 < arguments.count {
    renderSkinsPreview(to: arguments[i + 1])
    exit(0)
}
if let i = arguments.firstIndex(of: "--preview"), i + 1 < arguments.count {
    renderSpritePreview(to: arguments[i + 1])
    exit(0)
}
if let i = arguments.firstIndex(of: "--preview-island"), i + 1 < arguments.count {
    _ = NSApplication.shared
    renderIslandPreview(to: arguments[i + 1])
    exit(0)
}
let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
