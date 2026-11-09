using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

// The Claude Code side: the exe runs as the hook and status line command (--hook, --statusline), and
// can add itself to Claude Code's settings.json or take itself out again (--install-claude,
// --uninstall-claude).
static class ClaudeHooks
{
    // what the hub follows; see ClaudeSessions.Hook
    static readonly string[] Events =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
        "Notification", "PreCompact", "Stop", "StopFailure", "SessionEnd",
    };

    const string ExeName = "zmk-hub-display.exe";

    // Hook or status line command: passes the event on stdin to the running app. Always exits 0 and,
    // as a hook, prints nothing, so it can never change what Claude Code does. As the status line it
    // prints the app's line, or makes the line itself when the app is not running.
    public static int Forward(string path, bool statusLine)
    {
        byte[] body;
        try
        {
            body = ReadAll(Console.OpenStandardInput());
        }
        catch (IOException)
        {
            return 0;
        }

        string reply = Post(path, body);

        if (statusLine)
        {
            if (reply == null)
            {
                Dictionary<string, object> e = Parse(Encoding.UTF8.GetString(body));
                if (e != null)
                    reply = new ClaudeSessions().StatusLine(e);
            }
            if (!string.IsNullOrEmpty(reply))
            {
                byte[] text = Encoding.UTF8.GetBytes(reply);
                using (Stream o = Console.OpenStandardOutput())
                    o.Write(text, 0, text.Length);
            }
        }
        return 0;
    }

    // Plain socket instead of HttpWebRequest: Windows retries a refused connect for about two seconds,
    // and the status line should not hang that long while the app is not running. Returns the reply
    // body, or null when the app is not there.
    static string Post(string path, byte[] body)
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult connect = client.BeginConnect(IPAddress.Loopback, ClaudeServer.Port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(150) || !client.Connected)
                    return null;
                client.EndConnect(connect);
                client.ReceiveTimeout = client.SendTimeout = 2000;
                NetworkStream net = client.GetStream();
                byte[] head = Encoding.ASCII.GetBytes("POST " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n" +
                    "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                net.Write(head, 0, head.Length);
                net.Write(body, 0, body.Length);
                string response = Encoding.UTF8.GetString(ReadAll(net)); // the server closes after replying
                int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                return response.StartsWith("HTTP/1.1 200") && split >= 0 ? response.Substring(split + 4) : null;
            }
        }
        catch (SocketException) { return null; }
        catch (IOException) { return null; }
    }

    static byte[] ReadAll(Stream s)
    {
        MemoryStream m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    // ---- settings.json ----

    // Adds (or removes) the hooks and the status line in Claude Code's user settings. Everything else in
    // the file is kept; a status line that is not ours is left alone. The old file is kept as a backup.
    public static int Install(bool install)
    {
        string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
        string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string file = Path.Combine(dir, "settings.json");

        Dictionary<string, object> settings = new Dictionary<string, object>();
        if (File.Exists(file))
        {
            string text = File.ReadAllText(file, Encoding.UTF8);
            if (text.Trim().Length > 0)
            {
                settings = Parse(text);
                if (settings == null)
                {
                    Print(file + " is not valid JSON; nothing changed.");
                    return 1;
                }
            }
        }

        // hooks: our entries out first (also ones pointing at an old location), then back in if installing
        Dictionary<string, object> hooks = settings.ContainsKey("hooks") ? settings["hooks"] as Dictionary<string, object> : null;
        if (hooks == null && settings.ContainsKey("hooks"))
        {
            Print("\"hooks\" in " + file + " is not an object; nothing changed.");
            return 1;
        }
        if (hooks == null)
            hooks = new Dictionary<string, object>();
        foreach (string ev in hooks.Keys.ToList())
        {
            List<object> groups = List(hooks[ev]);
            if (groups == null)
                continue;
            foreach (object g in groups.ToList())
            {
                Dictionary<string, object> group = g as Dictionary<string, object>;
                List<object> handlers = group != null ? List(group.ContainsKey("hooks") ? group["hooks"] : null) : null;
                if (handlers == null)
                    continue;
                handlers.RemoveAll(IsOurHook);
                group["hooks"] = handlers;
                if (handlers.Count == 0)
                    groups.Remove(g);
            }
            if (groups.Count == 0)
                hooks.Remove(ev);
            else
                hooks[ev] = groups;
        }
        if (install)
            foreach (string ev in Events)
            {
                List<object> groups = hooks.ContainsKey(ev) ? List(hooks[ev]) ?? new List<object>() : new List<object>();
                groups.Add(new Dictionary<string, object>
                {
                    { "hooks", new List<object> { new Dictionary<string, object>
                        {
                            { "type", "command" },
                            { "command", exe },         // exec form: no shell, so Git Bash or not does not matter
                            { "args", new List<object> { "--hook" } },
                            { "async", true },          // never makes Claude wait
                            { "timeout", 10 },
                        } } },
                });
                hooks[ev] = groups;
            }
        if (hooks.Count > 0)
            settings["hooks"] = hooks;
        else
            settings.Remove("hooks");

        // status line: ours is replaced or removed, anyone else's is kept
        string statusNote = "";
        Dictionary<string, object> status = settings.ContainsKey("statusLine") ? settings["statusLine"] as Dictionary<string, object> : null;
        bool ours = status != null && IsOurs(status.ContainsKey("command") ? status["command"] as string : null, "--statusline");
        if (install && (status == null && !settings.ContainsKey("statusLine") || ours))
            settings["statusLine"] = new Dictionary<string, object>
            {
                { "type", "command" },
                // the status line runs through a shell; forward slashes and quotes work in Git Bash and cmd
                { "command", "\"" + exe.Replace('\\', '/') + "\" --statusline" },
            };
        else if (install)
            statusNote = " Your own status line was kept; quota needs the status line, so it stays empty on the hub.";
        else if (ours)
            settings.Remove("statusLine");

        Directory.CreateDirectory(dir);
        string tmp = file + ".tmp", backup = file + ".zmk-hub-display.bak";
        File.WriteAllText(tmp, Json(settings, "") + "\n", new UTF8Encoding(false));
        if (File.Exists(file))
            File.Replace(tmp, file, backup);
        else
            File.Move(tmp, file);

        Print((install ? "Added the hub hooks to " : "Removed the hub hooks from ") + file + "." + statusNote +
            (File.Exists(backup) ? " Previous file: " + backup : ""));
        return 0;
    }

    static bool IsOurHook(object h)
    {
        Dictionary<string, object> d = h as Dictionary<string, object>;
        if (d == null)
            return false;
        List<object> args = List(d.ContainsKey("args") ? d["args"] : null);
        string command = d.ContainsKey("command") ? d["command"] as string : null;
        return IsOurs(command, "--hook") || (IsOurs(command, "") && args != null && args.Contains("--hook"));
    }

    // a command that runs this app (from wherever it was installed) in the given mode
    static bool IsOurs(string command, string mode)
    {
        return command != null && command.IndexOf(ExeName, StringComparison.OrdinalIgnoreCase) >= 0 && command.Contains(mode);
    }

    static List<object> List(object o)
    {
        IList l = o as IList;
        return l == null ? null : l.Cast<object>().ToList();
    }

    static Dictionary<string, object> Parse(string text)
    {
        try
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 << 20 }.DeserializeObject(text) as Dictionary<string, object>;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // Pretty JSON with two-space indents, the way Claude Code writes the file.
    static string Json(object o, string indent)
    {
        string inner = indent + "  ";
        Dictionary<string, object> d = o as Dictionary<string, object>;
        if (d != null)
        {
            if (d.Count == 0)
                return "{}";
            return "{\n" + string.Join(",\n", d.Select(kv => inner + Quote(kv.Key) + ": " + Json(kv.Value, inner)).ToArray()) + "\n" + indent + "}";
        }
        IList l = o as IList;
        if (l != null && !(o is string))
        {
            if (l.Count == 0)
                return "[]";
            return "[\n" + string.Join(",\n", l.Cast<object>().Select(v => inner + Json(v, inner)).ToArray()) + "\n" + indent + "]";
        }
        if (o == null)
            return "null";
        if (o is bool)
            return (bool)o ? "true" : "false";
        if (o is string)
            return Quote((string)o);
        return Convert.ToString(o, CultureInfo.InvariantCulture);
    }

    static string Quote(string s)
    {
        StringBuilder sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // The app is a windows exe without a console; borrow the one it was started from, if any.
    static void Print(string line)
    {
        AttachConsole(-1);
        Console.WriteLine(line);
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
