// Point d'entrée : relie le serveur des hooks, les sessions, l'encoche et l'icône près de l'horloge.
//
//   Lutins.exe                      lance l'app (une seule à la fois)
//   Lutins.exe --hook <Événement>   appelé par Claude Code à chaque événement
//   Lutins.exe --install-hooks      branche Lutins dans settings.json
//   Lutins.exe --uninstall-hooks    le débranche (et retire l'ouverture au démarrage)
//   Lutins.exe --preview <dossier>  dessine les persos et l'encoche en PNG, pour vérifier sans écran

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Lutins")]
[assembly: AssemblyProduct("Lutins")]
[assembly: AssemblyDescription("Un perso par session Claude Code, en haut de l'écran")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace Lutins
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Le chemin le plus fréquent (des dizaines de fois par conversation) : rien d'autre à charger.
            if (args.Length >= 2 && args[0] == "--hook") return HookClient.Send(args[1]);
            if (args.Length >= 1 && (args[0] == "--install-hooks" || args[0] == "--uninstall-hooks")) return HooksCommand(args[0] == "--install-hooks");
            if (args.Length >= 2 && args[0] == "--preview") return Previews.Render(args[1]);
            return RunApp();
        }

        static int HooksCommand(bool install)
        {
            string error = install ? Hooks.Install() : Hooks.Uninstall();
            if (!install && error == null) Startup.Set(false);
            Console.WriteLine(error ?? "ok");
            return error == null ? 0 : 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunApp()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\Lutins-encoche", out created))
            {
                if (!created) return 0;
                Native.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var app = new App();
                app.Start();
                Application.Run();
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    sealed class App
    {
        readonly Store store = new Store();
        readonly HookServer server = new HookServer();
        readonly Island island;
        readonly Control ui = new Control();
        readonly System.Windows.Forms.Timer chores = new System.Windows.Forms.Timer { Interval = 3000 };
        NotifyIcon tray;
        string lastFinished;

        public App()
        {
            island = new Island(store);
        }

        public void Start()
        {
            IntPtr handle = ui.Handle; // crée la fenêtre invisible qui ramène le travail sur le fil de l'interface
            store.Post = a => ui.BeginInvoke(a);
            store.OnChange = island.Refresh;
            store.OnFinished = Finished;
            server.Post = a => ui.BeginInvoke(a);
            server.Ask = f => (string)ui.Invoke(f);
            server.OnEvent = (evt, body, query) => store.Handle(evt, body, query);
            server.State = StateJson;
            server.OnFailure = message =>
            {
                island.Problem = message;
                island.Refresh();
            };
            server.Start();
            if (!Hooks.Installed)
            {
                string error = Hooks.Install();
                if (error != null) island.Problem = "Branchement à Claude Code impossible : " + error;
            }
            EnableStartupByDefault();

            island.OptionsMenu = () =>
            {
                var menu = new ContextMenuStrip();
                Fill(menu);
                return menu;
            };
            island.OnOpen = Open;
            island.Rebuild();
            SetUpTray();

            chores.Tick += delegate { store.Housekeeping(); };
            chores.Start();
            SystemEvents.DisplaySettingsChanged += delegate { ui.BeginInvoke((Action)island.Rebuild); };
            GC.KeepAlive(handle);
        }

        // MARK: Icône près de l'horloge (menu, et bulle quand c'est fini)

        void SetUpTray()
        {
            tray = new NotifyIcon { Text = "Lutins", Icon = MakeIcon(), Visible = true };
            var menu = new ContextMenuStrip();
            menu.Opening += (sender, e) =>
            {
                menu.Items.Clear();
                Fill(menu);
                e.Cancel = false;
            };
            tray.ContextMenuStrip = menu;
            tray.BalloonTipClicked += delegate
            {
                Session session;
                if (lastFinished != null && store.Sessions.TryGetValue(lastFinished, out session)) Open(session);
                else ActivateFirst("claude");
            };
        }

        Icon MakeIcon()
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            int pixel = Math.Max(1, size / Skins.Cols);
            string skin = island.SkinChoice == Skins.Mix ? "lutin" : island.SkinChoice;
            using (var sprite = Skins.Icon(skin, pixel))
            using (var square = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(square))
                {
                    g.Clear(Color.Transparent);
                    g.DrawImageUnscaled(sprite, (size - sprite.Width) / 2, (size - sprite.Height) / 2);
                }
                return Icon.FromHandle(square.GetHicon());
            }
        }

        void Finished(Session s)
        {
            if (Prefs.GetBool("sound", false)) PlaySound();
            if (!Prefs.GetBool("notify", true) || tray == null) return;
            string body = "C'est fini !";
            if (s.TurnDuration.HasValue && s.TurnDuration.Value >= 5) body += " Claude a bossé " + Texts.Duration(s.TurnDuration.Value) + ".";
            lastFinished = s.Id;
            tray.ShowBalloonTip(5000, s.Name, body, ToolTipIcon.None);
        }

        static void PlaySound()
        {
            try
            {
                string wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Windows Notify System Generic.wav");
                if (File.Exists(wav))
                {
                    new SoundPlayer(wav).Play();
                    return;
                }
            }
            catch (Exception) { }
            SystemSounds.Asterisk.Play();
        }

        // MARK: Réglages (clic droit sur l'encoche, sur « ••• », ou sur l'icône près de l'horloge)

        void Fill(ContextMenuStrip menu)
        {
            var sessions = store.Ordered;
            if (island.Problem != null) menu.Items.Add(Disabled("Problème : " + island.Problem));
            menu.Items.Add(Disabled(Texts.Summary(sessions)));
            menu.Items.Add(new ToolStripSeparator());

            var sizes = new ToolStripMenuItem("Taille de l'encoche");
            string currentSize = Prefs.Get("size", "medium");
            foreach (var pair in new[] { new[] { "small", "Petite" }, new[] { "medium", "Moyenne" }, new[] { "large", "Grande" } })
            {
                string value = pair[0];
                var item = new ToolStripMenuItem(pair[1]) { Checked = currentSize == value };
                item.Click += delegate
                {
                    Prefs.Set("size", value);
                    island.Refresh();
                };
                sizes.DropDownItems.Add(item);
            }
            menu.Items.Add(sizes);

            var skins = new ToolStripMenuItem("Personnage");
            skins.DropDown.ImageScalingSize = Skins.SizeFor(2);
            foreach (var id in Skins.Ids.Concat(new[] { Skins.Mix }))
            {
                string value = id;
                if (value == Skins.Mix) skins.DropDownItems.Add(new ToolStripSeparator());
                var item = new ToolStripMenuItem(value == Skins.Mix ? "Un perso différent par session" : Skins.Name(value))
                {
                    Checked = island.SkinChoice == value,
                };
                if (value != Skins.Mix) item.Image = Skins.Icon(value, 2);
                item.Click += delegate
                {
                    Prefs.Set("skin", value);
                    island.Refresh();
                    if (tray != null) tray.Icon = MakeIcon();
                };
                skins.DropDownItems.Add(item);
            }
            menu.Items.Add(skins);

            var all = Screen.AllScreens;
            if (all.Length > 1)
            {
                string currentScreen = Prefs.Get("screen", "all");
                var screens = new ToolStripMenuItem("Écran");
                var choices = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("all", "Tous les écrans") };
                for (int i = 0; i < all.Length; i++)
                    choices.Add(new KeyValuePair<string, string>(all[i].DeviceName, "Écran " + (i + 1) + (all[i].Primary ? " (principal)" : "")));
                foreach (var choice in choices)
                {
                    string value = choice.Key;
                    var item = new ToolStripMenuItem(choice.Value) { Checked = currentScreen == value };
                    item.Click += delegate
                    {
                        Prefs.Set("screen", value);
                        island.Rebuild();
                    };
                    screens.DropDownItems.Add(item);
                }
                menu.Items.Add(screens);
            }

            menu.Items.Add(Toggle("Son quand c'est fini", Prefs.GetBool("sound", false), on =>
            {
                Prefs.SetBool("sound", on);
                if (on) PlaySound();
            }));
            menu.Items.Add(Toggle("Ouvrir au démarrage de Windows", Startup.Enabled, on =>
            {
                Prefs.SetBool("openAtLogin", on);
                Startup.Set(on);
            }));
            menu.Items.Add(Toggle("Notification quand c'est fini", Prefs.GetBool("notify", true), on => Prefs.SetBool("notify", on)));
            var tidy = Action("Ranger les persos finis", store.Tidy);
            tidy.Enabled = sessions.Any(s => s.State == LutinState.Done || s.State == LutinState.Idle);
            menu.Items.Add(tidy);
            menu.Items.Add(Action("Remettre l'encoche en haut au centre", island.ResetPlacements));
            if (!Hooks.Installed) menu.Items.Add(Action("Brancher à Claude Code", Connect));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Action("Débrancher de Claude Code et quitter", UnplugAndQuit));
            menu.Items.Add(Action("Quitter Lutins", Quit));
        }

        static ToolStripMenuItem Disabled(string title)
        {
            return new ToolStripMenuItem(title) { Enabled = false };
        }

        static ToolStripMenuItem Action(string title, Action action)
        {
            var item = new ToolStripMenuItem(title);
            item.Click += delegate { action(); };
            return item;
        }

        static ToolStripMenuItem Toggle(string title, bool on, Action<bool> change)
        {
            var item = new ToolStripMenuItem(title) { Checked = on };
            item.Click += delegate { change(!on); };
            return item;
        }

        // MARK: Actions

        /// Clic sur un perso : la fenêtre où tourne cette session passe au premier plan.
        void Open(Session session)
        {
            store.MarkSeen(session.Id);
            string[] apps;
            if (session.Entrypoint.Contains("desktop")) apps = new[] { "claude" };
            else if (session.Terminal == "vscode" || session.Entrypoint.Contains("vscode")) apps = new[] { "Code" };
            else if (session.Terminal == "WindowsTerminal") apps = new[] { "WindowsTerminal" };
            else apps = new[] { "claude", "WindowsTerminal", "Code" };
            ActivateFirst(apps);
        }

        static void ActivateFirst(params string[] names)
        {
            foreach (var name in names)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (process.MainWindowHandle == IntPtr.Zero) continue;
                        Native.Activate(process.MainWindowHandle);
                        return;
                    }
                    catch (InvalidOperationException) { }
                    finally { process.Dispose(); }
                }
            }
        }

        /// Ouverture au démarrage : activée d'office, l'option du menu permet de la couper.
        static void EnableStartupByDefault()
        {
            if (!Prefs.Has("openAtLogin")) Prefs.SetBool("openAtLogin", true);
            if (Prefs.GetBool("openAtLogin", true) && !Startup.Enabled) Startup.Set(true);
        }

        void Connect()
        {
            string error = Hooks.Install();
            if (error != null)
            {
                MessageBox.Show(error, "Branchement à Claude Code impossible", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            island.Problem = null;
            island.Refresh();
        }

        void UnplugAndQuit()
        {
            string error = Hooks.Uninstall();
            if (error != null)
            {
                MessageBox.Show(error, "Débranchement impossible", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Startup.Set(false);
            Quit();
        }

        void Quit()
        {
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            Application.Exit();
        }

        string StateJson()
        {
            var root = new JObj();
            root["platform"] = "windows";
            root["problem"] = island.Problem;
            root["hooks"] = Hooks.Installed;
            root["settings"] = Hooks.SettingsPath;
            root["openAtLogin"] = Startup.Enabled ? "enabled" : "disabled";
            root["notifications"] = Prefs.GetBool("notify", true) ? "bulles" : "coupées";
            root["islands"] = island.Describe();
            var list = new List<object>();
            foreach (var s in store.Ordered)
            {
                var o = new JObj();
                o["id"] = s.Id;
                o["name"] = s.Name;
                o["state"] = Session.StateName(s.State);
                o["tool"] = s.Tool;
                o["waitReason"] = s.WaitReason;
                o["entrypoint"] = s.Entrypoint;
                o["terminal"] = s.Terminal;
                o["since"] = (int)(DateTime.UtcNow - s.StateSince).TotalSeconds;
                list.Add(o);
            }
            root["sessions"] = list;
            return Json.Write(root);
        }
    }

    // MARK: Aperçus pour vérifier les dessins sans écran

    static class Previews
    {
        public static int Render(string folder)
        {
            Directory.CreateDirectory(folder);
            RenderSkins(Path.Combine(folder, "persos.png"));

            var store = new Store();
            var none = new Dictionary<string, string>();
            Action<string, string, string, string> send = (id, cwd, evt, tool) =>
            {
                var body = new JObj();
                body["session_id"] = id;
                body["cwd"] = cwd;
                if (tool != null) body["tool_name"] = tool;
                store.Handle(evt, body, none);
            };
            send("a", @"C:\p\serveur-data", "UserPromptSubmit", null);
            send("a", @"C:\p\serveur-data", "PreToolUse", "Bash");
            send("b", @"C:\p\api", "UserPromptSubmit", null);
            send("b", @"C:\p\api", "PermissionRequest", null);
            send("c", @"C:\p\site-vitrine", "UserPromptSubmit", null);
            send("c", @"C:\p\site-vitrine", "Stop", null);
            send("d", @"C:\p\app-mobile", "UserPromptSubmit", null);
            send("d", @"C:\p\app-mobile", "PreToolUse", "Edit");
            store.Sessions["a"].Title = "Migration Postgres";
            store.Sessions["b"].Title = "Tests de l'API de paiement";

            foreach (var size in new[] { IslandSize.Small, IslandSize.Medium, IslandSize.Large })
                Save(Island.Snapshot(store, Skins.Mix, size, false, 1), Path.Combine(folder, "encoche-" + size.ToString().ToLowerInvariant() + ".png"));
            Save(Island.Snapshot(store, Skins.Mix, IslandSize.Medium, true, 1), Path.Combine(folder, "encoche-depliee.png"));
            Save(Island.Snapshot(store, "panda-roux", IslandSize.Medium, true, 1.5f), Path.Combine(folder, "encoche-depliee-150.png"));
            Save(Island.Snapshot(new Store(), "koala", IslandSize.Medium, true, 1), Path.Combine(folder, "encoche-vide.png"));
            Console.WriteLine("ok");
            return 0;
        }

        /// Pose l'encoche en haut d'un faux bureau gris clair, pour voir ses bords.
        static void Save(Bitmap island, string path)
        {
            using (island)
            using (var canvas = new Bitmap(island.Width + 60, island.Height + 30, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.FromArgb(221, 227, 234));
                    g.DrawImageUnscaled(island, 30, 0);
                }
                canvas.Save(path, ImageFormat.Png);
            }
        }

        /// Chaque perso (lignes) dans chaque état (colonnes), sur fond noir.
        static void RenderSkins(string path)
        {
            var poses = new[]
            {
                new { State = LutinState.Working, Age = 10.0, Frame = 0 },
                new { State = LutinState.Working, Age = 10.0, Frame = 1 },
                new { State = LutinState.Waiting, Age = 10.0, Frame = 0 },
                new { State = LutinState.Done, Age = 0.0, Frame = 2 },
                new { State = LutinState.Done, Age = 10.0, Frame = 0 },
                new { State = LutinState.Idle, Age = 10.0, Frame = 0 },
            };
            int pixel = 8, pad = 16;
            var cell = Skins.SizeFor(pixel);
            int cellW = cell.Width + 2 * pad, cellH = cell.Height + 2 * pad;
            using (var bitmap = new Bitmap(poses.Length * cellW, Skins.Ids.Length * cellH, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Black);
                    for (int row = 0; row < Skins.Ids.Length; row++)
                        for (int col = 0; col < poses.Length; col++)
                            Skins.Draw(g, Skins.Ids[row], new Look(poses[col].State, poses[col].Age), poses[col].Frame,
                                       col * cellW + pad, row * cellH + pad, pixel);
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }
}
