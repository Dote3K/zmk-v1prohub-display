using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

// Follows Claude Code sessions through their hook events and the status line, and decides what the
// Claude screen shows. Times are the app's monotonic clock in ms; quota reset times are Unix seconds.
sealed class ClaudeSessions
{
    enum Phase { Idle, Thinking, Working, NeedsYou, Done, Error }

    sealed class Session
    {
        public string Id, Project = "";
        public Phase Phase = Phase.Idle;
        public string Pet = "", State = "", Detail = "", Error = "";
        public double PromptAt = -1, PhaseAt, PetAt, LastAt, StopAt = double.NegativeInfinity, TurnMs;
        public int Tools;
        public int Failures;            // tool calls failed in a row
        public double Context = -1;     // % of the context window used, from the status line
    }

    const double StaleMs = 10 * 60000;       // thinking or working with no events: interrupted (Esc fires no Stop)
    const double WaitMs = 2 * 3600000.0;      // a question nobody answered
    const double ShowDoneMs = 2 * 60000;      // how long DONE and ERROR stay up
    const double ForgetMs = 12 * 3600000.0;   // no events at all, SessionEnd never came
    const double CelebrateMs = 3500;          // then the DONE pet calms down
    const double LateToolMs = 3000;           // hooks run in parallel, so tool events can trail the Stop

    readonly object sync = new object();
    readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
    double quota5h = -1, quota7d = -1, reset5h, reset7d;
    DateTime statusAt = DateTime.MinValue; // last status line call, for the settings window
    bool statusHadLimits;

    // Where the quota bars get their numbers from, in a sentence for the settings window.
    public string QuotaInfo()
    {
        lock (sync)
        {
            if (statusAt == DateTime.MinValue)
                return "No data yet. The quota comes from Claude Code's status line, which has not run since the app started.";
            string ago = Ago((DateTime.UtcNow - statusAt).TotalMilliseconds) + " ago";
            if (!statusHadLimits && quota5h < 0 && quota7d < 0)
                return "Status line seen " + ago + ", but without rate limits (Pro, Max or Ultra plans only, after the first reply).";
            return "5h " + (quota5h >= 0 ? Math.Round(quota5h) + "%" : "--") + " · 7d " + (quota7d >= 0 ? Math.Round(quota7d) + "%" : "--") +
                " · status line " + ago;
        }
    }

    public void Hook(Dictionary<string, object> e, double now)
    {
        string id = Str(e, "session_id"), name = Str(e, "hook_event_name");
        if (id.Length == 0 || name.Length == 0)
            return;
        lock (sync)
        {
            Session s;
            if (!sessions.TryGetValue(id, out s))
            {
                if (name == "SessionEnd")
                    return;
                sessions[id] = s = new Session { Id = id };
            }
            string cwd = Str(e, "cwd").TrimEnd('/', '\\');
            if (cwd.Length > 0)
                s.Project = cwd.Substring(Math.Max(cwd.LastIndexOf('/'), cwd.LastIndexOf('\\')) + 1);
            s.LastAt = now;
            bool late = now - s.StopAt < LateToolMs;

            switch (name)
            {
                case "SessionEnd":
                    sessions.Remove(id);
                    break;
                case "UserPromptSubmit":
                    s.PromptAt = now;
                    s.Tools = 0;
                    s.Failures = 0;
                    s.StopAt = double.NegativeInfinity;
                    Set(s, Phase.Thinking, "loading", "THINKING", OneLine(Str(e, "prompt")), now);
                    break;
                case "PreToolUse":
                    if (late)
                        break;
                    s.Tools++;
                    Tool(s, Str(e, "tool_name"), e.ContainsKey("tool_input") ? e["tool_input"] as Dictionary<string, object> : null, now);
                    break;
                case "PostToolUse":
                case "PostToolUseFailure":
                    if (late)
                        break;
                    if (s.Phase == Phase.NeedsYou)
                        Set(s, Phase.Working, s.Pet == "notification" ? "loading" : s.Pet, "WORKING", s.Detail, now); // permission answered
                    s.Failures = name == "PostToolUseFailure" ? s.Failures + 1 : 0;
                    if (s.Failures >= 2) // things keep going wrong: Clawd fights the fire
                        Set(s, Phase.Working, "working-firefighting", "FIXING", s.Detail, now);
                    break;
                case "PreCompact":
                    Set(s, Phase.Working, "working-context-full", "COMPACTING", "Summarizing the conversation", now);
                    break;
                case "Notification":
                    string type = Str(e, "notification_type");
                    if (type == "permission_prompt" || type == "elicitation_dialog" || type == "agent_needs_input")
                        Set(s, Phase.NeedsYou, "notification", "NEEDS YOU", OneLine(Str(e, "message")), now);
                    break;
                case "Stop":
                    s.StopAt = now;
                    s.TurnMs = s.PromptAt >= 0 ? now - s.PromptAt : 0;
                    Set(s, Phase.Done, "celebrating", "DONE", "", now);
                    break;
                case "StopFailure":
                    s.StopAt = now;
                    string error = Str(e, "error");
                    Set(s, Phase.Error, ErrorPet(error), ErrorTitle(error), OneLine(Str(e, "last_assistant_message")), now);
                    s.Error = OneLine(Str(e, "error_details"));
                    break;
            }
        }
    }

    static void Set(Session s, Phase phase, string pet, string state, string detail, double now)
    {
        if (s.Phase != phase)
            s.PhaseAt = now;
        if (s.Pet != pet)
            s.PetAt = now;
        s.Phase = phase;
        s.Pet = pet;
        s.State = state;
        s.Detail = detail;
    }

    // What the tool is doing, in a word and a short argument, and which pet acts it out.
    static void Tool(Session s, string tool, Dictionary<string, object> input, double now)
    {
        string pet, verb, arg;
        switch (tool)
        {
            case "Bash":
            case "PowerShell":
                arg = Str(input, "command");
                string c = arg.ToLowerInvariant();
                pet = BashPet(c);
                verb = "RUNNING";
                break;
            case "Edit":
            case "Write":
            case "MultiEdit":
            case "NotebookEdit":
                arg = FileName(Str(input, "file_path").Length > 0 ? Str(input, "file_path") : Str(input, "notebook_path"));
                pet = "working-building";
                verb = "EDITING";
                break;
            case "Read":
                arg = FileName(Str(input, "file_path"));
                pet = "reading";
                verb = "READING";
                break;
            case "Grep":
            case "Glob":
                arg = Str(input, "pattern");
                pet = "detective";
                verb = "SEARCHING";
                break;
            case "WebFetch":
                Uri uri;
                arg = Uri.TryCreate(Str(input, "url"), UriKind.Absolute, out uri) ? uri.Host : "";
                pet = "telescope";
                verb = "BROWSING";
                break;
            case "WebSearch":
                arg = Str(input, "query");
                pet = "telescope";
                verb = "SEARCHING";
                break;
            case "Task":
            case "Agent":
                arg = Str(input, "description");
                pet = "working-juggling";
                verb = "DELEGATING";
                break;
            default:
                arg = "";
                pet = "working-tool-calling";
                verb = "WORKING";
                break;
        }
        // a plain command or tool gets a pet from how the turn is going
        if (pet == "working-typing" || pet == "working-tool-calling")
        {
            if (s.Context >= 85)
                pet = "working-overheated";
            else if (s.PromptAt >= 0 && now - s.PromptAt > 10 * 60000)
                pet = "working-oncall";
            else if (s.Tools > 15)
                pet = "working-juggling";
        }
        // mcp__server__tool reads as just "tool"
        string label = tool.StartsWith("mcp__") ? tool.Substring(tool.LastIndexOf("__", StringComparison.Ordinal) + 2) : tool;
        arg = OneLine(arg);
        Set(s, Phase.Working, pet, verb, arg.Length > 0 ? label + " · " + arg : label, now);
    }

    // which pet acts a shell command out, by what the command does
    static string BashPet(string c)
    {
        if (Regex.IsMatch(c, @"\btest|\bpytest|\bjest|\bvitest")) return "working-testing";
        if (c.Contains("git push")) return "working-pushing";
        if (c.Contains("git merge") || c.Contains("git rebase")) return "working-merging";
        if (Regex.IsMatch(c, @"\b(npm|pnpm|yarn|bun) (i|install|add)\b|\bpip install|\bcargo add|\bdotnet add|\bwinget install")) return "working-carrying";
        if (Regex.IsMatch(c, @"\bbuild\b|\bmake\b|\bcargo (build|run)|\bdotnet (build|run)|\bcsc\b|\btsc\b|\bgradle|\bmvn\b|\.cmd\b")) return "working-building";
        if (Regex.IsMatch(c, @"\blint|\bformat\b|\bprettier|\beslint|\brm -|\bremove-item|\bclean\b")) return "working-sweeping";
        if (Regex.IsMatch(c, @"\bdeploy|\bdocker\b|\bkubectl|\bgh release")) return "working-deploying";
        if (Regex.IsMatch(c, @"^\s*(grep|rg|ls|cat|find|head|tail|sed -n|wc|tree|dir|get-childitem|select-string)\b|^\s*git (log|status|diff|show)\b")) return "detective";
        if (Regex.IsMatch(c, @"^\s*(curl|wget|gh|invoke-webrequest)\b")) return "telescope";
        return "working-typing";
    }

    static string ErrorPet(string error)
    {
        switch (error)
        {
            case "rate_limit": return "429";
            case "overloaded": return "503";
            case "server_error": return "500";
            case "authentication_failed":
            case "oauth_org_not_allowed": return "401";
            default: return "error";
        }
    }

    static string ErrorTitle(string error)
    {
        switch (error)
        {
            case "rate_limit": return "RATE LIMIT";
            case "overloaded": return "OVERLOADED";
            case "billing_error":
            case "account_on_hold": return "BILLING";
            case "authentication_failed":
            case "oauth_org_not_allowed": return "SIGNED OUT";
            default: return "ERROR";
        }
    }

    // Takes the status line's JSON (quota, model, context) and returns the line Claude Code prints.
    public string StatusLine(Dictionary<string, object> e)
    {
        double used5h = Num(e, "rate_limits", "five_hour", "used_percentage"), used7d = Num(e, "rate_limits", "seven_day", "used_percentage");
        lock (sync)
        {
            statusAt = DateTime.UtcNow;
            statusHadLimits = e.ContainsKey("rate_limits");
            Session s;
            double used = Num(e, "context_window", "used_percentage");
            if (!double.IsNaN(used) && sessions.TryGetValue(Str(e, "session_id"), out s))
                s.Context = used;
            if (!double.IsNaN(used5h))
            {
                quota5h = used5h;
                reset5h = Num(e, "rate_limits", "five_hour", "resets_at");
            }
            if (!double.IsNaN(used7d))
            {
                quota7d = used7d;
                reset7d = Num(e, "rate_limits", "seven_day", "resets_at");
            }
        }

        List<string> parts = new List<string>();
        string model = Str(e, "model", "display_name");
        if (model.Length > 0)
            parts.Add(model);
        double ctx = Num(e, "context_window", "used_percentage");
        if (!double.IsNaN(ctx))
            parts.Add("ctx " + Math.Round(ctx) + "%");
        if (!double.IsNaN(used5h))
            parts.Add("5h " + Math.Round(used5h) + "%");
        if (!double.IsNaN(used7d))
            parts.Add("7d " + Math.Round(used7d) + "%");
        return string.Join(" · ", parts.ToArray());
    }

    // The most urgent session that has something going on, or null when the keyboard screen should stay.
    public ClaudeView Current(double now)
    {
        lock (sync)
        {
            Session best = null;
            int bestRank = 0, busy = 0;
            List<string> gone = new List<string>();
            foreach (Session s in sessions.Values)
            {
                if (now - s.LastAt > ForgetMs)
                {
                    gone.Add(s.Id);
                    continue;
                }
                int rank = Rank(s, now);
                if (rank == 0)
                    continue;
                busy++;
                if (rank > bestRank || (rank == bestRank && s.LastAt > best.LastAt))
                {
                    best = s;
                    bestRank = rank;
                }
            }
            foreach (string id in gone)
                sessions.Remove(id);
            return best == null ? null : View(best, now, busy - 1);
        }
    }

    static int Rank(Session s, double now)
    {
        switch (s.Phase)
        {
            case Phase.NeedsYou: return now - s.LastAt < WaitMs ? 5 : 0;
            case Phase.Error: return now - s.PhaseAt < ShowDoneMs ? 4 : 0;
            case Phase.Working:
            case Phase.Thinking: return now - s.LastAt < StaleMs ? 3 : 0;
            case Phase.Done: return now - s.PhaseAt < ShowDoneMs ? 2 : 0;
            default: return 0;
        }
    }

    ClaudeView View(Session s, double now, int others)
    {
        ClaudeView v = new ClaudeView
        {
            Header = (s.Project.Length > 0 ? s.Project : "Claude Code") + (others > 0 ? "  +" + others : ""),
            Pet = s.Pet,
            PetStart = s.PetAt,
            State = s.State,
            Detail = s.Detail,
        };
        switch (s.Phase)
        {
            case Phase.Thinking:
            case Phase.Working:
                v.Sub = (s.PromptAt >= 0 ? Clock(now - s.PromptAt) + " · " : "") + Count(s.Tools);
                break;
            case Phase.NeedsYou:
                v.StateColor = ClaudeView.Clay;
                v.Sub = "waiting " + Clock(now - s.PhaseAt);
                break;
            case Phase.Done:
                if (now - s.PetAt > CelebrateMs)
                {
                    v.Pet = "happy";
                    v.PetStart = s.PetAt + CelebrateMs;
                }
                v.Detail = (s.TurnMs > 0 ? Duration(s.TurnMs) + " · " : "") + Count(s.Tools);
                v.Sub = "your turn";
                break;
            case Phase.Error:
                v.StateColor = ClaudeView.Red;
                v.Sub = s.Error;
                break;
        }

        Quotas(v);
        return v;
    }

    // The page for when nothing is going on: a sleeping pet, the open sessions and the quota.
    public ClaudeView Idle(double now)
    {
        lock (sync)
        {
            Session last = null;
            foreach (Session s in sessions.Values)
                if (last == null || s.LastAt > last.LastAt)
                    last = s;
            ClaudeView v = new ClaudeView
            {
                Header = last != null && last.Project.Length > 0 ? last.Project : "Claude Code",
                Pet = "sleeping",
                State = "IDLE",
                Detail = sessions.Count == 0 ? "no session" : sessions.Count == 1 ? "1 session open" : sessions.Count + " sessions open",
                Sub = last != null ? "active " + Ago(now - last.LastAt) + " ago" : "",
            };
            Quotas(v);
            return v;
        }
    }

    // % used of the 5-hour and 7-day windows, -1 when unknown (for other screens)
    public void Quota(out int fiveHour, out int sevenDay)
    {
        ClaudeView v = new ClaudeView();
        lock (sync)
            Quotas(v);
        fiveHour = v.Quota5h;
        sevenDay = v.Quota7d;
    }

    // a window whose reset time has passed no longer says anything
    void Quotas(ClaudeView v)
    {
        double unix = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        v.Quota5h = quota5h >= 0 && !(reset5h > 0 && unix > reset5h) ? (int)Math.Round(quota5h) : -1;
        v.Quota7d = quota7d >= 0 && !(reset7d > 0 && unix > reset7d) ? (int)Math.Round(quota7d) : -1;
    }

    // 40s, 18m, 3h
    static string Ago(double ms)
    {
        TimeSpan t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? (int)t.TotalHours + "h" : t.TotalMinutes >= 1 ? (int)t.TotalMinutes + "m" : (int)t.TotalSeconds + "s";
    }

    static string Count(int tools)
    {
        return tools == 1 ? "1 tool" : tools + " tools";
    }

    // 2:05, or 1:02:05 past an hour
    static string Clock(double ms)
    {
        TimeSpan t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
            : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
    }

    // 45s, 3m 12s, 1h 05m
    static string Duration(double ms)
    {
        TimeSpan t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (t.TotalHours >= 1)
            return string.Format("{0}h {1:00}m", (int)t.TotalHours, t.Minutes);
        if (t.TotalMinutes >= 1)
            return string.Format("{0}m {1}s", t.Minutes, t.Seconds);
        return t.Seconds + "s";
    }

    static string FileName(string path)
    {
        path = path.TrimEnd('/', '\\');
        return path.Substring(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1);
    }

    // first line, spaces collapsed, cut short: enough to recognize, little enough to leak
    static string OneLine(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 80 ? s.Substring(0, 80) : s;
    }

    static object Get(Dictionary<string, object> d, string[] path)
    {
        object o = d;
        foreach (string key in path)
        {
            Dictionary<string, object> m = o as Dictionary<string, object>;
            if (m == null || !m.TryGetValue(key, out o))
                return null;
        }
        return o;
    }

    static string Str(Dictionary<string, object> d, params string[] path)
    {
        return d == null ? "" : Get(d, path) as string ?? "";
    }

    static double Num(Dictionary<string, object> d, params string[] path)
    {
        object o = d == null ? null : Get(d, path);
        if (o is int || o is long || o is decimal || o is double)
            return Convert.ToDouble(o, CultureInfo.InvariantCulture);
        return double.NaN;
    }
}
