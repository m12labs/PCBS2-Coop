using HarmonyLib;

namespace PCBS2Coop;

/// <summary>Co-op save barriers, paired-load preparation, and native object binding hooks. Solo paths pass through.</summary>
internal static class GamePatches
{
    public static bool Ready { get; private set; }
    public static void Apply()
    {
        try
        {
            new Harmony(Plugin.Guid + ".game").PatchAll(typeof(SaveHook));
            new Harmony(Plugin.Guid + ".objects").PatchAll(typeof(NativeLoadHook));
            new Harmony(Plugin.Guid + ".object-save").PatchAll(typeof(ComputerSaveHook));
            new Harmony(Plugin.Guid + ".object-loading").PatchAll(typeof(ComputerLoadingHook));
            new Harmony(Plugin.Guid + ".object-loaded").PatchAll(typeof(ComputerLoadedHook));
            var activity = new Harmony(Plugin.Guid + ".activity");
            activity.PatchAll(typeof(PickupGuard));
            activity.PatchAll(typeof(DestroyGuard));
            activity.PatchAll(typeof(RetrieveGuard));
            var lifecycle = new Harmony(Plugin.Guid + ".lifecycle");
            lifecycle.PatchAll(typeof(NativePcLifecycle.SpawnIntercept));
            lifecycle.PatchAll(typeof(NativePcLifecycle.CarriedDeleteIntercept));
            lifecycle.PatchAll(typeof(NativePcLifecycle.StoredDeleteIntercept));
            var transfers = new Harmony(Plugin.Guid + ".transfers");
            transfers.PatchAll(typeof(NativePcTransfers.BenchPickupIntercept));
            transfers.PatchAll(typeof(NativePcTransfers.ToolPickupIntercept));
            transfers.PatchAll(typeof(NativePcTransfers.DropIntercept));
            transfers.PatchAll(typeof(NativePcTransfers.StoreIntercept));
            transfers.PatchAll(typeof(NativePcTransfers.RetrieveIntercept));
            transfers.PatchAll(typeof(NativeProjection.AutosaveDeferral));
            new Harmony(Plugin.Guid + ".workstations").PatchAll(typeof(NativeWorkstations.SwapIntercept));
            new Harmony(Plugin.Guid + ".assembly-preview").PatchAll(typeof(NativeAssembly.PreviewTrace));
            var latches = new Harmony(Plugin.Guid + ".latches");
            latches.PatchAll(typeof(NativeMechanisms.GestureScope));
            latches.PatchAll(typeof(NativeMechanisms.ToggleIntercept));
            var panels = new Harmony(Plugin.Guid + ".panels");
            panels.PatchAll(typeof(NativeCaseAccess.RemovalIntercept));
            panels.PatchAll(typeof(NativeCaseAccess.PipeRemovalIntercept));
            panels.PatchAll(typeof(NativeCaseAccess.InstallIntercept));
            panels.PatchAll(typeof(NativeCaseAccess.SelectionIntercept));
            var replay = new Harmony(Plugin.Guid + ".edit-replay");
            replay.PatchAll(typeof(NativeEditReplay.PreviousGuard));
            replay.PatchAll(typeof(NativeEditReplay.LastDifferentGuard));
            replay.PatchAll(typeof(NativeEditReplay.EnterGuard));
            replay.PatchAll(typeof(NativeEditReplay.PreviousStateGuard));
            replay.PatchAll(typeof(NativeEditReplay.InputGuard));
            replay.PatchAll(typeof(NativeEditReplay.OrientationGuard));
            replay.PatchAll(typeof(NativeEditReplay.SelectableGuard));
            var edits = new Harmony(Plugin.Guid + ".edits");
            edits.PatchAll(typeof(NativeComponentEdits.EnterTrace));
            edits.PatchAll(typeof(NativeComponentEdits.SettleTrace));
            edits.PatchAll(typeof(NativeComponentEdits.RemovalTrace));
            var cables = new Harmony(Plugin.Guid + ".cables");
            cables.PatchAll(typeof(NativeComponentEdits.ConnectIntercept));
            cables.PatchAll(typeof(NativeComponentEdits.UnplugIntercept));
            cables.PatchAll(typeof(NativeComponentEdits.PowerButtonIntercept));
            cables.PatchAll(typeof(NativeComponentEdits.PowerClickIntercept));
            foreach (var method in Harmony.GetAllPatchedMethods())
                if (FoldedWith(method) is { } other)
                    Plugin.Logger.LogError($"[hooks] {method.DeclaringType?.Name}.{method.Name} and {other.DeclaringType?.Name}.{other.Name} are one native function hooked twice; running it may crash the game.");
            Ready = true;
            Plugin.Logger.LogInfo("Game hooks installed (save, objects, activity, PC lifecycle, transfers, peripherals, latches, case panels, edit replay, component edits, cables and power).");
        }
        catch (Exception e) { Plugin.Logger.LogError("Game hooks failed: " + e); }
    }

    /// <summary>The native code a generated IL2CPP method runs, or zero for a managed method. The game's linker merges
    /// identical method bodies into one function (the keyboard, mouse, mouse pad and headset <c>SwapPerif</c> are one).
    /// A second hook on the same code crashes the game in the runtime when the original runs (3g, G2), so hook each
    /// native function through one method only.</summary>
    internal static IntPtr NativeCode(System.Reflection.MethodBase method)
    {
        var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
        var info = field?.GetValue(null) is IntPtr p ? p : IntPtr.Zero;
        // Il2CppMethodInfo starts with its methodPointer.
        return info == IntPtr.Zero ? IntPtr.Zero : System.Runtime.InteropServices.Marshal.ReadIntPtr(info);
    }

    /// <summary>Another already patched method that runs the same native code, or null.</summary>
    internal static System.Reflection.MethodBase FoldedWith(System.Reflection.MethodBase method)
    {
        var code = NativeCode(method);
        return code == IntPtr.Zero ? null : Harmony.GetAllPatchedMethods().FirstOrDefault(m => m != method && NativeCode(m) == code);
    }

    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.PickUpCase))]
    private static class PickupGuard
    {
        private static bool Prefix(Case caseC) => NativeActivity.Allow(CoopController.Instance?.Objects.IdOf(caseC) ?? default);
    }
    // Bench pickup (BenchSlot.OnPickupPC, outermost because it clears its slot even when PickUpCase is refused), drop/swap,
    // store and retrieval are host-ordered transfers (NativePcTransfers) whose validation includes the movement check.
    // The guards here remain for native paths outside those gestures.
    [HarmonyPatch(typeof(WorkshopController), nameof(WorkshopController.DestroyCarriedCase))]
    private static class DestroyGuard { private static bool Prefix() => NativeActivity.Allow(NativeActivity.Carried()); }
    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.PickupComputerFromStorage))]
    private static class RetrieveGuard
    { private static bool Prefix(ComputerSave computer) => NativeActivity.Allow(CoopController.Instance?.Objects.IdOf(computer) ?? default); }
    /// <summary>Every game save (manual, auto, Free Build) goes through CreateSaveGame. The host sends its saves to the guest as copies.</summary>
    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.CreateSaveGame), typeof(string), typeof(bool), typeof(string))]
    private static class SaveHook
    {
        private static bool Prefix() => CoopController.Instance?.BeforeHostSave() ?? true;
        private static void Postfix(string __result)
        {
            try
            {
                if (CoopController.TakingSnapshot || CoopSaves.Current != CoopSaves.Mode.Host) return;
                CoopController.Instance?.OnHostSaved(__result);
            }
            catch (Exception e) { Plugin.Logger.LogError(e); }
        }
    }

    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.LoadGame), typeof(string), typeof(SaveGameHeader), typeof(SaveGame))]
    private static class NativeLoadHook
    {
        private static bool Prefix(SaveGame s) => CoopController.Instance?.PrepareNativeLoad(s) ?? true;
    }

    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.SaveComputer))]
    private static class ComputerSaveHook
    {
        private static void Postfix(Case caseToSave, ComputerSave __result) => CoopController.Instance?.Objects.SavedComputer(caseToSave, __result);
    }

    [HarmonyPatch(typeof(PartsDatabase), nameof(PartsDatabase.InitComputerLoading))]
    private static class ComputerLoadingHook
    {
        private static void Prefix(ComputerSave CompSave, ref UnityEngine.Events.UnityAction<bool, Case, BenchSlot> OnSpawned)
        {
            var objects = CoopController.Instance?.Objects;
            if (objects != null) OnSpawned = objects.WrapSpawn(CompSave, OnSpawned);
        }
    }

    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.LoadComputerToSlotAsync))]
    private static class ComputerLoadedHook
    {
        private static bool Prefix(Case Case, PartsDatabase.LoadComputerOperation LoadPC) => CoopController.Instance?.Objects.AllowLoadCompletion(Case, LoadPC) ?? true;
        private static void Postfix(Case Case, PartsDatabase.LoadComputerOperation LoadPC) => CoopController.Instance?.Objects.CompletedLoad(Case, LoadPC);
    }
}
