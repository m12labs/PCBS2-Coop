using System.Diagnostics;
using PCBS2Coop.Network;

internal sealed class Checker
{
    private int _failures, _passes;

    public void Section(string name) => Console.WriteLine($"\n== {name} ==");

    public void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (ok) _passes++; else _failures++;
    }

    public int Finish()
    {
        Console.WriteLine(_failures == 0 ? $"\nAll {_passes} checks passed." : $"\n{_failures} check(s) failed, {_passes} passed.");
        return _failures == 0 ? 0 : 1;
    }

    public static bool PumpUntil(Func<bool> done, int timeoutMs, params CoopSession[] sessions)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var s in sessions) s.Poll();
            if (done()) return true;
            Thread.Sleep(5);
        }
        return false;
    }
}
