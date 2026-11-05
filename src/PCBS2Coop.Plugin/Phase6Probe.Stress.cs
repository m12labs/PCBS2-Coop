using System.Security.Cryptography;
using PCBS2Coop.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Opt-in seed fixture for persistence/snapshot capacity. Each duplicate is made through the accepted PC
/// import lifecycle and stored through the accepted transfer lifecycle; this does not test the build gestures.</summary>
internal sealed partial class Phase6Probe
{
    private sealed class StressFixture
    {
        public NetObjectId Seed;
        public byte[] SourceBytes;
        public ComputerSave Source;
        public int Remaining;
        public int Requested;
        public int Completed;
        public uint Generation;
        public float Deadline;
        public NetObjectId Current;
        public bool StoreRequested;
        public readonly List<ulong> Created = new();
        public readonly HashSet<long> NativeRecords = new();
        public readonly List<ComputerSave> NativeRecordRefs = new();
        public readonly HashSet<NetObjectId> KnownIds = new();
    }

    private StressFixture _stress;
    private const float StressStepTimeout = 180f;

    private bool StressCommand(string command)
    {
        if (command == "stress-store")
        {
            try { RecoverStressCarrier(); }
            catch (Exception e) { Event("stress-fixture-recovery-failed", new { error = e.ToString() }); }
            return true;
        }
        if (!command.StartsWith("stress-import:", StringComparison.Ordinal)) return false;
        try
        {
            if (_stress != null) throw new InvalidOperationException("A stress fixture is already running.");
            if (!Safe) throw new InvalidOperationException("Disposable save isolation is not active.");
            var coop = CoopRunner.Instance?.Coop ?? throw new InvalidOperationException("Co-op is unavailable.");
            if (coop.Session.Role != Role.Host || coop.Session.World is not { Admitted: true, IsAuthority: true })
                throw new InvalidOperationException("The fixture can run only in an admitted host world.");
            if (!coop.WorldReady || !coop.Objects.Initialized) throw new InvalidOperationException("The host world is not ready.");
            EnsureStressIdle(coop);

            var fields = command.Split(':');
            if (fields.Length != 3 || !uint.TryParse(fields[1], out var seedValue) ||
                !int.TryParse(fields[2], out var count) || count is < 1 or > 40)
                throw new ArgumentException("Use stress-import:<bench-pc-id>:<count>, with count 1..40.");

            var seed = new NetObjectId(seedValue);
            var fact = coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == seed);
            if (fact == null || fact.Location.Place != PcPlace.Bench)
                throw new InvalidOperationException("The seed must be a PC currently on a bench.");
            if (fact.Parts.Count < 10) throw new InvalidOperationException("The seed must be a fully built PC with at least 10 logical parts.");
            var seedBench = coop.Objects.FindBench(seed) ?? throw new InvalidOperationException("The seed bench is not projected on the host.");
            var source = seedBench.GetComputer(false);
            if (source == null) throw new InvalidOperationException("The seed has no native record.");
            if (coop.Objects.IdOf(source) != seed) throw new InvalidOperationException("The seed's current native record is not bound to that PC.");
            NativeObjectState.CheckParts(fact, source);
            var sourceBytes = NativePcCodec.Write(source);
            NativeObjectState.CheckParts(fact, NativePcCodec.Read(sourceBytes));

            var capacity = ObjectWorld.MaxPcs - coop.Objects.Model.Pcs.Count;
            if (count > capacity) throw new InvalidOperationException($"The request exceeds remaining PC capacity ({capacity}).");
            if (Object.FindObjectOfType<WorkshopController>()?.CarryingEmpty() != true)
                throw new InvalidOperationException("The host must have empty hands.");
            if (coop.Lifecycle?.HasPending == true || coop.Transfers?.HasPending == true)
                throw new InvalidOperationException("Another PC lifecycle or transfer request is pending.");

            _stress = new StressFixture { Seed = seed, Source = source, SourceBytes = sourceBytes, Remaining = count,
                Generation = coop.Session.World.Generation, Deadline = Time.realtimeSinceStartup + StressStepTimeout };
            foreach (var existing in coop.Objects.Model.Pcs) _stress.KnownIds.Add(existing.Id);
            Event("stress-fixture-start", new
            {
                seed = seed.Value, requested = count, sourceParts = fact.Parts.Count,
                sourceBytes = sourceBytes.Length, initialPcs = coop.Objects.Model.Pcs.Count,
                role = coop.Session.Role.ToString(), fixture = "accepted import + accepted storage transfer; not build-gesture coverage"
            });
            BeginStressImport(coop);
        }
        catch (Exception e)
        {
            _stress = null;
            Event("stress-fixture-failed", new { stage = "start", error = e.ToString() });
        }
        return true;
    }

    private void RecoverStressCarrier()
    {
        if (_stress != null) throw new InvalidOperationException("A stress fixture is already running.");
        if (!Safe) throw new InvalidOperationException("Disposable save isolation is not active.");
        var coop = CoopRunner.Instance?.Coop ?? throw new InvalidOperationException("Co-op is unavailable.");
        if (coop.Session.World is not { Admitted: true, IsAuthority: true } || !coop.WorldReady || !coop.Objects.Initialized)
            throw new InvalidOperationException("The isolated host world is not ready.");
        EnsureStressIdle(coop);
        var workshop = Object.FindObjectOfType<WorkshopController>() ?? throw new InvalidOperationException("Workshop is unavailable.");
        var carried = workshop.GetCarriedCase();
        if (carried == null || workshop.IsCaseLoading || Object.FindObjectOfType<SaveLoadSystem>()?.SpawningPC == true)
            throw new InvalidOperationException("No settled carried PC is available to store.");
        var id = coop.Objects.IdOf(carried);
        var fact = coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == id);
        if (fact?.Location is not { Place: PcPlace.Carrier, Actor: ActorSlot.Host } || fact.Provenance.Origin != InstanceOrigin.Imported)
            throw new InvalidOperationException("Only the fixture's imported host carrier can be recovered.");
        if (coop.Lifecycle?.HasPending == true || coop.Transfers?.HasPending == true)
            throw new InvalidOperationException("A PC lifecycle or transfer is still pending.");
        if (coop.Transfers == null) throw new InvalidOperationException("PC transfers are unavailable.");
        coop.Transfers.RequestStore();
        Event("stress-fixture-recovery-store", new { pc = id.Value, path = "accepted PcTransfers.Store" });
    }

    private void UpdateStressFixture()
    {
        var f = _stress;
        if (f == null) return;
        try
        {
            var coop = CoopRunner.Instance?.Coop;
            if (!Safe || coop?.Session.World is not { Admitted: true, IsAuthority: true } world || world.Generation != f.Generation)
                throw new InvalidOperationException("Host session left the ready isolated world.");
            if (Time.realtimeSinceStartup > f.Deadline) throw new TimeoutException($"Timed out during copy {f.Completed + 1} (world waiting: {coop.WorldWaiting}).");
            if (!coop.WorldReady) return; // A native carrier spawn briefly closes the readiness barrier.

            if (f.Current.IsNone)
            {
                var imported = coop.Objects.Model.Pcs.SingleOrDefault(p =>
                    !f.KnownIds.Contains(p.Id) && p.Provenance.Origin == InstanceOrigin.Imported && p.Provenance.Operation.Actor == coop.Session.World.Local &&
                    p.Location.Place == PcPlace.Carrier && p.Location.Actor == ActorSlot.Host);
                if (imported == null) return;
                f.Current = imported.Id;
                f.KnownIds.Add(imported.Id);
                f.Requested++;
                var carried = coop.Objects.RecordFor(imported.Id);
                if (carried == null) throw new InvalidDataException($"Imported PC {imported.Id} has no native carrier record.");
                NativeObjectState.CheckParts(imported, carried);
                var pointer = carried.Pointer.ToInt64();
                if (pointer == f.Source.Pointer.ToInt64() || !f.NativeRecords.Add(pointer))
                    throw new InvalidDataException("An imported copy reused a native ComputerSave record.");
                f.NativeRecordRefs.Add(carried);
            }

            if (!f.StoreRequested)
            {
                var workshop = Object.FindObjectOfType<WorkshopController>();
                var carried = workshop?.GetCarriedCase();
                if (carried == null || coop.Objects.IdOf(carried) != f.Current || workshop.IsCaseLoading ||
                    Object.FindObjectOfType<SaveLoadSystem>()?.SpawningPC == true ||
                    coop.Transfers?.HasPending == true || coop.Lifecycle?.HasPending == true) return;
                if (coop.Activity.Actors.Any(a => !a.WorkingPc.IsNone || a.Pending != null) || coop.ToolBenches?.Handler.Uses.Any() == true) return;
                if (coop.Transfers == null) throw new InvalidOperationException("PC transfers are unavailable.");
                coop.Transfers.RequestStore();
                f.StoreRequested = true;
                f.Deadline = Time.realtimeSinceStartup + StressStepTimeout;
                Event("stress-fixture-copy-imported", new { index = f.Completed + 1, pc = f.Current.Value, nativeRecord = f.NativeRecords.Last() });
                return;
            }

            if (!f.StoreRequested) return;
            var stored = coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == f.Current);
            if (stored?.Location.Place != PcPlace.Storage || coop.Objects.FindStored(f.Current) == null ||
                Object.FindObjectOfType<WorkshopController>()?.GetCarriedCase() != null || coop.Transfers?.HasPending == true)
                return;
            NativeObjectState.CheckParts(stored, coop.Objects.FindStored(f.Current));
            coop.Objects.CheckProjection(coop.Session.World.Local, coop.Session.LocalIdentity.Player);

            f.Created.Add(f.Current.Value);
            f.Completed++;
            f.Remaining--;
            f.Current = default;
            f.StoreRequested = false;
            if (f.Remaining == 0)
            {
                FinishStressFixture(coop, f);
                _stress = null;
                return;
            }
            f.Deadline = Time.realtimeSinceStartup + StressStepTimeout;
            BeginStressImport(coop);
        }
        catch (Exception e)
        {
            Event("stress-fixture-failed", new { stage = f.StoreRequested ? "store/wait" : "import/wait", completed = f.Completed, current = f.Current.Value, error = e.ToString() });
            _stress = null;
        }
    }

    private void BeginStressImport(CoopController coop)
    {
        EnsureStressIdle(coop);
        if (coop.Transfers?.HasPending == true || coop.Lifecycle?.HasPending == true)
            throw new InvalidOperationException("A prior PC request is still pending.");
        if (Object.FindObjectOfType<WorkshopController>()?.CarryingEmpty() != true)
            throw new InvalidOperationException("Hands were not empty before importing the next copy.");
        // Each copy starts from the exact seed bytes captured before the fixture, regardless of subsequent live changes.
        var clone = NativePcCodec.Read(_stress.SourceBytes);
        coop.Lifecycle.RequestImport(clone);
        _stress.Deadline = Time.realtimeSinceStartup + StressStepTimeout;
        Event("stress-fixture-import-requested", new { index = _stress.Completed + 1, seed = _stress.Seed.Value, bytes = _stress.SourceBytes.Length });
    }

    private static void EnsureStressIdle(CoopController coop)
    {
        if (GameController.Get()?.GetCurrentState()?.TryCast<WalkingState>() == null)
            throw new InvalidOperationException("The host must be walking before each fixture import.");
        EnsureNoActiveWork(coop);
    }

    private static void EnsureNoActiveWork(CoopController coop)
    {
        if (coop.Activity.Actors.Any(a => !a.WorkingPc.IsNone || a.Pending != null))
            throw new InvalidOperationException("A player has an active PC work session or edit claim.");
        if (coop.ToolBenches?.Handler.Uses.Any() == true)
            throw new InvalidOperationException("A player has an active specialist tool table session.");
    }

    private void FinishStressFixture(CoopController coop, StressFixture f)
    {
        var state = coop.Objects.Model.ToBytes();
        Event("stress-fixture-complete", new
        {
            seed = f.Seed.Value, requested = f.Requested, completed = f.Completed,
            pcs = coop.Objects.Model.Pcs.Count, created = f.Created, distinctNativeRecords = f.NativeRecords.Count,
            modelBytes = state.Length, modelSha256 = Convert.ToHexString(SHA256.HashData(state)), summary = Summary(coop)
        });
    }
}
