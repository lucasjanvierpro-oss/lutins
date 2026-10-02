// L'encoche : une pastille noire en haut au centre de chaque écran, un perso par session.
// Au survol elle s'agrandit et liste les sessions. Clic sur un perso = ouvrir l'app, clic droit = réglages.

import Cocoa

enum IslandSize: String, CaseIterable {
    case small, medium, large

    var pixel: CGFloat {
        switch self {
        case .small: return 2
        case .medium: return 3
        case .large: return 4
        }
    }

    var label: String {
        switch self {
        case .small: return "Petite"
        case .medium: return "Moyenne"
        case .large: return "Grande"
        }
    }
}

/// Géométrie d'une encoche. `width`/`height` sont la taille réelle (qui change pendant l'animation).
struct IslandLayout {
    var pixel: CGFloat = 3
    var count = 0
    var notch = NSSize.zero
    var expanded = false
    var attached = true
    var width: CGFloat = 0
    var height: CGFloat = 0

    static let ear: CGFloat = 10
    static let rowHeight: CGFloat = 42
    static let footerHeight: CGFloat = 30
    static let listWidth: CGFloat = 340

    var sprite: NSSize { Skins.size(pixel) }
    var gap: CGFloat { pixel }
    var slots: Int { max(count, 1) }
    var rowWidth: CGFloat {
        CGFloat(slots) * sprite.width + CGFloat(slots - 1) * gap + (notch.width > 0 ? notch.width + gap : 0)
    }
    var collapsedHeight: CGFloat { max(pixel + sprite.height + pixel * 1.5, notch.height) }
    var listTop: CGFloat { collapsedHeight + 6 }

    var preferredSize: NSSize {
        var body = rowWidth + pixel * 8
        var h = collapsedHeight
        if expanded {
            body = max(body, Self.listWidth)
            h = listTop + CGFloat(max(count, 1)) * Self.rowHeight + Self.footerHeight + 6
        }
        return NSSize(width: ceil(body + 2 * Self.ear), height: ceil(h))
    }

    func spriteRect(_ i: Int) -> NSRect {
        var x = (width - rowWidth) / 2 + CGFloat(i) * (sprite.width + gap)
        if notch.width > 0 && i >= (slots + 1) / 2 { x += notch.width + gap }
        return NSRect(x: round(x), y: pixel, width: sprite.width, height: sprite.height)
    }

    func rowRect(_ i: Int) -> NSRect {
        NSRect(x: Self.ear + 8, y: listTop + CGFloat(i) * Self.rowHeight, width: width - 2 * Self.ear - 16, height: Self.rowHeight)
    }

    var footerRect: NSRect {
        NSRect(x: Self.ear + 8, y: listTop + CGFloat(max(count, 1)) * Self.rowHeight, width: width - 2 * Self.ear - 16, height: Self.footerHeight)
    }

    var optionsRect: NSRect { NSRect(x: footerRect.maxX - 30, y: footerRect.midY - 11, width: 30, height: 22) }
    var gripRect: NSRect { optionsRect.offsetBy(dx: -34, dy: 0) }

    func sprite(at p: NSPoint) -> Int? {
        (0..<count).first { spriteRect($0).insetBy(dx: -gap / 2, dy: 0).contains(p) }
    }

    func row(at p: NSPoint) -> Int? {
        guard expanded else { return nil }
        return (0..<count).first { rowRect($0).contains(p) }
    }

    /// Collée en haut : bord du haut contre l'écran, petites « oreilles » concaves, coins arrondis en bas.
    /// Posée librement : une pastille arrondie partout.
    func shape() -> NSBezierPath {
        let w = width, h = height, e = Self.ear
        guard attached else {
            let r = min(expanded ? 18 : 14, h / 2)
            return NSBezierPath(roundedRect: NSRect(x: e, y: 0, width: w - 2 * e, height: h), xRadius: r, yRadius: r)
        }
        let p = attachedEdge()
        p.close()
        return p
    }

    /// Le contour sans le bord du haut (collé à l'écran), pour l'illuminer.
    func outline() -> NSBezierPath { attached ? attachedEdge() : shape() }

    private func attachedEdge() -> NSBezierPath {
        let w = width, h = height, e = Self.ear
        let r = min(expanded ? 18 : 14, (h - e) / 2 + 4)
        let p = NSBezierPath()
        p.move(to: NSPoint(x: 0, y: 0))
        p.curve(to: NSPoint(x: e, y: e), controlPoint1: NSPoint(x: e * 0.6, y: 0), controlPoint2: NSPoint(x: e, y: e * 0.4))
        p.line(to: NSPoint(x: e, y: h - r))
        p.curve(to: NSPoint(x: e + r, y: h), controlPoint1: NSPoint(x: e, y: h - r * 0.45), controlPoint2: NSPoint(x: e + r * 0.45, y: h))
        p.line(to: NSPoint(x: w - e - r, y: h))
        p.curve(to: NSPoint(x: w - e, y: h - r), controlPoint1: NSPoint(x: w - e - r * 0.45, y: h), controlPoint2: NSPoint(x: w - e, y: h - r * 0.45))
        p.line(to: NSPoint(x: w - e, y: e))
        p.curve(to: NSPoint(x: w, y: 0), controlPoint1: NSPoint(x: w - e, y: e * 0.4), controlPoint2: NSPoint(x: w - e * 0.6, y: 0))
        return p
    }
}

/// Où se trouve l'encoche sur son écran : collée en haut (`x` = centre, en fraction de la largeur)
/// ou posée librement (`x` = centre et `y` = distance du haut de l'écran, en points).
struct Placement {
    var free = false
    var x: CGFloat = 0.5
    var y: CGFloat = 0

    static func key(_ screen: NSScreen) -> String { "placement." + screen.localizedName }

    static func load(_ screen: NSScreen) -> Placement {
        guard let d = UserDefaults.standard.dictionary(forKey: key(screen)) else { return Placement() }
        return Placement(free: d["free"] as? Bool ?? false,
                         x: (d["x"] as? Double).map { CGFloat($0) } ?? 0.5,
                         y: (d["y"] as? Double).map { CGFloat($0) } ?? 0)
    }

    func save(_ screen: NSScreen) {
        UserDefaults.standard.set(["free": free, "x": Double(x), "y": Double(y)], forKey: Self.key(screen))
    }
}

final class IslandPanel: NSPanel {
    init() {
        super.init(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        isMovable = false
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        acceptsMouseMovedEvents = true
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
}

final class IslandView: NSView {
    private unowned let island: Island
    let screen: NSScreen
    var layout = IslandLayout()
    var placement: Placement
    var hoveredRow: Int?
    var hoveredOptions = false
    var hoveredGrip = false
    private var dragStart: (mouse: NSPoint, origin: NSPoint)?
    private var dragging = false

    init(island: Island, screen: NSScreen) {
        self.island = island
        self.screen = screen
        placement = Placement.load(screen)
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    /// La géométrie à la taille actuelle de la vue.
    var current: IslandLayout {
        var l = layout
        l.width = bounds.width
        l.height = bounds.height
        return l
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseEntered(with event: NSEvent) { island.hover(self, inside: true) }

    override func mouseExited(with event: NSEvent) {
        hoveredRow = nil
        hoveredOptions = false
        if hoveredGrip { NSCursor.arrow.set() }
        hoveredGrip = false
        island.hover(self, inside: false)
    }

    override func mouseMoved(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        let l = current
        let row = l.row(at: p)
        let options = l.expanded && l.optionsRect.contains(p)
        let grip = l.expanded && l.gripRect.contains(p)
        if grip != hoveredGrip { (grip ? NSCursor.openHand : NSCursor.arrow).set() }
        if row != hoveredRow || options != hoveredOptions || grip != hoveredGrip {
            hoveredRow = row
            hoveredOptions = options
            hoveredGrip = grip
            needsDisplay = true
        }
    }

    // Un clic ouvre la session ; un glisser (depuis la poignée ou n'importe où) déplace l'encoche.
    override func mouseDown(with event: NSEvent) {
        guard let window else { return }
        dragStart = (NSEvent.mouseLocation, window.frame.origin)
        dragging = false
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = dragStart else { return }
        let mouse = NSEvent.mouseLocation
        let dx = mouse.x - start.mouse.x, dy = mouse.y - start.mouse.y
        if !dragging && hypot(dx, dy) < 4 { return }
        if !dragging { NSCursor.closedHand.set() }
        dragging = true
        island.drag(self, to: NSPoint(x: start.origin.x + dx, y: start.origin.y + dy))
    }

    override func mouseUp(with event: NSEvent) {
        defer {
            dragStart = nil
            dragging = false
        }
        if dragging {
            (hoveredGrip ? NSCursor.openHand : NSCursor.arrow).set()
            island.endDrag(self)
        } else {
            island.click(self, at: convert(event.locationInWindow, from: nil))
        }
    }

    override func rightMouseDown(with event: NSEvent) { island.showOptions(self, at: convert(event.locationInWindow, from: nil)) }

    override func draw(_ dirtyRect: NSRect) { island.draw(self) }
}

final class Island {
    private let store: Store
    private var panels: [IslandPanel] = []
    private weak var expanded: IslandView?
    private weak var dragged: IslandView?
    private lazy var sticky = StickySpace()
    private var hidden = false
    private var expandTimer: Timer?
    private var collapseTimer: Timer?
    private(set) var frame = 0
    var problem: String?
    var optionsMenu: () -> NSMenu = { NSMenu() }
    var onOpen: (Session) -> Void = { _ in }

    init(store: Store) {
        self.store = store
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in self?.rebuild() }
        // Rien à l'écran de verrouillage ni par-dessus l'économiseur d'écran.
        let distributed = DistributedNotificationCenter.default()
        for (name, hide) in [("com.apple.screenIsLocked", true), ("com.apple.screenIsUnlocked", false),
                             ("com.apple.screensaver.didstart", true), ("com.apple.screensaver.didstop", false)] {
            distributed.addObserver(forName: NSNotification.Name(name), object: nil, queue: .main) { [weak self] _ in
                self?.setHidden(hide)
            }
        }
    }

    private func setHidden(_ hide: Bool) {
        hidden = hide
        for panel in panels {
            panel.alphaValue = hide ? 0 : 1
            panel.ignoresMouseEvents = hide
        }
    }

    var sizeOverride: IslandSize?
    var skinOverride: String?
    var skinChoice: String {
        let choice = skinOverride ?? UserDefaults.standard.string(forKey: "skin") ?? "lutin"
        return choice == Skins.mix || Skins.ids.contains(choice) ? choice : "lutin"
    }
    var size: IslandSize { sizeOverride ?? IslandSize(rawValue: UserDefaults.standard.string(forKey: "size") ?? "") ?? .medium }

    /// Une encoche par écran choisi (tous par défaut).
    func rebuild() {
        for panel in panels {
            sticky.remove(panel)
            panel.orderOut(nil)
        }
        let choice = UserDefaults.standard.string(forKey: "screen") ?? "all"
        let picked = NSScreen.screens.filter { $0.localizedName == choice }
        panels = (picked.isEmpty ? NSScreen.screens : picked).map { screen in
            let panel = IslandPanel()
            panel.contentView = IslandView(island: self, screen: screen)
            return panel
        }
        expanded = nil
        refresh(animated: false)
        for panel in panels {
            sticky.add(panel)
            panel.orderFrontRegardless()
        }
        setHidden(hidden)
    }

    func refresh(animated: Bool = true) {
        for panel in panels {
            guard let view = panel.contentView as? IslandView else { continue }
            view.layout = layout(for: view, expanded: expanded === view)
            panel.hasShadow = view.placement.free
            let frame = self.frame(for: view, size: view.layout.preferredSize)
            if view !== dragged && frame != panel.frame {
                if animated {
                    NSAnimationContext.runAnimationGroup { context in
                        context.duration = 0.18
                        context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                        panel.animator().setFrame(frame, display: true)
                    }
                } else {
                    panel.setFrame(frame, display: true)
                }
            }
            view.needsDisplay = true
        }
    }

    private func layout(for view: IslandView, expanded: Bool) -> IslandLayout {
        var l = IslandLayout()
        l.pixel = size.pixel
        l.count = store.ordered.count
        l.notch = notch(of: view.screen)
        l.expanded = expanded
        l.attached = !view.placement.free
        return l
    }

    private func frame(for view: IslandView, size: NSSize) -> NSRect {
        let s = view.screen.frame
        let p = view.placement
        let centerX = p.free ? s.minX + p.x : s.minX + p.x * s.width
        let top = p.free ? s.maxY - p.y : s.maxY
        let x = min(max(centerX - size.width / 2, s.minX), s.maxX - size.width)
        let y = max(top - size.height, s.minY)
        return NSRect(x: round(x), y: round(y), width: size.width, height: size.height)
    }

    // MARK: Déplacement

    func drag(_ view: IslandView, to origin: NSPoint) {
        guard let panel = view.window else { return }
        dragged = view
        let s = view.screen.frame
        var f = panel.frame
        f.origin.x = min(max(origin.x, s.minX), s.maxX - f.width)
        f.origin.y = min(max(origin.y, s.minY), s.maxY - f.height)
        // Près du haut : elle se recolle (et s'aimante au centre). Ailleurs : posée librement.
        let attached = s.maxY - f.maxY < 40
        if attached {
            f.origin.y = s.maxY - f.height
            if abs(f.midX - s.midX) < 24 { f.origin.x = round(s.midX - f.width / 2) }
            view.placement = Placement(free: false, x: (f.midX - s.minX) / s.width, y: 0)
        } else {
            view.placement = Placement(free: true, x: f.midX - s.minX, y: s.maxY - f.maxY)
        }
        view.layout.attached = attached
        panel.hasShadow = !attached
        panel.setFrame(f, display: true)
        view.needsDisplay = true
    }

    func endDrag(_ view: IslandView) {
        dragged = nil
        view.placement.save(view.screen)
        refresh()
    }

    func resetPlacements() {
        for screen in NSScreen.screens { UserDefaults.standard.removeObject(forKey: Placement.key(screen)) }
        for case let view as IslandView in panels.map(\.contentView) { view.placement = Placement() }
        refresh()
    }

    private func notch(of screen: NSScreen) -> NSSize {
        guard screen.safeAreaInsets.top > 0,
              let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea else { return .zero }
        return NSSize(width: screen.frame.width - left.width - right.width, height: screen.safeAreaInsets.top)
    }

    func tick() {
        frame += 1
        let animating = !store.ordered.isEmpty
        for panel in panels {
            guard let view = panel.contentView as? IslandView else { continue }
            if animating || view === expanded { view.needsDisplay = true }
        }
    }

    // MARK: Souris

    func hover(_ view: IslandView, inside: Bool) {
        if inside {
            collapseTimer?.invalidate()
            collapseTimer = nil
            guard expanded !== view else { return }
            expandTimer?.invalidate()
            expandTimer = Timer.scheduledTimer(withTimeInterval: 0.35, repeats: false) { [weak self, weak view] _ in
                guard let self, let view else { return }
                self.expanded = view
                self.refresh()
            }
        } else {
            expandTimer?.invalidate()
            expandTimer = nil
            collapseTimer?.invalidate()
            collapseTimer = Timer.scheduledTimer(withTimeInterval: 0.3, repeats: false) { [weak self] _ in
                guard let self else { return }
                self.expanded = nil
                self.refresh()
            }
        }
    }

    func click(_ view: IslandView, at p: NSPoint) {
        let l = view.current
        if l.expanded && l.optionsRect.contains(p) {
            showOptions(view, at: NSPoint(x: l.optionsRect.minX, y: l.optionsRect.maxY + 4))
            return
        }
        let sessions = store.ordered
        if let i = l.row(at: p) ?? l.sprite(at: p), i < sessions.count { onOpen(sessions[i]) }
    }

    /// Le menu s'ouvre sous l'encoche, pour ne pas passer dessous.
    func showOptions(_ view: IslandView, at p: NSPoint) {
        optionsMenu().popUp(positioning: nil, at: NSPoint(x: p.x, y: view.bounds.maxY + 2), in: view)
    }

    // MARK: Dessin

    func draw(_ view: IslandView) {
        let l = view.current
        let context = NSGraphicsContext.current
        context?.shouldAntialias = true
        NSColor.black.setFill()
        l.shape().fill()

        let sessions = store.ordered
        // Une session finie et pas encore vue : tout le contour de l'encoche pulse en vert.
        if sessions.contains(where: { $0.state == .done }) {
            let strength = 0.45 + 0.55 * Effects.pulse(frame, period: 9)
            NSGraphicsContext.saveGraphicsState()
            l.shape().addClip()
            let edge = l.outline()
            edge.lineWidth = 14
            Effects.greenGlow.withAlphaComponent(0.25 * strength).setStroke()
            edge.stroke()
            edge.lineWidth = 5
            Effects.greenGlow.withAlphaComponent(strength).setStroke()
            edge.stroke()
            NSGraphicsContext.restoreGraphicsState()
        }
        let now = Date()
        context?.shouldAntialias = false
        if sessions.isEmpty {
            let r = l.spriteRect(0)
            let look = LutinLook(state: problem == nil ? .idle : .waiting, age: 99)
            let skin = skinChoice == Skins.mix ? "lutin" : skinChoice
            Skins.draw(skin, look, frame: 0, x: r.minX, y: r.minY, pixel: l.pixel, alpha: problem == nil ? 0.45 : 1,
                       sleepyZ: false, effects: false)
        }
        for (i, s) in sessions.enumerated() {
            let r = l.spriteRect(i)
            Skins.draw(Skins.skin(for: s, choice: skinChoice), LutinLook(state: s.state, age: now.timeIntervalSince(s.stateSince)),
                       frame: frame, x: r.minX, y: r.minY, pixel: l.pixel)
        }

        guard l.expanded, l.height > l.collapsedHeight + 8 else { return }
        context?.shouldAntialias = true
        NSColor(white: 1, alpha: 0.12).setFill()
        NSRect(x: IslandLayout.ear + 14, y: l.collapsedHeight + 2, width: l.width - 2 * IslandLayout.ear - 28, height: 1).fill()

        if sessions.isEmpty {
            let r = l.rowRect(0)
            text("Un perso apparaît dès que tu écris à Claude Code.", NSRect(x: r.minX + 8, y: r.midY - 8, width: r.width - 16, height: 18), size: 12, alpha: 0.6)
        }
        let mini: CGFloat = 2
        let miniSize = Skins.size(mini)
        for (i, s) in sessions.enumerated() {
            let r = l.rowRect(i)
            if view.hoveredRow == i {
                NSColor(white: 1, alpha: 0.09).setFill()
                NSBezierPath(roundedRect: r, xRadius: 8, yRadius: 8).fill()
            }
            context?.shouldAntialias = false
            Skins.draw(Skins.skin(for: s, choice: skinChoice), LutinLook(state: s.state, age: 99), frame: frame,
                       x: round(r.minX + 4), y: round(r.midY - miniSize.height / 2), pixel: mini, sleepyZ: false, effects: false)
            context?.shouldAntialias = true
            let x = r.minX + 6 + miniSize.width + 10
            text(s.name, NSRect(x: x, y: r.minY + 5, width: r.maxX - x - 6, height: 18), size: 13, weight: .semibold)
            text(Texts.status(s), NSRect(x: x, y: r.minY + 23, width: r.maxX - x - 6, height: 15), size: 11, alpha: 0.55)
        }

        let f = l.footerRect
        let footer = problem.map { "⚠️ " + $0 } ?? Texts.summary(sessions)
        text(footer, NSRect(x: f.minX + 6, y: f.midY - 7, width: f.width - 80, height: 15), size: 11, alpha: 0.5)
        let g = l.gripRect
        if view.hoveredGrip {
            NSColor(white: 1, alpha: 0.14).setFill()
            NSBezierPath(roundedRect: g, xRadius: 6, yRadius: 6).fill()
        }
        NSColor(white: 1, alpha: 0.75).setFill()
        for col in 0..<2 {
            for row in 0..<3 {
                NSBezierPath(ovalIn: NSRect(x: g.midX - 4 + CGFloat(col) * 5.5, y: g.midY - 6.5 + CGFloat(row) * 5, width: 2.6, height: 2.6)).fill()
            }
        }
        let o = l.optionsRect
        if view.hoveredOptions {
            NSColor(white: 1, alpha: 0.14).setFill()
            NSBezierPath(roundedRect: o, xRadius: 6, yRadius: 6).fill()
        }
        text("⋯", NSRect(x: o.minX, y: o.minY + 1, width: o.width, height: o.height), size: 15, weight: .bold, alpha: 0.8, align: .center)
    }

    private func text(_ string: String, _ rect: NSRect, size: CGFloat, weight: NSFont.Weight = .regular,
                      alpha: CGFloat = 1, align: NSTextAlignment = .left) {
        let style = NSMutableParagraphStyle()
        style.lineBreakMode = .byTruncatingTail
        style.alignment = align
        (string as NSString).draw(with: rect, options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: [
            .font: NSFont.systemFont(ofSize: size, weight: weight),
            .foregroundColor: NSColor(white: 1, alpha: alpha),
            .paragraphStyle: style,
        ])
    }

    /// Rendu hors écran, pour l'aperçu (`Lutins --preview-island`).
    func snapshot(expanded: Bool, screen: NSScreen) -> NSBitmapImageRep? {
        let view = IslandView(island: self, screen: screen)
        view.layout = layout(for: view, expanded: expanded)
        view.frame = NSRect(origin: .zero, size: view.layout.preferredSize)
        if expanded { view.hoveredRow = 0 }
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return nil }
        view.cacheDisplay(in: view.bounds, to: rep)
        return rep
    }
}

// MARK: - Textes

enum Texts {
    static func summary(_ sessions: [Session]) -> String {
        if sessions.isEmpty { return "Aucune session Claude en cours" }
        func count(_ state: LutinState) -> Int { sessions.filter { $0.state == state }.count }
        var parts: [String] = []
        if count(.waiting) > 0 { parts.append("\(count(.waiting)) t'attend\(count(.waiting) > 1 ? "ent" : "")") }
        if count(.working) > 0 { parts.append("\(count(.working)) au travail") }
        if count(.done) > 0 { parts.append("\(count(.done)) fini\(count(.done) > 1 ? "s" : "")") }
        if count(.idle) > 0 { parts.append("\(count(.idle)) endormi\(count(.idle) > 1 ? "s" : "")") }
        return parts.joined(separator: " · ")
    }

    static func status(_ s: Session) -> String {
        let now = Date()
        switch s.state {
        case .working:
            return "Travaille · \(tool(s.tool)) · \(duration(now.timeIntervalSince(s.turnStart ?? s.stateSince)))"
        case .waiting:
            return "\(s.waitReason ?? "T'attend") · \(duration(now.timeIntervalSince(s.stateSince)))"
        case .done:
            let ago = now.timeIntervalSince(s.stateSince)
            var line = ago < 10 ? "Fini à l'instant" : "Fini il y a \(duration(ago))"
            if let worked = s.turnDuration, worked >= 5 { line += " · a bossé \(duration(worked))" }
            return line
        case .idle:
            return s.interrupted ? "Interrompu" : "Se repose"
        }
    }

    static func tool(_ tool: String?) -> String {
        switch tool {
        case nil: return "réfléchit"
        case "Bash", "PowerShell", "BashOutput": return "lance une commande"
        case "Write", "Edit", "MultiEdit", "NotebookEdit": return "écrit du code"
        case "Read", "Glob", "Grep", "LS": return "lit des fichiers"
        case "WebSearch", "WebFetch": return "cherche sur le web"
        case "Task", "Agent": return "a lancé un sous-agent"
        case "TodoWrite": return "fait sa liste"
        case let t? where t.hasPrefix("mcp__"): return "utilise un outil"
        default: return "s'active"
        }
    }

    static func duration(_ seconds: TimeInterval) -> String {
        let s = Int(seconds)
        if s < 60 { return "\(s) s" }
        if s < 3600 { return "\(s / 60) min" }
        let m = (s % 3600) / 60
        return m > 0 ? "\(s / 3600) h \(String(format: "%02d", m))" : "\(s / 3600) h"
    }
}
