namespace PCBS2Coop;

/// <summary>Native calls made by an accepted change (inside WorldSync's Apply). Interception and the movement guards
/// let them through. The native Free Build autosave such a call would trigger (a PC dropped on a bench) is skipped
/// in co-op, like every game autosave (<see cref="CoopController.AllowGameAutosave"/>), so no save runs inside Apply.</summary>
internal static class NativeProjection
{
    public static bool Active { get; private set; }

    public static void Run(Action call) => Run(() => { call(); return true; });

    public static T Run<T>(Func<T> call)
    {
        var outer = Active;
        Active = true;
        try { return call(); }
        finally { Active = outer; }
    }
}
