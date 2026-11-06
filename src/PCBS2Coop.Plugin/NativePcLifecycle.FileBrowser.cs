using HarmonyLib;
using StoredComputers = Il2CppSystem.Collections.Generic.List<ComputerSave>;

namespace PCBS2Coop;

internal sealed partial class NativePcLifecycle
{
    private sealed class FileLoad(uint generation, HashSet<IntPtr> existingDesigns)
    {
        public readonly uint Generation = generation;
        public readonly HashSet<IntPtr> ExistingDesigns = existingDesigns;
    }

    private FileLoad _openingFile;
    private readonly Dictionary<IntPtr, (ComputerSave Record, CustomCaseSaveContainer Design)> _filePcs = new();

    private void StageFile(ComputerSave record, HashSet<IntPtr> existingDesigns)
    {
        var design = NativeToolBenches.DesignOf(record);
        if (design != null && !existingDesigns.Contains(design.Pointer)) CustomCaseManager.Get().CustomisationList.Remove(design);
        else design = null;
        _filePcs.Add(record.Pointer, (record, design));
    }

    private void ConfirmFile(FileLoad load, Il2CppSystem.Action nativeYes)
    {
        if (Current != this || !Active || _world.Generation != load.Generation) return;
        var previous = _openingFile;
        _openingFile = load;
        try { nativeYes.Invoke(); }
        finally { _openingFile = previous; }
    }

    // Both file formats reach the native AddComputerToStorage after file validation and design regeneration.
    // Mark the file path so the same storage method still works normally for stores and loads.
    [HarmonyPatch(typeof(Storage), nameof(Storage.ImportPcFromFilePath))]
    internal static class FileOpenHook
    {
        private static bool Prefix(out NativePcLifecycle __state)
        {
            __state = null;
            if (!InCoopWorkshop || Projecting) return true;
            var owner = Current;
            if (owner?.Active != true || owner._openingFile != null)
            {
                CoopController.Instance.Notice("Import refused: the shared cupboard isn't ready yet.");
                return false;
            }
            var designs = CustomCaseManager.Get()?.CustomisationList;
            var existing = new HashSet<IntPtr>();
            if (designs != null) foreach (var design in designs) if (design != null) existing.Add(design.Pointer);
            owner._openingFile = new FileLoad(owner._world.Generation, existing);
            __state = owner;
            return true;
        }

        private static void Finalizer(NativePcLifecycle __state) { if (__state != null) __state._openingFile = null; }
    }

    // The native duplicate-confirmation callback closes over the imported record and prepares its design later.
    // Wrap that one callback at the dialog boundary so both native file formats can share the same hook.
    [HarmonyPatch(typeof(MessageBox), nameof(MessageBox.ShowWithoutStateChange), typeof(string), typeof(string), typeof(Il2CppSystem.Action),
        typeof(bool), typeof(Il2CppSystem.Action), typeof(Rewired.ActionElementMap), typeof(bool), typeof(bool),
        typeof(MessageBox.ImmediateMessageBoxHolder), typeof(MessageBox.ImmediateMessageBoxHolder))]
    internal static class FileDuplicateHook
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ref Il2CppSystem.Action yesAction, bool twoButtons, MessageBox.ImmediateMessageBoxHolder immediateMessageOnYes)
        {
            var owner = Current;
            var load = owner?._openingFile;
            if (!twoButtons || immediateMessageOnYes == null || yesAction == null || load == null) return;
            var nativeYes = yesAction;
            yesAction = (Il2CppSystem.Action)new Action(() => owner.ConfirmFile(load, nativeYes));
        }
    }

    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.AddComputerToStorage), typeof(ComputerSave))]
    internal static class StageFileHook
    {
        private static bool Prefix(SaveLoadSystem __instance, ComputerSave computer, ref int __result)
        {
            var owner = Current;
            if (Projecting || owner?._openingFile is not { } load) return true;
            owner.StageFile(computer, load.ExistingDesigns);
            __result = __instance.localSaveGame.computersInStorage.Count + owner._filePcs.Count - 1;
            return false;
        }
    }

    // Give the native cupboard its local cards only while it builds the view. The StoredPC components keep their
    // record references, so the existing file-PC pickup path can recognize a card outside the shared list.
    [HarmonyPatch(typeof(Storage), nameof(Storage.Populate))]
    internal static class FileRowsHook
    {
        private static void Prefix(out (StoredComputers Stored, List<ComputerSave> Added) __state)
        {
            __state = default;
            var owner = Current;
            if (!InCoopWorkshop || owner == null || owner._filePcs.Count == 0) return;
            var stored = Saves.localSaveGame?.computersInStorage;
            if (stored == null) return;
            __state = (stored, new List<ComputerSave>());
            foreach (var file in owner._filePcs.Values)
            {
                if (stored.Contains(file.Record)) continue;
                stored.Add(file.Record);
                __state.Added.Add(file.Record);
            }
        }

        private static void Finalizer((StoredComputers Stored, List<ComputerSave> Added) __state)
        {
            if (__state.Added == null) return;
            foreach (var record in __state.Added) __state.Stored.Remove(record);
        }
    }
}
