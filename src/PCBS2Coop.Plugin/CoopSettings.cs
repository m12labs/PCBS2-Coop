using BepInEx.Configuration;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>Co-op menu choices remembered in the BepInEx config: the last IP and port joined and the LAN port to host on.
/// The self tests always use 127.0.0.1 and the default port and never change the owner's settings.</summary>
internal static class CoopSettings
{
    private static ConfigEntry<string> _lastIp;
    private static ConfigEntry<int> _port;
    private static ConfigEntry<int> _joinPort;

    private static bool Fixed => AutoTest.IsEnabled || SteamProbe.Enabled;

    public static void Init(ConfigFile config)
    {
        _lastIp = config.Bind("Connection", "LastIp", "127.0.0.1", "The host IP address last used in Join a Friend.");
        _port = config.Bind("Connection", "Port", Protocol.DefaultPort, "LAN port this PC hosts on (1-65535).");
        _joinPort = config.Bind("Connection", "LastJoinPort", Protocol.DefaultPort, "The host port last used in Join a Friend.");
    }

    public static string LastIp
    {
        get => Fixed ? "127.0.0.1" : _lastIp?.Value ?? "127.0.0.1";
        set { if (_lastIp != null && !Fixed) _lastIp.Value = value; }
    }

    /// <summary>The LAN port this PC hosts on (Co-op Options).</summary>
    public static int Port
    {
        get => Fixed || _port == null || _port.Value is < 1 or > 65535 ? Protocol.DefaultPort : _port.Value;
        set { if (_port != null && !Fixed) _port.Value = value; }
    }

    public static int LastJoinPort
    {
        get => Fixed || _joinPort == null || _joinPort.Value is < 1 or > 65535 ? Protocol.DefaultPort : _joinPort.Value;
        set { if (_joinPort != null && !Fixed) _joinPort.Value = value; }
    }
}
