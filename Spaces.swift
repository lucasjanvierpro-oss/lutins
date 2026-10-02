// Garde l'encoche affichée pendant le glissement d'un bureau à l'autre.
//
// Une fenêtre « sur tous les bureaux » disparaît le temps de l'animation. Les apps d'encoche
// contournent ça en mettant leur fenêtre dans un espace à elles, posé au-dessus des bureaux
// (fonctions privées de SkyLight/CoreGraphics, non documentées mais stables depuis des années).

import Cocoa

private typealias CGSConnectionID = Int32
private typealias CGSSpaceID = UInt64

@_silgen_name("_CGSDefaultConnection")
private func _CGSDefaultConnection() -> CGSConnectionID
@_silgen_name("CGSSpaceCreate")
private func CGSSpaceCreate(_ cid: CGSConnectionID, _ flag: Int, _ options: CFDictionary?) -> CGSSpaceID
@_silgen_name("CGSSpaceSetAbsoluteLevel")
private func CGSSpaceSetAbsoluteLevel(_ cid: CGSConnectionID, _ space: CGSSpaceID, _ level: Int)
@_silgen_name("CGSShowSpaces")
private func CGSShowSpaces(_ cid: CGSConnectionID, _ spaces: CFArray)
@_silgen_name("CGSAddWindowsToSpaces")
private func CGSAddWindowsToSpaces(_ cid: CGSConnectionID, _ windows: CFArray, _ spaces: CFArray)
@_silgen_name("CGSRemoveWindowsFromSpaces")
private func CGSRemoveWindowsFromSpaces(_ cid: CGSConnectionID, _ windows: CFArray, _ spaces: CFArray)

final class StickySpace {
    private let id: CGSSpaceID

    init() {
        let cid = _CGSDefaultConnection()
        // Le drapeau doit valoir 1, sinon le Finder dessine les icônes du bureau dans cet espace.
        id = CGSSpaceCreate(cid, 1, nil)
        CGSSpaceSetAbsoluteLevel(cid, id, 2_147_483_647)
        CGSShowSpaces(cid, [id] as CFArray)
    }

    func add(_ window: NSWindow) {
        CGSAddWindowsToSpaces(_CGSDefaultConnection(), [window.windowNumber] as CFArray, [id] as CFArray)
    }

    func remove(_ window: NSWindow) {
        CGSRemoveWindowsFromSpaces(_CGSDefaultConnection(), [window.windowNumber] as CFArray, [id] as CFArray)
    }
}
