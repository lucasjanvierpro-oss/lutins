// Dessin des persos en pixel art. Tout est dessiné dans un contexte retourné (y vers le bas).

import Cocoa

struct LutinLook {
    let state: LutinState
    let age: TimeInterval
}

enum Sprite {
    static let cols = 10, rows = 12

    static func size(_ pixel: CGFloat) -> NSSize {
        NSSize(width: CGFloat(cols) * pixel, height: CGFloat(rows) * pixel)
    }

    private static let body = [
        "..####..",
        ".######.",
        "########",
        "########",
        "########",
        "########",
        ".######.",
    ]

    static func color(_ state: LutinState) -> NSColor {
        switch state {
        case .working: return NSColor(srgbRed: 0.30, green: 0.55, blue: 0.97, alpha: 1)
        case .waiting: return NSColor(srgbRed: 1.00, green: 0.48, blue: 0.10, alpha: 1)
        case .done: return NSColor(srgbRed: 0.18, green: 0.80, blue: 0.44, alpha: 1)
        case .idle: return NSColor(srgbRed: 0.60, green: 0.63, blue: 0.66, alpha: 1)
        }
    }

    /// Dessine un lutin, coin haut-gauche en (x, y), chaque « pixel » faisant `pixel` points.
    static func draw(_ look: LutinLook, frame: Int, x: CGFloat, y: CGFloat, pixel: CGFloat,
                     alpha: CGFloat = 1, sleepyZ: Bool = true, effects: Bool = true) {
        let base = color(look.state)
        let dark = base.blended(withFraction: 0.35, of: .black) ?? base
        let light = base.blended(withFraction: 0.55, of: .white) ?? base
        let ink = NSColor(srgbRed: 0.09, green: 0.09, blue: 0.12, alpha: 1)

        func dot(_ c: Int, _ r: Int, _ color: NSColor) {
            color.withAlphaComponent(color.alphaComponent * alpha).setFill()
            NSRect(x: x + CGFloat(c) * pixel, y: y + CGFloat(r) * pixel, width: pixel, height: pixel).fill()
        }

        var dx = 0, dy = 0, tip = 4
        var legs = "..#..#.."
        var eyes = [(2, 3), (2, 4), (5, 3), (5, 4)]
        var mouth: [(Int, Int)] = []
        var bang = false, zzz = false

        switch look.state {
        case .working:
            dy = Effects.hop[frame % Effects.hop.count]
            if dy == 0 && (frame / Effects.hop.count) % 2 == 1 { legs = ".#....#." }
            let glance = [0, -1, 0, 1][(frame / 10) % 4]
            eyes = eyes.map { ($0.0 + glance, $0.1) }
            tip = frame % 2 == 0 ? 3 : 5
        case .waiting:
            eyes = [(2, 2), (2, 3), (5, 2), (5, 3)]
            mouth = [(3, 5), (4, 5)]
            bang = (frame / 3) % 2 == 0
            dx = [0, 1, 0, -1][frame % 4]
        case .done:
            eyes = [(2, 3), (5, 3)]
            mouth = [(2, 4), (3, 5), (4, 5), (5, 4)]
            if look.age < 3 { dy = Effects.bigJump[frame % Effects.bigJump.count] }
        case .idle:
            eyes = [(1, 4), (2, 4), (5, 4), (6, 4)]
            zzz = sleepyZ && (frame / 8) % 2 == 0
        }

        let bx = 1 + dx, by = 4 + dy
        var pixels: [(Int, Int, NSColor)] = []
        for (r, line) in body.enumerated() {
            for (c, ch) in line.enumerated() where ch == "#" { pixels.append((bx + c, by + r, base)) }
        }
        for (c, r) in eyes + mouth { pixels.append((bx + c, by + r, ink)) }
        pixels.append((bx + 4, by - 1, base))
        pixels.append((bx + tip, by - 2, light))
        for (c, ch) in legs.enumerated() where ch == "#" { pixels.append((bx + c, by + 7, dark)) }

        if effects { Effects.glow(around: Set(pixels.map { Px(c: $0.0, r: $0.1) }), state: look.state, frame: frame, dot: dot) }
        for (c, r, color) in pixels { dot(c, r, color) }
        if bang { for r in [0, 1, 2, 4] { dot(9, r, base) } }
        if zzz { for (c, r) in [(7, 0), (8, 0), (9, 0), (8, 1), (7, 2), (7, 3), (8, 3), (9, 3)] { dot(c, r, base) } }
        if effects && look.state == .done { Effects.confetti(age: look.age, origin: Px(c: 5, r: 3), dot: dot) }
    }
}
