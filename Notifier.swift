// Notification macOS quand une session a fini (et seulement à ce moment-là).

import Cocoa
import UserNotifications

final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    var onClick: (String) -> Void = { _ in }
    private(set) var status = "inconnu"

    var enabled: Bool {
        get { UserDefaults.standard.object(forKey: "notify") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "notify") }
    }

    func setUp() {
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        center.requestAuthorization(options: [.alert]) { [weak self] _, _ in self?.refreshStatus() }
    }

    /// Le réglage réel (Réglages Système → Notifications → Lutins), pour le diagnostic.
    func refreshStatus() {
        UNUserNotificationCenter.current().getNotificationSettings { [weak self] settings in
            let names: [UNAuthorizationStatus: String] = [.notDetermined: "pas encore demandé", .denied: "refusé",
                                                          .authorized: "autorisé", .provisional: "provisoire"]
            let status = (names[settings.authorizationStatus] ?? "inconnu")
                + (settings.alertStyle == .none ? ", sans bannière" : settings.alertStyle == .banner ? ", bannières" : ", alertes")
            DispatchQueue.main.async { self?.status = status }
        }
    }

    func finished(_ session: Session) {
        guard enabled else { return }
        var body = "C'est fini !"
        if let worked = session.turnDuration, worked >= 5 { body += " Claude a bossé \(Texts.duration(worked))." }
        let content = UNMutableNotificationContent()
        content.title = session.name
        content.body = body
        content.userInfo = ["session": session.id]
        // Pas de son ici : l'option « Son quand c'est fini » joue déjà le sien.
        let request = UNNotificationRequest(identifier: "fini-\(session.id)", content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request) { error in
            if error != nil { Self.fallback(title: session.name, body: body) }
        }
    }

    /// Si macOS refuse les notifications de l'app, on passe par AppleScript.
    private static func fallback(title: String, body: String) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
                             "-e", "end run", title, body]
        try? process.run()
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        if let id = response.notification.request.content.userInfo["session"] as? String {
            DispatchQueue.main.async { self.onClick(id) }
        }
        completionHandler()
    }
}
