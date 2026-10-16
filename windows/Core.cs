// Lutins pour Windows : les sessions, la lecture des transcripts, le serveur des hooks et le branchement
// dans %USERPROFILE%\.claude\settings.json. Même logique que Core.swift.
//
// Claude Code lance « Lutins.exe --hook <Événement> » (forme exec, sans shell) avec le JSON de l'événement
// sur l'entrée standard ; ce petit process le transmet en HTTP à l'app sur 127.0.0.1:3850.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Lutins
{
    enum LutinState { Working, Waiting, Done, Idle }

    sealed class Session
    {
        public readonly string Id;
        public readonly int Order;
        public string Cwd;
        public string TranscriptPath;
        public string Title;
        public string Entrypoint = "";
        public string Terminal = "";
        public LutinState State = LutinState.Idle;
        public DateTime StateSince = DateTime.UtcNow;
        public string Tool;
        public string WaitReason;
        public DateTime? TurnStart;
        public double? TurnDuration;
        public DateTime LastEvent = DateTime.UtcNow;
        public DateTime TitleCheckedAt = DateTime.MinValue;
        public bool Interrupted;
        public bool Ended;

        public Session(string id, string cwd, int order)
        {
            Id = id;
            Cwd = cwd;
            Order = order;
        }

        public string Name
        {
            get
            {
                if (!string.IsNullOrEmpty(Title)) return Title;
                string folder = "";
                try { folder = Path.GetFileName((Cwd ?? "").TrimEnd('\\', '/')); } catch (ArgumentException) { }
                return string.IsNullOrEmpty(folder) || folder.StartsWith("scratch-") ? "Sans dossier" : folder;
            }
        }

        public static string StateName(LutinState state)
        {
            switch (state)
            {
                case LutinState.Working: return "working";
                case LutinState.Waiting: return "waiting";
                case LutinState.Done: return "done";
                default: return "idle";
            }
        }
    }

    sealed class Store
    {
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        int counter;
        public Action OnChange = delegate { };
        public Action<Session> OnFinished = delegate { };
        /// Pour revenir sur le fil de l'interface après une lecture de fichier en arrière-plan.
        public Action<Action> Post = a => a();

        public IDictionary<string, Session> Sessions { get { return sessions; } }
        public List<Session> Ordered { get { return sessions.Values.OrderBy(s => s.Order).ToList(); } }

        public void Handle(string evt, JObj body, IDictionary<string, string> query)
        {
            string id = body.Str("session_id");
            if (string.IsNullOrEmpty(id)) return;
            string cwd = body.Str("cwd") ?? "";

            if (evt == "SessionEnd")
            {
                Session ended;
                if (!sessions.TryGetValue(id, out ended)) return;
                // L'app Claude peut arrêter le process entre deux messages : un lutin « fini » reste visible
                // jusqu'à ce qu'il s'endorme.
                if (ended.State == LutinState.Done) ended.Ended = true; else sessions.Remove(id);
                OnChange();
                return;
            }

            Session s;
            if (!sessions.TryGetValue(id, out s))
            {
                counter++;
                s = new Session(id, cwd, counter);
                sessions[id] = s;
            }
            if (cwd.Length > 0) s.Cwd = cwd;
            string path = body.Str("transcript_path");
            if (!string.IsNullOrEmpty(path)) s.TranscriptPath = path;
            string value;
            if (query.TryGetValue("entry", out value) && value.Length > 0) s.Entrypoint = value;
            if (query.TryGetValue("term", out value) && value.Length > 0) s.Terminal = value;
            s.LastEvent = DateTime.UtcNow;
            s.Ended = false;
            string tool = body.Str("tool_name");

            switch (evt)
            {
                case "UserPromptSubmit":
                    s.TurnStart = DateTime.UtcNow;
                    s.Tool = null;
                    Set(s, LutinState.Working);
                    RefreshTitle(s, true);
                    break;
                case "PreToolUse":
                    if (s.TurnStart == null || s.State == LutinState.Done || s.State == LutinState.Idle) s.TurnStart = DateTime.UtcNow;
                    s.Tool = tool;
                    if (tool == "AskUserQuestion") Wait(s, "A une question pour toi");
                    else if (tool == "ExitPlanMode") Wait(s, "Attend que tu valides son plan");
                    else Set(s, LutinState.Working);
                    break;
                case "PostToolUse":
                    s.Tool = null;
                    Set(s, LutinState.Working);
                    break;
                case "PermissionRequest":
                    Wait(s, "Attend ta permission");
                    break;
                case "Notification":
                    string type = body.Str("notification_type");
                    string message = body.Str("message") ?? "";
                    bool needsYou = type != null
                        ? type == "permission_prompt" || type == "elicitation_dialog"
                        : message.IndexOf("waiting for your input", StringComparison.OrdinalIgnoreCase) < 0;
                    if (needsYou && s.State != LutinState.Waiting) Wait(s, "Attend ta permission");
                    break;
                case "Stop":
                    bool wasDone = s.State == LutinState.Done;
                    s.Tool = null;
                    s.TurnDuration = s.TurnStart.HasValue ? (DateTime.UtcNow - s.TurnStart.Value).TotalSeconds : (double?)null;
                    Set(s, LutinState.Done);
                    RefreshTitle(s, true);
                    if (!wasDone) OnFinished(s);
                    break;
            }
            OnChange();
        }

        /// Clic sur un lutin vert : on l'a vu, il peut aller dormir.
        public void MarkSeen(string id)
        {
            Session s;
            if (!sessions.TryGetValue(id, out s) || s.State != LutinState.Done) return;
            Set(s, LutinState.Idle);
            OnChange();
        }

        public void Tidy()
        {
            foreach (var s in sessions.Values.ToList())
                if (s.State == LutinState.Done || s.State == LutinState.Idle) sessions.Remove(s.Id);
            OnChange();
        }

        /// Toutes les 3 s : interruptions, sessions plantées, lutins qui s'endorment puis s'en vont.
        public void Housekeeping()
        {
            DateTime now = DateTime.UtcNow;
            bool changed = false;
            foreach (var s in sessions.Values.ToList())
            {
                switch (s.State)
                {
                    case LutinState.Working:
                    case LutinState.Waiting:
                        if (s.TranscriptPath != null && Transcript.Interrupted(s.TranscriptPath, s.TurnStart ?? s.StateSince))
                        {
                            Set(s, LutinState.Idle);
                            s.Interrupted = true;
                            changed = true;
                        }
                        else if (s.State == LutinState.Working
                                 && (now - s.LastEvent).TotalMinutes > 15
                                 && (now - (Transcript.Modified(s.TranscriptPath) ?? s.LastEvent)).TotalMinutes > 15)
                        {
                            Set(s, LutinState.Idle);
                            changed = true;
                        }
                        break;
                    case LutinState.Done:
                        if ((now - s.StateSince).TotalMinutes > 30)
                        {
                            Set(s, LutinState.Idle);
                            changed = true;
                        }
                        break;
                    case LutinState.Idle:
                        if (s.Ended || (now - s.StateSince).TotalMinutes > 20)
                        {
                            sessions.Remove(s.Id);
                            changed = true;
                        }
                        break;
                }
                if (s.State != LutinState.Idle) RefreshTitle(s, false);
            }
            if (changed) OnChange();
        }

        void Set(Session s, LutinState state)
        {
            if (s.State != state)
            {
                s.State = state;
                s.StateSince = DateTime.UtcNow;
            }
            if (state != LutinState.Waiting) s.WaitReason = null;
            if (state != LutinState.Idle) s.Interrupted = false;
        }

        void Wait(Session s, string reason)
        {
            Set(s, LutinState.Waiting);
            s.WaitReason = reason;
        }

        void RefreshTitle(Session s, bool force)
        {
            string path = s.TranscriptPath;
            if (path == null) return;
            if (!force && (DateTime.UtcNow - s.TitleCheckedAt).TotalSeconds < 30) return;
            s.TitleCheckedAt = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string title = Transcript.Title(path);
                if (title == null) return;
                Post(delegate
                {
                    if (title == s.Title) return;
                    s.Title = title;
                    OnChange();
                });
            });
        }
    }

    // MARK: Transcripts (%USERPROFILE%\.claude\projects\…\<session>.jsonl)

    static class Transcript
    {
        /// Lignes complètes d'un morceau du fichier, la plus récente en premier.
        static List<string> Lines(string path, bool fromEnd, int bytes)
        {
            var result = new List<string>();
            try
            {
                // Claude Code écrit dans ce fichier pendant qu'on le lit : partage en lecture et écriture.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long size = stream.Length;
                    long start = fromEnd && size > bytes ? size - bytes : 0;
                    stream.Seek(start, SeekOrigin.Begin);
                    var buffer = new byte[Math.Min(bytes, size - start)];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    var parts = Encoding.UTF8.GetString(buffer, 0, read).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (start > 0 && parts.Count > 0) parts.RemoveAt(0);
                    if (!fromEnd && read == bytes && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    parts.Reverse();
                    result = parts;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return result;
        }

        static JObj Parse(string line)
        {
            try { return Json.Parse(line) as JObj; } catch (FormatException) { return null; }
        }

        /// Le titre affiché dans l'app Claude pour cette conversation.
        public static string Title(string path)
        {
            foreach (bool fromEnd in new[] { true, false })
                foreach (var line in Lines(path, fromEnd, 512 * 1024))
                {
                    if (line.IndexOf("Title\"", StringComparison.Ordinal) < 0) continue;
                    var obj = Parse(line);
                    if (obj == null) continue;
                    string title = obj.Str("customTitle") ?? obj.Str("aiTitle");
                    if (!string.IsNullOrEmpty(title)) return title;
                }
            return null;
        }

        /// Vrai si le dernier message de la conversation est une interruption faite après `date`.
        public static bool Interrupted(string path, DateTime after)
        {
            foreach (var line in Lines(path, true, 64 * 1024).Take(60))
            {
                var obj = Parse(line);
                if (obj == null) continue;
                string type = obj.Str("type");
                if (type != "user" && type != "assistant") continue;
                if (type != "user" || line.IndexOf("[Request interrupted by user", StringComparison.Ordinal) < 0) return false;
                DateTime stamp;
                if (!DateTime.TryParse(obj.Str("timestamp") ?? "", CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out stamp)) return true;
                return stamp >= after.AddSeconds(-1);
            }
            return false;
        }

        public static DateTime? Modified(string path)
        {
            try { return path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null; }
            catch (Exception) { return null; }
        }
    }

    // MARK: Serveur des hooks

    sealed class HookServer
    {
        public const int Port = 3850;
        TcpListener listener;
        public Action<string, JObj, Dictionary<string, string>> OnEvent = delegate { };
        public Func<string> State = () => "{}";
        public Action<string> OnFailure = delegate { };
        /// Exécute sur le fil de l'interface : sans attendre (événements) ou en attendant la réponse (/state).
        public Action<Action> Post = a => a();
        public Func<Func<string>, string> Ask = f => f();

        public void Start()
        {
            try
            {
                // Uniquement en local : rien n'est accessible depuis le réseau.
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.ExclusiveAddressUse = true;
                listener.Start();
            }
            catch (SocketException e)
            {
                OnFailure("Port " + Port + " indisponible : " + e.Message);
                return;
            }
            var thread = new Thread(Accept) { IsBackground = true, Name = "Lutins hooks" };
            thread.Start();
        }

        void Accept()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
            }
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 3000;
                    var stream = client.GetStream();
                    string method, target;
                    byte[] body;
                    if (!ReadRequest(stream, out method, out target, out body)) return;

                    string path = target, queryString = "";
                    int q = target.IndexOf('?');
                    if (q >= 0) { path = target.Substring(0, q); queryString = target.Substring(q + 1); }
                    var query = ParseQuery(queryString);

                    string status = "204 No Content";
                    string answer = "";
                    if (method == "POST" && path.StartsWith("/hook/"))
                    {
                        string evt = Uri.UnescapeDataString(path.Substring("/hook/".Length));
                        JObj json = null;
                        try { json = Json.Parse(Encoding.UTF8.GetString(body).TrimStart((char)0xFEFF)) as JObj; } catch (FormatException) { }
                        if (json != null) Post(delegate { OnEvent(evt, json, query); });
                    }
                    else if (method == "GET" && path == "/state")
                    {
                        answer = Ask(State);
                        status = "200 OK";
                    }
                    else
                    {
                        status = "404 Not Found";
                    }
                    // Corps vide pour les hooks : Claude Code ajouterait sinon la réponse au contexte de Claude.
                    byte[] payload = Encoding.UTF8.GetBytes(answer);
                    string head = "HTTP/1.1 " + status + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: "
                                  + payload.Length + "\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        static bool ReadRequest(NetworkStream stream, out string method, out string target, out byte[] body)
        {
            method = target = null;
            body = new byte[0];
            var data = new List<byte>();
            var buffer = new byte[8192];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) return false;
                for (int i = 0; i < n; i++) data.Add(buffer[i]);
                headerEnd = IndexOf(data, new byte[] { 13, 10, 13, 10 });
                if (data.Count > 1 << 20 && headerEnd < 0) return false;
            }
            string head = Encoding.ASCII.GetString(data.ToArray(), 0, headerEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var start = lines[0].Split(' ');
            if (start.Length < 2) return false;
            method = start[0];
            target = start[1];
            int length = 0;
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring(colon + 1).Trim(), out length);
            }
            if (length < 0 || length > 64 << 20) return false;
            int bodyStart = headerEnd + 4;
            while (data.Count - bodyStart < length)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) return false;
                for (int i = 0; i < n; i++) data.Add(buffer[i]);
            }
            body = data.GetRange(bodyStart, length).ToArray();
            return true;
        }

        static int IndexOf(List<byte> data, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= data.Count; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length && match; j++) match = data[i + j] == needle[j];
                if (match) return i;
            }
            return -1;
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>();
            foreach (var part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string key = eq >= 0 ? part.Substring(0, eq) : part;
                string value = eq >= 0 ? part.Substring(eq + 1) : "";
                result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return result;
        }
    }

    /// `Lutins.exe --hook <Événement>` : lancé par Claude Code, transmet le JSON reçu sur l'entrée standard.
    /// N'affiche rien et sort toujours avec le code 0, même si l'app ne tourne pas.
    static class HookClient
    {
        public static int Send(string evt)
        {
            try
            {
                byte[] body;
                using (var input = Console.OpenStandardInput())
                using (var copy = new MemoryStream())
                {
                    input.CopyTo(copy);
                    body = copy.ToArray();
                }
                string entry = Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT") ?? "";
                string term = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
                if (term.Length == 0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))) term = "WindowsTerminal";
                string target = "/hook/" + Uri.EscapeDataString(evt) + "?entry=" + Uri.EscapeDataString(entry)
                                + "&term=" + Uri.EscapeDataString(term);
                using (var client = new TcpClient())
                {
                    var pending = client.BeginConnect(IPAddress.Loopback, HookServer.Port, null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(500)) return 0;
                    client.EndConnect(pending);
                    client.SendTimeout = 2000;
                    client.ReceiveTimeout = 2000;
                    var stream = client.GetStream();
                    byte[] head = Encoding.ASCII.GetBytes("POST " + target + " HTTP/1.1\r\nHost: 127.0.0.1:" + HookServer.Port
                        + "\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(head, 0, head.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                    var buffer = new byte[1024];
                    while (stream.Read(buffer, 0, buffer.Length) > 0) { }
                }
            }
            catch (Exception) { }
            return 0;
        }
    }

    // MARK: Branchement dans settings.json

    static class Hooks
    {
        public static readonly string[] Events = { "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "SessionEnd" };

        /// LUTINS_SETTINGS_FILE sert à tester le branchement sur une copie.
        public static string SettingsPath
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("LUTINS_SETTINGS_FILE");
                if (!string.IsNullOrEmpty(custom)) return custom;
                string config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(config))
                    config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                return Path.Combine(config, "settings.json");
            }
        }

        public static string ExePath
        {
            get { return Path.GetFullPath(System.Reflection.Assembly.GetEntryAssembly().Location); }
        }

        /// Nos hooks : ceux qui lancent Lutins.exe --hook (ou l'ancienne forme curl de la version Mac).
        static bool IsOurs(object item)
        {
            var hook = item as JObj;
            if (hook == null) return false;
            string command = hook.Str("command") ?? "";
            if (command.Contains("127.0.0.1:" + HookServer.Port + "/hook/")) return true;
            var args = hook["args"] as List<object>;
            return command.EndsWith("Lutins.exe", StringComparison.OrdinalIgnoreCase)
                   && args != null && args.Count > 0 && (args[0] as string) == "--hook";
        }

        public static bool Installed
        {
            get
            {
                JObj root;
                try { root = Json.Parse(File.ReadAllText(SettingsPath)) as JObj; } catch (Exception) { return false; }
                var hooks = root != null ? root["hooks"] as JObj : null;
                if (hooks == null) return false;
                foreach (var evt in Events)
                {
                    var groups = hooks[evt] as List<object>;
                    bool found = groups != null && groups.OfType<JObj>().Any(g =>
                    {
                        var list = g["hooks"] as List<object>;
                        return list != null && list.OfType<JObj>().Any(h => IsOurs(h)
                            && string.Equals(h.Str("command"), ExePath, StringComparison.OrdinalIgnoreCase));
                    });
                    if (!found) return false;
                }
                return true;
            }
        }

        /// Ajoute nos hooks (en retirant d'abord les anciens), sans toucher à ceux des autres outils.
        public static string Install() { return Rewrite(true); }

        public static string Uninstall() { return Rewrite(false); }

        static string Rewrite(bool install)
        {
            string path = SettingsPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path)) File.WriteAllText(path, "{}\n", new UTF8Encoding(false));
                string backup = path + ".lutins-backup";
                if (!File.Exists(backup)) File.Copy(path, backup);

                string text = File.ReadAllText(path);
                JObj root;
                try { root = text.Trim().Length == 0 ? new JObj() : Json.Parse(text) as JObj; }
                catch (FormatException) { root = null; }
                if (root == null) return path + " n'est pas un JSON valide";

                var hooks = root["hooks"] as JObj ?? new JObj();
                foreach (var key in hooks.Keys.ToList())
                {
                    var groups = hooks[key] as List<object>;
                    if (groups == null) continue;
                    var kept = new List<object>();
                    foreach (var item in groups)
                    {
                        var group = item as JObj;
                        var list = group != null ? group["hooks"] as List<object> : null;
                        if (list == null) { kept.Add(item); continue; }
                        var others = list.Where(h => !IsOurs(h)).ToList();
                        if (others.Count == 0) continue;
                        group["hooks"] = others;
                        kept.Add(group);
                    }
                    if (kept.Count == 0 && !install) hooks.Remove(key); else hooks[key] = kept;
                }
                if (install)
                {
                    foreach (var evt in Events)
                    {
                        var groups = hooks[evt] as List<object> ?? new List<object>();
                        var hook = new JObj();
                        hook["type"] = "command";
                        hook["command"] = ExePath;
                        hook["args"] = new List<object> { "--hook", evt };
                        hook["async"] = true;
                        hook["timeout"] = 5;
                        var group = new JObj();
                        group["matcher"] = "*";
                        group["hooks"] = new List<object> { hook };
                        groups.Add(group);
                        hooks[evt] = groups;
                    }
                }
                if (hooks.Count == 0 && !install) root.Remove("hooks"); else root["hooks"] = hooks;

                string temp = path + ".lutins-tmp";
                File.WriteAllText(temp, Json.Write(root) + "\n", new UTF8Encoding(false));
                try { File.Replace(temp, path, null); }
                catch (IOException)
                {
                    File.Copy(temp, path, true);
                    File.Delete(temp);
                }
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
    }

    // MARK: Réglages (HKCU\Software\Lutins) et ouverture au démarrage

    static class Prefs
    {
        const string Key = @"Software\Lutins";

        public static string Get(string name, string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                {
                    var value = key != null ? key.GetValue(name) as string : null;
                    return value ?? fallback;
                }
            }
            catch (Exception) { return fallback; }
        }

        public static void Set(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value == null) key.DeleteValue(name, false); else key.SetValue(name, value);
                }
            }
            catch (Exception) { }
        }

        public static bool GetBool(string name, bool fallback)
        {
            string value = Get(name, null);
            return value == null ? fallback : value == "1";
        }

        public static void SetBool(string name, bool value) { Set(name, value ? "1" : "0"); }

        public static bool Has(string name) { return Get(name, null) != null; }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        var value = key != null ? key.GetValue("Lutins") as string : null;
                        return value != null && value.Trim('"').Equals(Hooks.ExePath, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception) { return false; }
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) key.SetValue("Lutins", "\"" + Hooks.ExePath + "\""); else key.DeleteValue("Lutins", false);
                }
            }
            catch (Exception) { }
        }
    }

    // MARK: JSON (ordre des clés conservé, nombres recopiés tels quels)

    sealed class JObj
    {
        readonly List<KeyValuePair<string, object>> items = new List<KeyValuePair<string, object>>();

        public object this[string key]
        {
            get
            {
                foreach (var item in items) if (item.Key == key) return item.Value;
                return null;
            }
            set
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Key == key) { items[i] = new KeyValuePair<string, object>(key, value); return; }
                items.Add(new KeyValuePair<string, object>(key, value));
            }
        }

        public string Str(string key) { return this[key] as string; }
        public int Count { get { return items.Count; } }
        public IEnumerable<string> Keys { get { return items.Select(i => i.Key); } }
        public IEnumerable<KeyValuePair<string, object>> Items { get { return items; } }
        public void Remove(string key) { items.RemoveAll(i => i.Key == key); }
    }

    /// Un nombre lu dans un fichier, réécrit exactement comme il était.
    sealed class JNumber
    {
        public readonly string Text;
        public JNumber(string text) { Text = text; }
    }

    static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            if (text.Length > 0 && text[0] == '﻿') i = 1;
            object value = ParseValue(text, ref i);
            SkipSpace(text, ref i);
            if (i != text.Length) throw new FormatException("JSON : texte en trop");
            return value;
        }

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON : fin inattendue");
            char c = s[i];
            if (c == '{')
            {
                var obj = new JObj();
                i++;
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    SkipSpace(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("JSON : clé attendue");
                    string key = ParseString(s, ref i);
                    SkipSpace(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("JSON : « : » attendu");
                    i++;
                    obj[key] = ParseValue(s, ref i);
                    SkipSpace(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return obj; }
                    throw new FormatException("JSON : « , » ou « } » attendu");
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                i++;
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ParseValue(s, ref i));
                    SkipSpace(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new FormatException("JSON : « , » ou « ] » attendu");
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("JSON : valeur inattendue");
            return new JNumber(s.Substring(start, i - start));
        }

        static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON : \\u incomplet");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("JSON : texte non terminé");
        }

        /// Écrit avec deux espaces d'indentation, comme Claude Code.
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is JNumber) { sb.Append(((JNumber)value).Text); return; }
            if (value is int || value is long) { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is double) { sb.Append(((double)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            string pad = new string(' ', 2 * (depth + 1)), end = new string(' ', 2 * depth);
            var obj = value as JObj;
            if (obj != null)
            {
                if (obj.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                bool first = true;
                foreach (var item in obj.Items)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append(pad);
                    WriteString(sb, item.Key);
                    sb.Append(": ");
                    WriteValue(sb, item.Value, depth + 1);
                }
                sb.Append("\n").Append(end).Append("}");
                return;
            }
            var list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                var items = list.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int k = 0; k < items.Count; k++)
                {
                    if (k > 0) sb.Append(",\n");
                    sb.Append(pad);
                    WriteValue(sb, items[k], depth + 1);
                }
                sb.Append("\n").Append(end).Append("]");
                return;
            }
            WriteString(sb, value.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
