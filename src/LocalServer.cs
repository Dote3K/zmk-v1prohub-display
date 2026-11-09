using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

sealed class Request
{
    public string Method, Path;
    public Dictionary<string, string> Query = new Dictionary<string, string>();
    public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public byte[] Body = new byte[0];

    public string Header(string name)
    {
        string v;
        return Headers.TryGetValue(name, out v) ? v : null;
    }

    // the body as a JSON object, or null
    public Dictionary<string, object> Json()
    {
        return global::Json.Parse(Encoding.UTF8.GetString(Body));
    }
}

sealed class Response
{
    public string Status = "200 OK";
    public string Type = "text/plain; charset=utf-8";
    public byte[] Body = new byte[0];
    public Dictionary<string, string> Headers = new Dictionary<string, string>();
    public Action<Stream> Stream;   // instead of Body: writes until it returns or the client goes away

    public static Response Text(string text)
    {
        return new Response { Body = Encoding.UTF8.GetBytes(text ?? "") };
    }

    public static Response Json(object o)
    {
        return new Response { Type = "application/json; charset=utf-8", Body = Encoding.UTF8.GetBytes(global::Json.Write(o)) };
    }

    public static Response Error(string status)
    {
        return new Response { Status = status };
    }
}

// A tiny HTTP server on 127.0.0.1 only. It parses requests and writes responses; what each path means
// and who may call it is up to the handler (Program for Claude Code's hooks, WebUi for the settings window).
sealed class LocalServer
{
    public const int Port = 47811;
    public static readonly string Origin = "http://127.0.0.1:" + Port;
    const int MaxHeader = 16 << 10, MaxBody = 8 << 20;

    readonly Func<Request, Response> handle;

    public LocalServer(Func<Request, Response> handle)
    {
        this.handle = handle;
    }

    // Returns false when the port is taken; the app then runs without hooks and settings window.
    public bool Start()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, Port);
        // right after a restart the old copy may still hold the port for a moment
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                listener.Start();
                break;
            }
            catch (SocketException)
            {
                if (attempt == 10)
                    return false;
                Thread.Sleep(300);
            }
        }
        Thread t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    ThreadPool.QueueUserWorkItem(Serve, listener.AcceptTcpClient());
                }
                catch (SocketException) { } // a client that went away mid-accept
            }
        });
        t.IsBackground = true;
        t.Start();
        return true;
    }

    void Serve(object state)
    {
        using (TcpClient client = (TcpClient)state)
        {
            client.ReceiveTimeout = client.SendTimeout = 3000;
            try
            {
                NetworkStream net = client.GetStream();
                string error;
                Request req = Read(net, out error);
                Response res;
                if (error != null)
                    res = Response.Error(error);
                else
                {
                    try
                    {
                        res = handle(req) ?? Response.Error("404 Not Found");
                    }
                    catch (Exception e)
                    {
                        // a bad request must not take the app down
                        System.Diagnostics.Debug.WriteLine("request " + req.Path + ": " + e);
                        res = Response.Error("500 Internal Server Error");
                    }
                }
                Write(net, res);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (FormatException) { }   // bad chunk size
            catch (OverflowException) { }
        }
    }

    static Request Read(Stream net, out string error)
    {
        error = null;
        string head = ReadHead(net);
        string[] lines = head != null ? head.Split(new[] { "\r\n" }, StringSplitOptions.None) : null;
        string[] first = lines != null ? lines[0].Split(' ') : null;
        if (first == null || first.Length < 3 || (first[0] != "GET" && first[0] != "POST"))
        {
            error = "400 Bad Request";
            return null;
        }

        Request req = new Request { Method = first[0] };
        string target = first[1];
        int q = target.IndexOf('?');
        req.Path = q >= 0 ? target.Substring(0, q) : target;
        if (q >= 0)
            foreach (string pair in target.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    req.Query[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
                req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
        }
        if (req.Method == "GET")
            return req;

        string value = req.Header("Expect");
        if (value != null && value.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            WriteAscii(net, "HTTP/1.1 100 Continue\r\n\r\n");
        value = req.Header("Transfer-Encoding");
        if (value != null && value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            req.Body = ReadChunked(net);
        else
        {
            int length;
            if (!int.TryParse(req.Header("Content-Length") ?? "0", out length) || length < 0 || length > MaxBody)
            {
                error = "413 Payload Too Large";
                return null;
            }
            req.Body = ReadExactly(net, length);
        }
        if (req.Body == null)
            error = "413 Payload Too Large";
        return req;
    }

    // request line and headers, up to the blank line
    static string ReadHead(Stream net)
    {
        StringBuilder sb = new StringBuilder();
        int matched = 0; // how much of \r\n\r\n has been seen
        while (sb.Length < MaxHeader)
        {
            int b = net.ReadByte();
            if (b < 0)
                return null;
            sb.Append((char)b);
            matched = b == (matched % 2 == 0 ? '\r' : '\n') ? matched + 1 : b == '\r' ? 1 : 0;
            if (matched == 4)
                return sb.ToString(0, sb.Length - 4);
        }
        return null;
    }

    static byte[] ReadExactly(Stream net, int length)
    {
        byte[] data = new byte[length];
        for (int got = 0; got < length; )
        {
            int n = net.Read(data, got, length - got);
            if (n <= 0)
                throw new IOException("connection closed mid-body");
            got += n;
        }
        return data;
    }

    static byte[] ReadChunked(Stream net)
    {
        MemoryStream body = new MemoryStream();
        while (true)
        {
            string size = ReadLine(net);
            int semi = size.IndexOf(';');
            int n = Convert.ToInt32(semi >= 0 ? size.Substring(0, semi) : size, 16);
            if (n == 0)
            {
                while (ReadLine(net).Length > 0) { } // trailers
                return body.ToArray();
            }
            if (body.Length + n > MaxBody)
                return null;
            byte[] chunk = ReadExactly(net, n);
            body.Write(chunk, 0, n);
            ReadLine(net);
        }
    }

    static string ReadLine(Stream net)
    {
        StringBuilder sb = new StringBuilder();
        while (true)
        {
            int b = net.ReadByte();
            if (b < 0)
                throw new IOException("connection closed mid-chunk");
            if (b == '\n')
                return sb.ToString().TrimEnd('\r');
            if (sb.Length > 1024)
                throw new IOException("chunk line too long");
            sb.Append((char)b);
        }
    }

    static void Write(Stream net, Response res)
    {
        // a stream has no length: its body simply ends when the connection closes
        StringBuilder head = new StringBuilder("HTTP/1.1 " + res.Status + "\r\nContent-Type: " + res.Type +
            (res.Stream == null ? "\r\nContent-Length: " + res.Body.Length : "") + "\r\nConnection: close\r\n");
        foreach (KeyValuePair<string, string> h in res.Headers)
            head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        WriteAscii(net, head.Append("\r\n").ToString());
        if (res.Stream != null)
            res.Stream(net);
        else
            net.Write(res.Body, 0, res.Body.Length);
    }

    static void WriteAscii(Stream net, string ascii)
    {
        byte[] b = Encoding.ASCII.GetBytes(ascii);
        net.Write(b, 0, b.Length);
    }
}
