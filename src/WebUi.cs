using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// The settings window: one HTML page built into the exe (src/ui/settings.html), served on the local
// server and opened in an app window of the browser. The page draws itself from the Field descriptions
// (Settings.Pages), so it knows nothing about particular settings.
//
// Every request needs the token made at startup, which only the URL the tray opens carries. Together
// with the Host and Origin checks this keeps other web pages and DNS rebinding out.
sealed class WebUi
{
    readonly ScreenManager manager;
    readonly Func<byte[]> lastFrame;   // RGB565 copy of what the hub shows, null before the first frame
    readonly Func<bool> lit;
    readonly string token;

    public WebUi(ScreenManager manager, Func<byte[]> lastFrame, Func<bool> lit)
    {
        this.manager = manager;
        this.lastFrame = lastFrame;
        this.lit = lit;
        byte[] random = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            rng.GetBytes(random);
        token = string.Concat(random.Select(b => b.ToString("x2")).ToArray());
    }

    public string Url
    {
        get { return LocalServer.Origin + "/ui?t=" + token; }
    }

    // Where the running app leaves its URL, so launching the exe again can open the window. Only the
    // same Windows user can read it, and that user can change config.json directly anyway.
    static readonly string UrlFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zmk-hub-display", "settings.url");

    public void Publish()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UrlFile));
            File.WriteAllText(UrlFile, Url);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Open()
    {
        Browser.OpenApp(Url);
    }

    // from a second launch of the exe: the window of the copy that is already running
    public static void OpenRunning()
    {
        if (File.Exists(UrlFile))
            Browser.OpenApp(File.ReadAllText(UrlFile).Trim());
    }

    // null for paths that are not the settings window's
    public Response Handle(Request r)
    {
        if (r.Path != "/ui" && !r.Path.StartsWith("/api/"))
            return null;

        string host = r.Header("Host"), origin = r.Header("Origin");
        if (host != "127.0.0.1:" + LocalServer.Port && host != "localhost:" + LocalServer.Port)
            return Response.Error("403 Forbidden");
        if (origin != null && origin != LocalServer.Origin)
            return Response.Error("403 Forbidden");
        string given;
        if (!r.Query.TryGetValue("t", out given))
            given = r.Header("X-Hub-Token");
        if (!SameToken(given))
            return r.Path == "/ui" ? Page(Expired) : Response.Error("403 Forbidden");

        if (r.Method == "GET" && r.Path == "/ui")
            return Page(Html());
        if (r.Method == "GET" && r.Path == "/api/model")
            return Response.Json(Model());
        if (r.Method == "GET" && r.Path == "/api/frame.png")
            return Frame();
        if (r.Method != "POST")
            return Response.Error("405 Method Not Allowed");

        Dictionary<string, object> body = r.Json() ?? new Dictionary<string, object>();
        Func<string, string> str = k => body.ContainsKey(k) ? body[k] as string ?? "" : "";
        switch (r.Path)
        {
            case "/api/set":
                {
                    Field f = Find(str("page"), str("key"));
                    if (f == null || f.Set == null)
                        return Response.Error("404 Not Found");
                    f.Set(body.ContainsKey("value") ? body["value"] : null);
                    return Response.Json(Model());
                }
            case "/api/run":
                {
                    Field f = Find(str("page"), str("key"));
                    if (f == null || f.Run == null)
                        return Response.Error("404 Not Found");
                    string message = f.Run();
                    return Response.Json(new Dictionary<string, object> { { "message", message }, { "model", Model() } });
                }
            case "/api/preview":
                manager.Preview(str("screen"), 8000);
                return Response.Json(new Dictionary<string, object>());
        }
        return Response.Error("404 Not Found");
    }

    // compared in constant time, so the token cannot be guessed a character at a time
    bool SameToken(string given)
    {
        if (given == null || given.Length != token.Length)
            return false;
        int diff = 0;
        for (int i = 0; i < token.Length; i++)
            diff |= given[i] ^ token[i];
        return diff == 0;
    }

    static Field Find(string page, string key)
    {
        Page p = Settings.Pages().FirstOrDefault(x => x.Id == page);
        return p == null ? null : p.Fields.FirstOrDefault(f => f.Key == key);
    }

    Dictionary<string, object> Model()
    {
        Config c = manager.Config;
        double now = manager.Now;
        return new Dictionary<string, object>
        {
            { "pages", Settings.Pages().Select(p => (object)new Dictionary<string, object>
                {
                    { "id", p.Id },
                    { "title", p.Title },
                    { "icon", p.Icon },
                    { "group", p.Group },
                    { "summary", p.Summary },
                    { "screen", p.Screen },
                    // consecutive fields of one section share a card
                    { "sections", p.Fields.Where(f => f.Visible == null || f.Visible())
                        .GroupBy(f => f.Section)
                        .Select(g => (object)new Dictionary<string, object> { { "title", g.Key }, { "fields", g.Select(f => (object)FieldJson(f)).ToList() } })
                        .ToList() },
                }).ToList() },
            { "screens", manager.Screens.Select(s => (object)new Dictionary<string, object>
                {
                    { "id", s.Id },
                    { "title", s.Title },
                    { "summary", s.Summary(now) },
                    { "pinned", c.Mode == "pinned" && c.Pinned == s.Id },
                    { "rotation", c.Mode == "rotate" && c.Rotate.Contains(s.Id) },
                    { "takeover", s.CanTakeover && c.Takeover.Contains(s.Id) },
                }).ToList() },
            { "mode", c.Mode },
            { "shown", manager.ShownId },
            { "lit", lit() },
        };
    }

    static Dictionary<string, object> FieldJson(Field f)
    {
        Dictionary<string, object> d = new Dictionary<string, object>
        {
            { "key", f.Key },
            { "label", f.Label },
            { "help", f.Help },
            { "kind", f.Kind },
        };
        if (f.Get != null)
            d["value"] = f.Get();
        if (f.Choices != null)
            d["choices"] = f.Choices.Select(c => (object)new Dictionary<string, object> { { "value", c.Key }, { "label", c.Value } }).ToList();
        if (f.Kind == "number")
        {
            d["min"] = f.Min;
            d["max"] = f.Max;
            d["step"] = f.Step;
            d["unit"] = f.Unit;
        }
        return d;
    }

    // what the hub shows right now, as a PNG; X-Lit says whether the panel is on
    Response Frame()
    {
        byte[] rgb565 = lastFrame();
        using (Bitmap bmp = new Bitmap(Hub.Width, Hub.Height, PixelFormat.Format24bppRgb))
        {
            if (rgb565 != null)
            {
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, Hub.Width, Hub.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                byte[] row = new byte[data.Stride];
                for (int y = 0, i = 0; y < Hub.Height; y++)
                {
                    for (int x = 0; x < Hub.Width; x++, i += 2)
                    {
                        int v = rgb565[i] << 8 | rgb565[i + 1]; // the hub's big-endian RGB565
                        int r = v >> 11 & 31, g = v >> 5 & 63, b = v & 31;
                        row[x * 3] = (byte)(b << 3 | b >> 2);
                        row[x * 3 + 1] = (byte)(g << 2 | g >> 4);
                        row[x * 3 + 2] = (byte)(r << 3 | r >> 2);
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
                }
                bmp.UnlockBits(data);
            }
            using (MemoryStream png = new MemoryStream())
            {
                bmp.Save(png, ImageFormat.Png);
                Response res = new Response { Type = "image/png", Body = png.ToArray() };
                res.Headers["Cache-Control"] = "no-store";
                res.Headers["X-Lit"] = lit() ? "1" : "0";
                return res;
            }
        }
    }

    static Response Page(string html)
    {
        Response res = new Response { Type = "text/html; charset=utf-8", Body = Encoding.UTF8.GetBytes(html) };
        // everything the page needs is inline; nothing else may load, and it may not be framed
        res.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors 'none'";
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["Referrer-Policy"] = "no-referrer";
        return res;
    }

    const string Expired = "<!doctype html><meta charset=utf-8><title>ZMK hub display</title>" +
        "<body style='background:#111;color:#ccc;font:14px Segoe UI;display:grid;place-items:center;height:90vh'>" +
        "This window is out of date (the app restarted). Open it again from the tray icon.";

    static string html;

    static string Html()
    {
        if (html == null)
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("settings.html"))
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                html = r.ReadToEnd();
        return html;
    }
}
