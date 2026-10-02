// Effets autour des persos : halo bleu qui pulse quand il travaille, halo vert léger et confettis quand il a fini.
// Tout se compte en images d'animation (une toutes les 0,12 s) dans le repère pixel du perso.

import Cocoa

struct Px: Hashable {
    let c: Int
    let r: Int
}

enum Effects {
    static let frameDuration = 0.12
    /// Petits sauts pendant le travail, gros saut quand c'est fini.
    static let hop = [0, -1, -2, -2, -1, 0]
    static let bigJump = [0, -2, -3, -3, -2, 0]

    static let blueGlow = NSColor(srgbRed: 0.5, green: 0.76, blue: 1, alpha: 1)
    static let greenGlow = NSColor(srgbRed: 0.42, green: 0.95, blue: 0.6, alpha: 1)

    /// Entre 0 et 1, en boucle sur `period` images.
    static func pulse(_ frame: Int, period: Double) -> CGFloat {
        CGFloat(0.5 + 0.5 * sin(Double(frame) * 2 * .pi / period))
    }

    /// Le halo qui correspond à l'état : bleu qui pulse au travail, vert léger quand c'est fini.
    static func glow(around shape: Set<Px>, state: LutinState, frame: Int, dot: (Int, Int, NSColor) -> Void) {
        switch state {
        case .working: aura(shape, color: blueGlow, strength: 0.1 + 0.9 * pulse(frame, period: 8), dot: dot)
        case .done: aura(shape, color: greenGlow, strength: 0.35, dot: dot)
        default: break
        }
    }

    /// Halo en pixels autour de la silhouette : un anneau franc, puis un anneau léger.
    static func aura(_ shape: Set<Px>, color: NSColor, strength: CGFloat, dot: (Int, Int, NSColor) -> Void) {
        func around(_ set: Set<Px>, diagonal: Bool) -> Set<Px> {
            let steps = diagonal
                ? [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)]
                : [(0, -1), (-1, 0), (1, 0), (0, 1)]
            var out = Set<Px>()
            for p in set { for (dc, dr) in steps { out.insert(Px(c: p.c + dc, r: p.r + dr)) } }
            return out.subtracting(set)
        }
        let inner = around(shape, diagonal: true)
        let outer = around(shape.union(inner), diagonal: false)
        for p in outer { dot(p.c, p.r, color.withAlphaComponent(0.3 * strength)) }
        for p in inner { dot(p.c, p.r, color.withAlphaComponent(0.9 * strength)) }
    }

    /// Gerbe de confettis pendant les 2 premières secondes après la fin, partant de `origin`.
    static func confetti(age: TimeInterval, origin: Px, dot: (Int, Int, NSColor) -> Void) {
        let k = age / frameDuration
        guard k < 16 else { return }
        let colors = [NSColor.systemGreen, .systemYellow, .systemPink, .systemTeal, .white]
        for i in 0..<18 {
            let angle = Double(i) / 18 * 2 * .pi
            let speed = 0.4 + Double((i * 37) % 10) / 20
            let x = Double(origin.c) + cos(angle) * speed * k
            let y = Double(origin.r) - (abs(sin(angle)) * speed + 0.3) * k + 0.06 * k * k
            dot(Int(x.rounded()), Int(y.rounded()), colors[i % colors.count])
        }
    }
}
