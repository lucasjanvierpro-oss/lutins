// Les persos au choix. Chaque créature est un dessin de 12×10 pixels avec sa propre palette ;
// comme ses couleurs ne changent pas, l'état se lit au halo sous ses pieds (bleu / orange / vert),
// au halo qui l'entoure (bleu qui pulse au travail, vert quand c'est fini) et aux petits signes
// à côté de sa tête (« ! », étincelle, « z »).

import Cocoa

private func rgb(_ hex: UInt32) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
}

struct Creature {
    let id: String
    let name: String
    let palette: [Character: NSColor]
    /// 10 lignes de 12 caractères ; « . » = transparent. La dernière ligne, ce sont les pieds.
    let art: [String]
    /// Pieds pour l'autre pas de la marche.
    let walk: String
    /// Pixels des yeux (colonne, ligne) dans `art`, et couleur de paupière pour les fermer
    /// (le trait des yeux fermés prend la couleur « K », sinon celle des yeux « E »).
    let eyes: [(Int, Int)]
    let lid: Character
    var floats = false

    func draw(_ look: LutinLook, frame: Int, x: CGFloat, y: CGFloat, pixel: CGFloat, alpha: CGFloat, sleepyZ: Bool,
              decorated: Bool = true, effects: Bool = true) {
        func dot(_ c: Int, _ r: Int, _ color: NSColor) {
            color.withAlphaComponent(color.alphaComponent * alpha).setFill()
            NSRect(x: x + CGFloat(c) * pixel, y: y + CGFloat(r) * pixel, width: pixel, height: pixel).fill()
        }

        var dx = 0, dy = 0, stepping = false
        switch look.state {
        case .working:
            dy = Effects.hop[frame % Effects.hop.count]
            stepping = dy == 0 && (frame / Effects.hop.count) % 2 == 1
        case .waiting:
            dx = [0, 1, 0, -1][frame % 4]
        case .done:
            if look.age < 3 { dy = Effects.bigJump[frame % Effects.bigJump.count] }
        case .idle:
            if floats { dy = (frame / 8) % 2 == 0 ? 0 : -1 }
        }

        let ox = dx, oy = 2 + dy
        var shape = Set<Px>()
        for (r, line) in art.enumerated() {
            let row = stepping && r == art.count - 1 ? walk : line
            for (c, ch) in row.enumerated() where ch != "." { shape.insert(Px(c: ox + c, r: oy + r)) }
        }
        if decorated && effects { Effects.glow(around: shape, state: look.state, frame: frame, dot: dot) }

        // Halo au sol : la couleur de l'état.
        if decorated && look.state != .idle {
            let glow = Sprite.color(look.state)
            for c in 1...10 { dot(c, 12, glow.withAlphaComponent(c == 1 || c == 10 ? 0.45 : 0.95)) }
        }

        for (r, line) in art.enumerated() {
            let row = stepping && r == art.count - 1 ? walk : line
            for (c, ch) in row.enumerated() where ch != "." {
                if let color = palette[ch] { dot(ox + c, oy + r, color) }
            }
        }

        // Yeux fermés quand il dort, et un clignement de temps en temps.
        let blink = look.state != .idle && frame % 37 == 36
        if look.state == .idle || blink, let lidColor = palette[lid], let ink = palette["K"] ?? palette["E"] {
            let center = 5.5
            for side in [eyes.filter { Double($0.0) < center }, eyes.filter { Double($0.0) >= center }] where !side.isEmpty {
                for (c, r) in side { dot(ox + c, oy + r, lidColor) }
                let bottom = side.map(\.1).max()!
                let cols = side.map(\.0)
                let outward = Double(cols[0]) < center ? cols.min()! - 1 : cols.max()! + 1
                for c in Set(cols + [outward]) { dot(ox + c, oy + bottom, ink) }
            }
        }

        guard decorated else { return }
        let color = Sprite.color(look.state)
        switch look.state {
        case .waiting where (frame / 3) % 2 == 0:
            for r in [1, 2, 3, 5] { dot(12, r, color) }
        case .done where (frame / 4) % 2 == 0 || look.age >= 3:
            for (c, r) in [(12, 0), (11, 1), (12, 1), (13, 1), (12, 2)] { dot(c, r, color) }
        case .idle where sleepyZ && (frame / 8) % 2 == 0:
            for (c, r) in [(11, 0), (12, 0), (13, 0), (12, 1), (11, 2), (11, 3), (12, 3), (13, 3)] { dot(c, r, rgb(0x9AA0A6)) }
        default:
            break
        }
        if effects && look.state == .done { Effects.confetti(age: look.age, origin: Px(c: 6, r: 2), dot: dot) }
    }
}

enum Skins {
    static let cols = 14, rows = 13
    static let mix = "mix"

    static func size(_ pixel: CGFloat) -> NSSize {
        NSSize(width: CGFloat(cols) * pixel, height: CGFloat(rows) * pixel)
    }

    static let creatures: [Creature] = [
        Creature(
            id: "panda-roux", name: "Panda roux",
            palette: ["O": rgb(0xD9622B), "o": rgb(0x9E3F17), "W": rgb(0xF6EDE1), "D": rgb(0x3A1D10),
                      "E": rgb(0x140904), "R": rgb(0xF0A060)],
            art: [
                ".OO....OO...",
                "ODOOOOOODO..",
                "OOWOOOOWOO..",
                "OOEOOOOEOO..",
                "OOoWEEWoOO..",
                ".OOOWWOOO..R",
                "..DOOOOD..oR",
                "..DDDDDD.oR.",
                "..DDDDDDRo..",
                "..DD..DD....",
            ],
            walk: ".DD....DD...", eyes: [(2, 3), (7, 3)], lid: "O"),
        Creature(
            id: "koala", name: "Koala",
            palette: ["G": rgb(0x9AA3AD), "g": rgb(0x6F7882), "W": rgb(0xEEF0F2), "N": rgb(0x2B2D33),
                      "E": rgb(0x15161A), "L": rgb(0x6BCB77)],
            art: [
                ".gg....gg...",
                "gWWg..gWWg..",
                "gWGGGGGGWg..",
                ".GEGGGGEG...",
                ".GGGNNGGG...",
                ".GGNNNNGG...",
                "..GGNNGG.L..",
                "..GWWWWGL...",
                "..GWWWWG....",
                "..gg..gg....",
            ],
            walk: ".gg....gg...", eyes: [(2, 3), (7, 3)], lid: "G"),
        Creature(
            id: "kitsune", name: "Renard magique",
            palette: ["F": rgb(0xF28A30), "W": rgb(0xFFF3E2), "E": rgb(0x1C0F07), "B": rgb(0x4A2A18), "G": rgb(0x7FDBFF)],
            art: [
                ".F....F.....",
                "FFF..FFF....",
                "FFFFFFFF....",
                "FEFFFFEF...G",
                "WWFFFFWW..GG",
                ".WWEEWW...WG",
                "..FFFF...FF.",
                ".FWWWWF.FF..",
                ".FWWWWFFF...",
                ".BB..BB.....",
            ],
            walk: "BB....BB....", eyes: [(1, 3), (6, 3)], lid: "F"),
        Creature(
            id: "dragon", name: "Petit dragon",
            palette: ["G": rgb(0x45C486), "g": rgb(0x2B8C5C), "Y": rgb(0xF7DC79), "H": rgb(0xF4E6C0), "E": rgb(0x0F1A14)],
            art: [
                "..H....H....",
                "..HGGGGH....",
                "..GGGGGG....",
                "..GEGGEG....",
                "g.GGggGG.g..",
                "gg.GYYG.gg..",
                "gggGYYGggg..",
                ".ggGYYGgg.Gg",
                "...GGGGGGG..",
                "...GG..GG...",
            ],
            walk: "..GG....GG..", eyes: [(3, 3), (6, 3)], lid: "G"),
        Creature(
            id: "fantome", name: "Fantôme",
            palette: ["W": rgb(0xEEF1FF), "E": rgb(0x1E2340), "P": rgb(0xFF9EC7)],
            art: [
                "....WWWW....",
                "...WWWWWW...",
                "..WWWWWWWW..",
                "..WEWWWWEW..",
                "..WEWWWWEW..",
                ".WWPWWWWPWW.",
                "..WWWWWWWW..",
                "..WWWWWWWW..",
                "..WWWWWWWW..",
                "..WW.WW.WW..",
            ],
            walk: "..W.WW.WW.W.", eyes: [(3, 3), (3, 4), (8, 3), (8, 4)], lid: "W", floats: true),
        Creature(
            id: "chat-sorcier", name: "Chat sorcier",
            palette: ["H": rgb(0x8B5CF6), "S": rgb(0xFFE066), "C": rgb(0x6B6F9E), "E": rgb(0xFFE066), "N": rgb(0xF49AC1), "K": rgb(0x2A2540)],
            art: [
                ".....HH.....",
                "....HHHS....",
                "...HHHHH....",
                "HHHHHHHHHH..",
                ".CCCCCCCC...",
                ".CECCCCEC...",
                ".CCCNNCCC.C.",
                "..CCCCCC..C.",
                "..CCCCCCCC..",
                "..CC..CC....",
            ],
            walk: ".CC....CC...", eyes: [(2, 5), (7, 5)], lid: "C"),
        Creature(
            id: "axolotl", name: "Axolotl",
            palette: ["P": rgb(0xFFA3CF), "p": rgb(0xF25C9A), "E": rgb(0x2A0F1E), "M": rgb(0xC23A78)],
            art: [
                "p.p......p.p",
                ".pPPPPPPPPp.",
                "pPPPPPPPPPPp",
                ".PEPPPPPPEP.",
                "pPPPPPPPPPPp",
                ".PPPMPPMPPP.",
                "..PPPMMPPP..",
                "...PPPPPP...",
                "...PPPPPP...",
                "...PP..PP...",
            ],
            walk: "..PP....PP..", eyes: [(2, 3), (9, 3)], lid: "P"),
        Creature(
            id: "champignon", name: "Champignon",
            palette: ["R": rgb(0xE5484D), "W": rgb(0xFFF5F5), "S": rgb(0xF5E6C8), "E": rgb(0x2A1A10), "P": rgb(0xFFB0A8)],
            art: [
                "....RRRR....",
                "..RRWRRRRR..",
                ".RRWWRRRWRR.",
                "RRRRRRRWWRRR",
                "RRWRRRRRRRRR",
                ".RRRRRRRRRR.",
                "...SSSSSS...",
                "...SESSES...",
                "...PSSSSP...",
                "...SS..SS...",
            ],
            walk: "..SS....SS..", eyes: [(4, 7), (7, 7)], lid: "S"),
        Creature(
            id: "chouette", name: "Chouette",
            palette: ["B": rgb(0xA0703F), "b": rgb(0x6B4423), "Y": rgb(0xF7D046), "E": rgb(0x1A120A),
                      "O": rgb(0xF59A2F), "C": rgb(0xEBD5AA), "c": rgb(0xC9A877)],
            art: [
                "..b......b..",
                "..BBBBBBBB..",
                ".BYYBBBBYYB.",
                ".BYEBBBBEYB.",
                ".BBBBOOBBBB.",
                "bBCCCCCCCCBb",
                "bBCcCCCCcCBb",
                ".BCCCCCCCCB.",
                "..BBBBBBBB..",
                "...OO..OO...",
            ],
            walk: "..OO....OO..", eyes: [(2, 2), (3, 2), (2, 3), (3, 3), (8, 2), (9, 2), (8, 3), (9, 3)], lid: "B"),
    ]

    /// Tous les choix possibles, dans l'ordre du menu.
    static let ids = ["lutin"] + creatures.map(\.id)

    static func name(_ id: String) -> String {
        id == "lutin" ? "Lutin (couleur = état)" : creatures.first { $0.id == id }?.name ?? id
    }

    /// Le perso d'une session : le choix fait, ou en mode mélange un différent par session.
    static func skin(for session: Session, choice: String) -> String {
        choice == mix ? ids[(session.order - 1) % ids.count] : choice
    }

    static func draw(_ id: String, _ look: LutinLook, frame: Int, x: CGFloat, y: CGFloat, pixel: CGFloat,
                     alpha: CGFloat = 1, sleepyZ: Bool = true, decorated: Bool = true, effects: Bool = true) {
        if let creature = creatures.first(where: { $0.id == id }) {
            creature.draw(look, frame: frame, x: x, y: y, pixel: pixel, alpha: alpha, sleepyZ: sleepyZ,
                          decorated: decorated, effects: effects)
        } else {
            Sprite.draw(look, frame: frame, x: x + pixel * 2, y: y + pixel, pixel: pixel, alpha: alpha,
                        sleepyZ: sleepyZ, effects: effects && decorated)
        }
    }

    /// Petite image d'un perso pour le menu.
    static func icon(_ id: String, pixel: CGFloat = 2) -> NSImage {
        NSImage(size: size(pixel), flipped: true) { _ in
            NSGraphicsContext.current?.shouldAntialias = false
            draw(id, LutinLook(state: .working, age: 99), frame: 0, x: 0, y: 0, pixel: pixel, sleepyZ: false, decorated: false)
            return true
        }
    }
}
