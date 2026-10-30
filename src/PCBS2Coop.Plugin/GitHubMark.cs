using System.Globalization;

namespace PCBS2Coop;

/// <summary>
/// The GitHub mark (Octicons "mark-github", 16×16) drawn into an alpha mask at runtime, so the plugin ships no image
/// files and needs no image loading API. Pure C#: <see cref="Coverage"/> returns one alpha value per pixel, top row first.
/// </summary>
internal static class GitHubMark
{
    private const string Path16 =
        "M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94" +
        "-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07" +
        "-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27" +
        ".68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95" +
        ".29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z";

    /// <summary>Alpha (0..1) per pixel of a <paramref name="size"/>² image, row 0 at the top, 4×4 samples per pixel.</summary>
    public static float[] Coverage(int size)
    {
        var polys = Flatten(Path16);
        const int ss = 4;
        var n = size * ss;
        var scale = n / 16.0;
        var hits = new int[size * size];
        for (var row = 0; row < n; row++)
        {
            var y = (row + 0.5) / scale;
            var winds = new List<(double X, int W)>();
            foreach (var p in polys)
                for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                {
                    var (x0, y0) = p[j]; var (x1, y1) = p[i];
                    if ((y0 <= y) == (y1 <= y)) continue;
                    winds.Add((x0 + (y - y0) / (y1 - y0) * (x1 - x0), y1 > y0 ? 1 : -1));
                }
            winds.Sort((a, b) => a.X.CompareTo(b.X));
            var w = 0;
            for (var k = 0; k < winds.Count - 1; k++)
            {
                w += winds[k].W;
                if (w == 0) continue; // nonzero fill
                var from = (int)Math.Ceiling(winds[k].X * scale - 0.5);
                var to = (int)Math.Floor(winds[k + 1].X * scale - 0.5);
                for (var c = Math.Max(0, from); c <= Math.Min(n - 1, to); c++) hits[row / ss * size + c / ss]++;
            }
        }
        var result = new float[size * size];
        for (var i = 0; i < result.Length; i++) result[i] = hits[i] / (float)(ss * ss);
        return result;
    }

    /// <summary>SVG path (M, L, H, V, C, S, A, Z; absolute and relative) to closed polygons.</summary>
    private static List<List<(double X, double Y)>> Flatten(string d)
    {
        var polys = new List<List<(double, double)>>();
        List<(double, double)> cur = null;
        double x = 0, y = 0, sx = 0, sy = 0, cx2 = 0, cy2 = 0;
        var i = 0; var cmd = ' ';
        while (true)
        {
            Skip();
            if (i >= d.Length) break;
            if (char.IsLetter(d[i])) cmd = d[i++];
            var rel = char.IsLower(cmd);
            double ox = rel ? x : 0, oy = rel ? y : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    x = ox + Num(); y = oy + Num(); sx = x; sy = y;
                    cur = new List<(double, double)> { (x, y) }; polys.Add(cur);
                    cmd = rel ? 'l' : 'L'; cx2 = x; cy2 = y;
                    break;
                case 'L': x = ox + Num(); y = oy + Num(); cur.Add((x, y)); cx2 = x; cy2 = y; break;
                case 'H': x = ox + Num(); cur.Add((x, y)); cx2 = x; cy2 = y; break;
                case 'V': y = oy + Num(); cur.Add((x, y)); cx2 = x; cy2 = y; break;
                case 'C':
                {
                    double x1 = ox + Num(), y1 = oy + Num(), x2 = ox + Num(), y2 = oy + Num(), ex = ox + Num(), ey = oy + Num();
                    Cubic(x1, y1, x2, y2, ex, ey); break;
                }
                case 'S':
                {
                    double x1 = 2 * x - cx2, y1 = 2 * y - cy2, x2 = ox + Num(), y2 = oy + Num(), ex = ox + Num(), ey = oy + Num();
                    Cubic(x1, y1, x2, y2, ex, ey); break;
                }
                case 'A':
                {
                    double rx = Num(), ry = Num(), rot = Num(); var large = Flag(); var sweep = Flag();
                    double ex = ox + Num(), ey = oy + Num();
                    Arc(rx, ry, rot, large, sweep, ex, ey); break;
                }
                case 'Z': x = sx; y = sy; cx2 = x; cy2 = y; break;
                default: throw new FormatException($"Unsupported path command '{cmd}'.");
            }
        }
        return polys;

        void Cubic(double x1, double y1, double x2, double y2, double ex, double ey)
        {
            const int steps = 12;
            for (var s = 1; s <= steps; s++)
            {
                var t = s / (double)steps; var u = 1 - t;
                cur.Add((u * u * u * x + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * ex,
                         u * u * u * y + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * ey));
            }
            cx2 = x2; cy2 = y2; x = ex; y = ey;
        }

        void Arc(double rx, double ry, double rotDeg, bool large, bool sweep, double ex, double ey)
        {
            // SVG endpoint parameterization to center parameterization (SVG 1.1 appendix F.6).
            var phi = rotDeg * Math.PI / 180; double cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (x - ex) / 2, dy = (y - ey) / 2;
            double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
            if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
            var num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            var coef = Math.Sqrt(Math.Max(0, num / (rx * rx * y1p * y1p + ry * ry * x1p * x1p))) * (large == sweep ? -1 : 1);
            double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;
            double ccx = cos * cxp - sin * cyp + (x + ex) / 2, ccy = sin * cxp + cos * cyp + (y + ey) / 2;
            var a0 = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
            var a1 = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);
            var da = a1 - a0;
            if (sweep && da < 0) da += 2 * Math.PI;
            if (!sweep && da > 0) da -= 2 * Math.PI;
            var steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(da) / (Math.PI / 32)));
            for (var s = 1; s <= steps; s++)
            {
                var a = a0 + da * s / steps;
                cur.Add((ccx + rx * Math.Cos(a) * cos - ry * Math.Sin(a) * sin, ccy + rx * Math.Cos(a) * sin + ry * Math.Sin(a) * cos));
            }
            x = ex; y = ey; cx2 = x; cy2 = y;
        }

        void Skip() { while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++; }

        bool Flag() { Skip(); return d[i++] == '1'; }

        double Num()
        {
            Skip();
            var start = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            var dot = false;
            while (i < d.Length && (char.IsDigit(d[i]) || (d[i] == '.' && !dot))) { if (d[i] == '.') dot = true; i++; }
            if (i < d.Length && (d[i] == 'e' || d[i] == 'E')) { i++; if (d[i] == '-' || d[i] == '+') i++; while (i < d.Length && char.IsDigit(d[i])) i++; }
            return double.Parse(d.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
