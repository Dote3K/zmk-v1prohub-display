using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// A tiny HTTP endpoint on 127.0.0.1 for Claude Code. Hooks POST their event JSON to /hook and the status
// line command POSTs its JSON to /statusline. Hooks get 200 with an empty body, which Claude Code reads as
// "success, no output", so nothing here can approve, deny or block anything. /statusline answers with
// the text the status line should print.
sealed class ClaudeServer
{
    public const int Port = 47811;
    const int MaxHeader = 16 << 10, MaxBody = 8 << 20;

    // path ("/hook", "/statusline") and parsed body; returns the response text (null = empty)
    readonly Func<string, Dictionary<string, object>, string> handle;

    public ClaudeServer(Func<string, Dictionary<string, object>, string> handle)
    {
        this.handle = handle;
    }

    // Returns false when the port is taken; the app then simply runs without the Claude screen.
    public bool Start()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            return false;
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
                string method, path, error;
                Dictionary<string, object> body = Read(net, out method, out path, out error);
                if (error != null)
                {
                    Reply(net, error, null);
                    return;
                }
                string text = null;
                try
                {
                    text = handle(path, body);
                }
                catch (Exception e)
                {
                    // a bad event must not take the app down; the hook still gets its empty 200
                    System.Diagnostics.Debug.WriteLine("claude event: " + e);
                }
                Reply(net, "200 OK", text);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (FormatException) { }   // bad chunk size
            catch (OverflowException) { }
        }
    }

    // Parses one request. Only JSON POSTs without an Origin header are taken: a web page in a browser can
    // not send those to localhost without a CORS preflight, which is refused, so sites cannot fake events.
    static Dictionary<string, object> Read(Stream net, out string method, out string path, out string error)
    {
        method = path = null;
        string head = ReadHead(net);
        if (head == null)
        {
            error = "400 Bad Request";
            return null;
        }
        string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length < 3)
        {
            error = "400 Bad Request";
            return null;
        }
        method = first[0];
        path = first[1];

        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
        }

        string value;
        if (method != "POST" || (path != "/hook" && path != "/statusline"))
        {
            error = "404 Not Found";
            return null;
        }
        if (headers.ContainsKey("Origin") ||
            !headers.TryGetValue("Content-Type", out value) || !value.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            error = "403 Forbidden";
            return null;
        }
        if (headers.TryGetValue("Expect", out value) && value.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            Write(net, "HTTP/1.1 100 Continue\r\n\r\n");

        byte[] data;
        if (headers.TryGetValue("Transfer-Encoding", out value) && value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            data = ReadChunked(net);
        else
        {
            int length;
            if (!headers.TryGetValue("Content-Length", out value) || !int.TryParse(value, out length) || length < 0 || length > MaxBody)
            {
                error = "411 Length Required";
                return null;
            }
            data = ReadExactly(net, length);
        }
        if (data == null)
        {
            error = "413 Payload Too Large";
            return null;
        }

        try
        {
            JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaxBody };
            Dictionary<string, object> parsed = json.DeserializeObject(Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
            error = parsed == null ? "400 Bad Request" : null;
            return parsed;
        }
        catch (ArgumentException)
        {
            error = "400 Bad Request";
            return null;
        }
        catch (InvalidOperationException)
        {
            error = "400 Bad Request";
            return null;
        }
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

    static void Reply(Stream net, string status, string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text ?? "");
        Write(net, "HTTP/1.1 " + status + "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length +
            "\r\nConnection: close\r\n\r\n");
        net.Write(body, 0, body.Length);
    }

    static void Write(Stream net, string ascii)
    {
        byte[] b = Encoding.ASCII.GetBytes(ascii);
        net.Write(b, 0, b.Length);
    }
}
