using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// Reading and writing the small JSON files the app touches (its config, Claude Code's settings).
static class Json
{
    // An object, or null when the text is not a JSON object.
    public static Dictionary<string, object> Parse(string text)
    {
        try
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 << 20 }.DeserializeObject(text) as Dictionary<string, object>;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // Pretty JSON with two-space indents, the way Claude Code writes its files.
    public static string Write(object o)
    {
        return Write(o, "");
    }

    static string Write(object o, string indent)
    {
        string inner = indent + "  ";
        IDictionary<string, object> d = o as IDictionary<string, object>;
        if (d != null)
        {
            if (d.Count == 0)
                return "{}";
            return "{\n" + string.Join(",\n", d.Select(kv => inner + Quote(kv.Key) + ": " + Write(kv.Value, inner)).ToArray()) + "\n" + indent + "}";
        }
        IList l = o as IList;
        if (l != null && !(o is string))
        {
            if (l.Count == 0)
                return "[]";
            return "[\n" + string.Join(",\n", l.Cast<object>().Select(v => inner + Write(v, inner)).ToArray()) + "\n" + indent + "]";
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
}
