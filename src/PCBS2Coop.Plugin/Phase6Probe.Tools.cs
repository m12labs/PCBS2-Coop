using System.Security.Cryptography;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Read-only Phase 9/11 observations for the opt-in Phase 6 probe. These reports inspect native game state;
/// they never invoke a tool, change a table, or alter inventory.</summary>
internal sealed partial class Phase6Probe
{
    private bool ToolsCommand(string command)
    {
        if (DelidProbeCommand(command)) return true;
        if (command == "tools-report")
        {
            if (!Safe) return true;
            try { Event("tools-report", ToolsReport()); }
            catch (Exception e) { Event("tools-report-error", e.GetType().Name + ": " + e.Message); }
            return true;
        }

        const string carrierPrefix = "carrier-report:";
        if (command.StartsWith(carrierPrefix, StringComparison.Ordinal))
        {
            if (!Safe) return true;
            try { Event("carrier-report", CarrierReport(command[carrierPrefix.Length..])); }
            catch (Exception e) { Event("carrier-report-error", e.GetType().Name + ": " + e.Message); }
            return true;
        }

        const string pastePrefix = "paste-report:";
        if (!command.StartsWith(pastePrefix, StringComparison.Ordinal)) return false;
        if (!Safe) return true;
        try
        {
            if (!ulong.TryParse(command[pastePrefix.Length..], out var value) || value == 0)
                throw new ArgumentException("paste-report:<pcId>");
            Event("paste-report", PasteReport(new NetObjectId(value)));
        }
        catch (Exception e) { Event("paste-report-error", e.GetType().Name + ": " + e.Message); }
        return true;
    }

    private object CarrierReport(string id)
    {
        var coop = RequireGestureWorld();
        if (!ulong.TryParse(id, out var value) || value == 0) throw new ArgumentException("carrier-report:<pcId>");
        var pc = coop.Objects.Model.Pcs.SingleOrDefault(pc => pc.Id == new NetObjectId(value) && pc.Location.Place == PcPlace.Carrier)
            ?? throw new InvalidOperationException("The selected PC is not being carried.");
        var record = coop.Objects.CaptureRecord(pc.Id) ?? throw new InvalidOperationException("The carried PC has no native record.");
        string profileError = null;
        try { NativeAssembly.CheckProfile(pc, record); }
        catch (InvalidDataException e) { profileError = e.Message; }
        return new
        {
            pc = value, owner = pc.Location.Actor.ToString(), role = coop.Session.Role.ToString(),
            profile = pc.BuildProfile?.Signature, profileError, pciCovers = record.casePciCovers?.ToArray(),
            bespokeSlots = record.m_bespokeCaseSlots?.ToArray(), cables = record.cablesInstalled?.Length,
            parts = NativeObjectState.Parts(record).Select(part => new
            {
                path = part.Key, sku = part.Value.GetPartId(), orientation = part.Value.GetSlotOrientation(), flipped = part.Value.m_flipped
            }).ToArray()
        };
    }

    private static object ToolsReport()
    {
        var coop = CoopRunner.Instance?.Coop;
        var controller = GameController.Get();
        var stateStack = new List<string>();
        if (controller?.m_previousState != null)
            foreach (var state in controller.m_previousState)
                if (state != null) stateStack.Add(StateClass(state));

        var tables = Object.FindObjectsOfType<BenchToolSlot>(true)
            .Where(table => table != null)
            .Select(table =>
            {
                var key = NativeToolBenches.Key(table);
                var claim = coop?.ToolBenches?.Handler.At(key);
                var mesh = table.Slot?.GetCaseInSlot();
                return new
                {
                    key,
                    tool = (byte)table.CurrentBenchType,
                    toolName = table.CurrentBenchType.ToString(),
                    slot = table.Slot?.name,
                    pc = mesh == null ? 0UL : (coop?.Objects.IdOf(mesh) ?? default).Value,
                    claim = claim == null ? null : new
                    {
                        actor = claim.Actor.ToString(),
                        tool = claim.Tool.ToString(),
                        claim.Epoch
                    }
                };
            })
            .OrderBy(table => table.key, StringComparer.Ordinal)
            .ToArray();

        var uses = coop?.ToolBenches?.Handler.Uses
            .OrderBy(use => use.Bench, StringComparer.Ordinal)
            .Select(use => (object)new { use.Bench, actor = use.Actor.ToString(), tool = use.Tool.ToString(), use.Epoch })
            .ToArray() ?? Array.Empty<object>();

        var inventory = CareerStatus.Get()?.GetInventory();
        var variants = inventory == null
            ? Array.Empty<object>()
            : inventory.ToArray()
                .Where(part => part != null && PartVariants.Of(part).Length > 0)
                .GroupBy(PartVariants.Key, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    var part = group.First();
                    return (object)new
                    {
                        key = group.Key,
                        sku = part.GetPartId(),
                        nativeType = part.GetIl2CppType().FullName,
                        variant = PartVariants.Of(part),
                        count = group.Count()
                    };
                })
                .ToArray();

        var designs = coop?.Objects.Initialized == true
            ? coop.Objects.Model.Pcs
                .OrderBy(pc => pc.Id.Value)
                .Select(pc =>
                {
                    var bench = coop.Objects.FindBench(pc.Id);
                    var record = bench?.GetComputer(false) ?? coop.Objects.RecordFor(pc.Id);
                    if (record?.isCustomised != true) return null;
                    var design = NativeToolBenches.DesignOf(record);
                    string hash = null, error = null;
                    if (design != null)
                    {
                        try { hash = Convert.ToHexString(SHA256.HashData(NativeToolBenches.WriteDesign(design))); }
                        catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
                    }
                    return (object)new
                    {
                        pc = pc.Id.Value,
                        caseSku = record.caseID?.GetPartId(),
                        designId = record.CustomisationID,
                        found = design != null,
                        layers = design?.SavedPrims?.Count ?? 0,
                        hash,
                        error
                    };
                })
                .Where(design => design != null)
                .ToArray()
            : Array.Empty<object>();

        return new
        {
            role = coop?.Session.Role.ToString(),
            generation = coop?.Session.World?.Generation,
            revision = coop?.Session.World?.Revision,
            currentState = controller == null ? null : StateClass(controller.GetCurrentState()),
            previousStates = stateStack,
            tables,
            claims = uses,
            inventoryVariants = variants,
            caseDesigns = designs
        };
    }

    private static object PasteReport(NetObjectId id)
    {
        var coop = CoopRunner.Instance?.Coop;
        if (coop?.Objects.Initialized != true) return new { pc = id.Value, available = false, reason = "object world not initialized" };
        var bench = coop.Objects.FindBench(id);
        var mesh = bench?.GetCaseInSlot();
        var record = bench?.GetComputer(false) ?? coop.Objects.RecordFor(id);
        if (mesh == null)
            return new { pc = id.Value, available = false, reason = "PC has no live case on a bench", savedPasteFlag = (bool?)record?.thermalPasteApplied };

        var nativePastes = NativeComponentEdits.Pastes(mesh)
            .Select(paste => (object)new { paste.Path, paste.Applied })
            .ToArray();
        return new
        {
            role = coop.Session.Role.ToString(),
            generation = coop.Session.World?.Generation,
            revision = coop.Session.World?.Revision,
            pc = id.Value,
            caseSku = record?.caseID?.GetPartId(),
            savedPasteFlag = (bool?)record?.thermalPasteApplied,
            livePasteCount = nativePastes.Length,
            pastes = nativePastes
        };
    }

    private static string StateClass(State state) => state?.GetIl2CppType()?.FullName;
}
