using System;

// Sunrise, sunset and the moon's phase, worked out locally (the sunrise equation; accurate to a minute
// or two, which is plenty for a sky drawn on a 320x170 screen).
static class Astro
{
    static readonly DateTime J2000 = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    const double Rad = Math.PI / 180;

    // Local sunrise and sunset on that day. False in polar day or night.
    public static bool SunTimes(DateTime day, double lat, double lon, out DateTime rise, out DateTime set)
    {
        // the day count whose solar noon (n - lon/360) falls on this local noon
        double n = Math.Round((day.Date.AddHours(12).ToUniversalTime() - J2000).TotalDays + lon / 360);
        double jStar = n - lon / 360;
        double m = (357.5291 + 0.98560028 * jStar) % 360;
        double c = 1.9148 * Math.Sin(m * Rad) + 0.02 * Math.Sin(2 * m * Rad) + 0.0003 * Math.Sin(3 * m * Rad);
        double lambda = (m + c + 180 + 102.9372) % 360;
        double transit = jStar + 0.0053 * Math.Sin(m * Rad) - 0.0069 * Math.Sin(2 * lambda * Rad);
        double sinDecl = Math.Sin(lambda * Rad) * Math.Sin(23.4397 * Rad);
        double cosDecl = Math.Cos(Math.Asin(sinDecl));
        double cosHour = (Math.Sin(-0.833 * Rad) - Math.Sin(lat * Rad) * sinDecl) / (Math.Cos(lat * Rad) * cosDecl);
        rise = set = day.Date;
        if (cosHour < -1 || cosHour > 1)
            return false;
        double hour = Math.Acos(cosHour) / Rad / 360;
        rise = J2000.AddDays(transit - hour).ToLocalTime();
        set = J2000.AddDays(transit + hour).ToLocalTime();
        return true;
    }

    // 0 new moon .. 0.5 full .. 1 new again
    public static double MoonPhase(DateTime when)
    {
        double days = (when.ToUniversalTime() - new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc)).TotalDays;
        double p = days / 29.530588853 % 1;
        return p < 0 ? p + 1 : p;
    }
}
