using System.Reflection;
using System.Runtime.Loader;
using BepInEx.Configuration;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>Keep Steam types out of the plugin's load path: missing dependencies leave LAN usable.</summary>
internal static class SteamBootstrap
{
    internal static ISteamService Service { get; private set; }
    internal static string Reason { get; private set; } = "Steam is disabled.";
    private static ConfigEntry<string> _method;
    private static string _directory;
    private static Assembly ResolveSteam(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is not ("Facepunch.Steamworks.Win64" or "PCBS2Coop.Steam")) return null;
        var file = Path.Combine(_directory, name.Name + ".dll");
        return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
    }
    /// <summary>The player chose Steam in Co-op Options (it may still be unavailable).</summary>
    internal static bool WantsSteam => !AutoTest.IsEnabled && (SteamProbe.Enabled || _method?.Value != "LAN");
    internal static bool PreferSteam
    {
        get => !AutoTest.IsEnabled && Service?.Available == true && (SteamProbe.Enabled || _method?.Value != "LAN");
        set { if (_method != null && !AutoTest.IsEnabled && !SteamProbe.Enabled) _method.Value = value ? "Steam" : "LAN"; }
    }
    internal static void Init(ConfigFile config)
    {
        var enabled = config.Bind("Steam", "Enabled", true, "Enable Steam invites and P2P. Restart the game after changing.");
        var appId = config.Bind("Steam", "AppId", 480u, "Steam App ID. 480 is Spacewar for initial testing. Restart after changing; both peers must match.");
        _method = config.Bind("Connection", "Method", "Steam", "Preferred connection method: Steam or LAN.");
        if (!enabled.Value || AutoTest.IsEnabled) { Reason = AutoTest.IsEnabled ? "Steam is disabled during the two-instance LAN self-test." : "Steam is disabled in configuration."; return; }
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "steam");
            _directory = directory;
            AssemblyLoadContext.Default.Resolving += ResolveSteam;
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "PCBS2Coop.Steam.dll"));
            var type = assembly.GetType("PCBS2Coop.Steam.SteamService", throwOnError: true);
            Service = (ISteamService)Activator.CreateInstance(type, appId.Value, directory, new Action<string>(message => Plugin.Logger.LogInfo("[steam] " + message)));
            Reason = Service.UnavailableReason;
        }
        catch (Exception e)
        {
            Reason = "Steam dependencies could not be loaded. LAN is available. " + (e.InnerException?.Message ?? e.Message);
            Plugin.Logger.LogWarning(Reason);
        }
    }
}
