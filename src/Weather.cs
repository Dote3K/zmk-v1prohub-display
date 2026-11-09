using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

// The weather where the user is, right now.
sealed class WeatherNow
{
    public double Temp, High = double.NaN, Low = double.NaN;
    public int Code;              // WMO weather code
    public bool IsDay = true;
    public int RainChance = -1;   // % today, -1 unknown
    public DateTime At;           // when it was fetched

    // one of clear, partly, cloudy, fog, drizzle, rain, snow, storm
    public string Kind
    {
        get
        {
            if (Code <= 0) return "clear";
            if (Code <= 2) return "partly";
            if (Code == 3) return "cloudy";
            if (Code == 45 || Code == 48) return "fog";
            if (Code >= 51 && Code <= 57) return "drizzle";
            if ((Code >= 61 && Code <= 67) || (Code >= 80 && Code <= 82)) return "rain";
            if ((Code >= 71 && Code <= 77) || Code == 85 || Code == 86) return "snow";
            if (Code >= 95) return "storm";
            return "cloudy";
        }
    }

    public string Condition
    {
        get
        {
            switch (Kind)
            {
                case "clear": return IsDay ? "Sunny" : "Clear";
                case "partly": return "Partly cloudy";
                case "cloudy": return "Cloudy";
                case "fog": return "Fog";
                case "drizzle": return "Drizzle";
                case "rain": return "Rain";
                case "snow": return "Snow";
                default: return "Storm";
            }
        }
    }

    public bool Wet
    {
        get { return Kind == "drizzle" || Kind == "rain" || Kind == "storm"; }
    }
}

// A place from the geocoder.
sealed class Place
{
    public string Name = "", Region = "", Country = "";
    public double Lat, Lon;

    public string Label
    {
        get { return Name + (Region.Length > 0 && Region != Name ? ", " + Region : "") + (Country.Length > 0 ? ", " + Country : ""); }
    }
}

// Weather from Open-Meteo (free, no key): the place is looked up once by name, then the current
// conditions are fetched every 30 minutes in the background. Only the place's coordinates leave the
// machine.
static class Weather
{
    const string Screen = "clock";
    const int EveryMs = 30 * 60 * 1000, RetryMs = 5 * 60 * 1000;

    static volatile WeatherNow current;
    static volatile string problem = "";
    static readonly AutoResetEvent wake = new AutoResetEvent(false);
    static Action changed;

    public static WeatherNow Current
    {
        get { return Enabled ? current : null; }
    }

    public static bool Enabled
    {
        get { return Settings.Option(Screen, "weather", true) && Location() != null; }
    }

    public static void Start(Action onChange)
    {
        changed = onChange;
        // .NET Framework 4 would otherwise offer only TLS 1.0, which the API refuses
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        Thread t = new Thread(() =>
        {
            while (true)
            {
                int wait = EveryMs;
                Place p = Location();
                if (p != null && Settings.Option(Screen, "weather", true))
                {
                    try
                    {
                        current = Fetch(p);
                        problem = "";
                    }
                    catch (Exception e) // network, a changed API, anything: the clock just goes without weather
                    {
                        problem = e.Message;
                        wait = RetryMs;
                    }
                    if (changed != null)
                        changed();
                }
                wake.WaitOne(wait);
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // fetch now, e.g. after the place changed
    public static void Refresh()
    {
        current = null;
        wake.Set();
    }

    // for the settings window
    public static string Describe()
    {
        Place p = Location();
        if (p == null)
            return "Set a city to get the weather.";
        if (!Settings.Option(Screen, "weather", true))
            return "Off";
        WeatherNow w = current;
        if (w == null)
            return problem.Length > 0 ? "Could not get the weather: " + problem : "Getting the weather for " + p.Label + "…";
        int minutes = (int)(DateTime.Now - w.At).TotalMinutes;
        return Math.Round(w.Temp) + "° " + w.Condition + " in " + p.Name + " · updated " + (minutes < 1 ? "just now" : minutes + " min ago") +
            (problem.Length > 0 ? " (last try failed: " + problem + ")" : "");
    }

    // the chosen place, from config.json; null when none is set
    public static Place Location()
    {
        double lat, lon;
        string name = Settings.Option(Screen, "city", "");
        if (name.Length == 0 ||
            !double.TryParse(Settings.Option(Screen, "lat", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out lat) ||
            !double.TryParse(Settings.Option(Screen, "lon", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
            return null;
        return new Place { Name = name, Lat = lat, Lon = lon, Country = Settings.Option(Screen, "country", "") };
    }

    // coordinates are kept as text: JSON numbers come back from the config as decimals or ints
    public static void SetLocation(Place p)
    {
        Settings.Edit(c =>
        {
            Dictionary<string, object> o;
            if (!c.Options.TryGetValue(Screen, out o))
                c.Options[Screen] = o = new Dictionary<string, object>();
            o["city"] = p.Name;
            o["country"] = p.Country;
            o["lat"] = p.Lat.ToString("0.####", CultureInfo.InvariantCulture);
            o["lon"] = p.Lon.ToString("0.####", CultureInfo.InvariantCulture);
        });
        Refresh();
    }

    // places matching a name, best first
    public static List<Place> Search(string name)
    {
        string json = Get("https://geocoding-api.open-meteo.com/v1/search?count=6&language=en&format=json&name=" + Uri.EscapeDataString(name.Trim()));
        Dictionary<string, object> d = Json.Parse(json);
        IList results = d != null && d.ContainsKey("results") ? d["results"] as IList : null;
        List<Place> places = new List<Place>();
        if (results == null)
            return places;
        foreach (Dictionary<string, object> r in results.OfType<Dictionary<string, object>>())
            places.Add(new Place
            {
                Name = Str(r, "name"),
                Region = Str(r, "admin1"),
                Country = Str(r, "country"),
                Lat = Convert.ToDouble(r["latitude"], CultureInfo.InvariantCulture),
                Lon = Convert.ToDouble(r["longitude"], CultureInfo.InvariantCulture),
            });
        return places;
    }

    static WeatherNow Fetch(Place p)
    {
        string url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}" +
            "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            "&timezone=auto&forecast_days=1", p.Lat, p.Lon);
        Dictionary<string, object> d = Json.Parse(Get(url));
        if (d == null || !(d.ContainsKey("current") && d["current"] is Dictionary<string, object>))
            throw new FormatException("unexpected answer");
        Dictionary<string, object> now = (Dictionary<string, object>)d["current"];
        WeatherNow w = new WeatherNow
        {
            Temp = Convert.ToDouble(now["temperature_2m"], CultureInfo.InvariantCulture),
            Code = Convert.ToInt32(now["weather_code"], CultureInfo.InvariantCulture),
            IsDay = Convert.ToInt32(now["is_day"], CultureInfo.InvariantCulture) != 0,
            At = DateTime.Now,
        };
        Dictionary<string, object> daily = d.ContainsKey("daily") ? d["daily"] as Dictionary<string, object> : null;
        if (daily != null)
        {
            w.High = First(daily, "temperature_2m_max", double.NaN);
            w.Low = First(daily, "temperature_2m_min", double.NaN);
            w.RainChance = (int)First(daily, "precipitation_probability_max", -1);
        }
        return w;
    }

    static double First(Dictionary<string, object> d, string key, double fallback)
    {
        IList l = d.ContainsKey(key) ? d[key] as IList : null;
        return l != null && l.Count > 0 && l[0] != null ? Convert.ToDouble(l[0], CultureInfo.InvariantCulture) : fallback;
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        return d.ContainsKey(key) ? d[key] as string ?? "" : "";
    }

    static string Get(string url)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "zmk-hub-display";
        req.Timeout = req.ReadWriteTimeout = 10000;
        using (WebResponse res = req.GetResponse())
        using (StreamReader r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            return r.ReadToEnd();
    }
}
