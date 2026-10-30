using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

[BepInPlugin(Guid, Name, Network.ModVersion.Value)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "com.macery12.pcbs2coop";
    public const string Name = "PCBS2 Coop";

    internal static ManualLogSource Logger { get; private set; }

    public override void Load()
    {
        Logger = Log;
        GameInfo.Init(Config);
        SteamBootstrap.Init(Config);
        CoopSettings.Init(Config);
        GamePatches.Apply();
        ClassInjector.RegisterTypeInIl2Cpp<CoopRunner>();
        var host = new GameObject("PCBS2Coop");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<CoopRunner>();
        Log.LogInfo($"{Name} {Network.ModVersion.Value} loaded.");
    }
}
