using System;

// A value easing from one point to another over Ms, restartable from wherever it currently is.
sealed class Tween
{
    public float From, To;
    public double Start = double.MinValue;
    public double Ms;

    public Tween(double ms)
    {
        Ms = ms;
    }

    public Tween(float value, double ms)
    {
        From = To = value;
        Ms = ms;
    }

    public float At(double now)
    {
        return From + (To - From) * Ease(Progress(now));
    }

    public bool Done(double now)
    {
        return Progress(now) >= 1;
    }

    public void Retarget(float to, double now)
    {
        From = At(now);
        To = to;
        Start = now;
    }

    float Progress(double now)
    {
        return (float)Math.Min(1, Math.Max(0, (now - Start) / Ms));
    }

    public static float Ease(float t)
    {
        float u = 1 - t;
        return 1 - u * u * u; // ease-out cubic
    }
}
