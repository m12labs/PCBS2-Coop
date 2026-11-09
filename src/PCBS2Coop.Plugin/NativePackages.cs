using System.IO.Compression;
using UnityEngine;
using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>
/// Phase 16c (owner decisions, November 8, 2026): packages and physical job notes in a co-op Career on this game
/// (<see cref="PackageWorld"/>, command 20).
/// <list type="bullet">
/// <item>The host's game is the reference: twice a second, when nothing is loading, it compares its benches
/// (<c>BenchSlot.GetPackageInSlot</c>), its note slots (<c>PostItSlot</c>, except those on a package) and its hands
/// with the model and shares the difference. The guest's game follows: it makes or removes bench packages
/// (<c>SaveLoadSystem.LoadPackageToSlot</c>, <c>BenchSlot.DestroyPackage</c>) and notes
/// (<c>SpawnJobPostItAtSlot</c>, <c>PostItSlot.Clear</c>). A note for a job this game doesn't know yet (the host
/// accepted it today) waits for the day-end reload.</item>
/// <item>The guest's package and note actions are asked first. Once accepted, its game runs its own native action
/// (pick up, drop, pack, open), and the host's game makes the matching change on its benches and inventory.</item>
/// <item>Packed parts leave the shared inventory as the exact owned items; opening puts them back (the host's
/// career update gives them new IDs). Parts travel compressed in the game's own serialized form.</item>
/// <item>A departed guest's package goes to a free bench, or its parts to the inventory; its note to a free note slot.</item>
/// </list>
/// </summary>
internal sealed class NativePackages : IPackageNative
{
    private const float ObserveEvery = 0.5f, FollowEvery = 1f, PendingTimeout = 15f, RecoverEvery = 2f;

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    public PackageWorld Handler { get; }
    internal static NativePackages Current => CoopController.Instance?.Packages;

    /// <summary>Guest: its requests waiting for the host, with the native action to run here once accepted.</summary>
    private readonly Dictionary<OperationId, (string What, Action Here, float At)> _pending = new();
    private (string What, Action Here)? _sending;
    private float _nextObserve, _nextFollow, _nextRecover;
    private bool _dirty;
    private readonly HashSet<int> _missingJobs = new();

    public NativePackages(CoopController coop, WorldSync world, CareerWorld career)
    {
        _coop = coop; _world = world;
        Handler = new PackageWorld(coop.Activity, career, this) { ItemsInUse = InUse };
        world.GenerationStarted += (uint _, string _) => { _pending.Clear(); _dirty = false; _missingJobs.Clear(); };
        world.Rejected += OnRejected;
    }

    private IEnumerable<ulong> InUse() => _coop.Edits?.Handler.ItemsInUse(default) ?? Enumerable.Empty<ulong>();

    private bool Active => NativeCareer.InCareer && Handler.Bound && _world.Admitted && _world.Divergence == null && _coop.Objects.Initialized && _coop.WorldReady;
    private ulong Epoch => _coop.Activity.Get(_world.Local)?.Epoch ?? 0;
    private static SaveLoadSystem Saves => UnityEngine.Object.FindObjectOfType<SaveLoadSystem>() ?? throw new InvalidDataException("Save system unavailable.");
    private static WorkshopController Workshop => UnityEngine.Object.FindObjectOfType<WorkshopController>() ?? throw new InvalidDataException("Workshop unavailable.");

    /// <summary>The player holds a package or a note (PC pickups refuse it).</summary>
    internal bool HandsFull(ActorSlot actor) => Handler.Bound && Handler.HandsFull(actor);
    /// <summary>The model has a package on this bench (PC drops refuse it).</summary>
    internal bool BenchHasPackage(BenchSlot bench) => Handler.Bound && Handler.Benches.ContainsKey(NativeObjectState.BenchKey(bench));

    // ---------------- reading and writing the game ----------------

    internal static byte[] PackPart(PartInstance part)
    {
        var raw = PartVariants.Write(part);
        using var output = new MemoryStream();
        using (var z = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    internal static PartInstance UnpackPart(byte[] data)
    {
        using var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        input.CopyTo(raw);
        if (raw.Length > 4 * 1024 * 1024) throw new InvalidDataException("Invalid packed part.");
        return PartVariants.Read(raw.ToArray());
    }

    private static PackageFact FactOf(PackageCareerStatus status)
    {
        var parts = new List<byte[]>();
        if (status?.contents != null) foreach (var p in status.contents) if (p != null) parts.Add(PackPart(p));
        return new PackageFact(status?.jobId ?? 0, parts);
    }

    private static PackageFact FactOf(Package p) => p == null ? null : FactOf(p.m_careerStatus);

    private static PackageCareerStatus StatusOf(PackageFact fact)
    {
        var status = new PackageCareerStatus { jobId = fact.JobId, contents = new Il2CppSystem.Collections.Generic.List<PartInstance>() };
        foreach (var c in fact.Contents) status.contents.Add(UnpackPart(c));
        return status;
    }

    private static string NoteKey(PostItSlot slot) => NativeObjectState.PathOf(slot.transform);

    /// <summary>The note slots shared in co-op: every one in the workshop except those on a package (a package's
    /// note travels with it).</summary>
    private static Dictionary<string, PostItSlot> NoteSlots()
    {
        var map = new Dictionary<string, PostItSlot>(StringComparer.Ordinal);
        foreach (var s in UnityEngine.Object.FindObjectsOfType<PostItSlot>(true))
            if (s != null && s.GetComponentInParent<Package>() == null) map[NoteKey(s)] = s;
        return map;
    }

    private static int NoteOn(PostItSlot slot) => slot?.GetPostIt() != null && slot.GetJobId() > 0 ? slot.GetJobId() : 0;
    private static int JobOf(PostIt note) => note?.GetJob()?.GetId() ?? 0;

    private static Dictionary<string, (BenchSlot Bench, PackageFact Package)> ReadBenches()
    {
        var map = new Dictionary<string, (BenchSlot, PackageFact)>(StringComparer.Ordinal);
        foreach (var b in Saves.benchSlots)
            if (b != null && b.GetPackageInSlot() is { } p) map[NativeObjectState.BenchKey(b)] = (b, FactOf(p));
        return map;
    }

    private Job FindJob(int id)
    {
        var job = id > 0 ? CareerStatus.Get()?.GetJob(id) : null;
        if (job == null && id > 0 && _missingJobs.Add(id)) Plugin.Logger.LogInfo($"[packages] job {id} isn't in this game's job list yet; its note appears after the day-end reload.");
        return job;
    }

    // ---------------- binding ----------------

    internal void BindHost()
    {
        if (!NativeCareer.InCareer) return;
        var wc = Workshop;
        // Loops, not lambdas: the plugin builds without the nullable attributes a lambda over these types needs.
        var benches = new Dictionary<string, PackageFact>(StringComparer.Ordinal);
        foreach (var (key, b) in ReadBenches()) benches[key] = b.Package;
        var notes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, slot) in NoteSlots()) if (NoteOn(slot) is var job && job > 0) notes[key] = job;
        Handler.BindHost(benches, notes, FactOf(wc.GetCarriedPackage()), JobOf(wc.GetCarriedPostIt()));
        Plugin.Logger.LogInfo($"[packages] host bound {benches.Count} bench package(s) and {notes.Count} note(s).");
    }

    /// <summary>Guest, once admitted: what it loaded follows the host's model. The host's own carried package and
    /// note came with the host's save into this game's hands; they go back to the host (the model).</summary>
    internal void BindGuest()
    {
        if (!NativeCareer.InCareer || !Handler.Bound) return;
        try
        {
            var wc = Workshop;
            if (wc.GetCarriedPackage() != null && Handler.CarriedBy(ActorSlot.Guest) == null)
            {
                // Its parts land in this game's inventory and are taken out again by the career (host wins).
                NativeProjection.Run(wc.EmptyPackage);
                Plugin.Logger.LogInfo("[packages] put down the host's carried package that came with its save.");
            }
            if (wc.GetCarriedPostIt() != null && Handler.NoteCarriedBy(ActorSlot.Guest) == 0 &&
                NoteSlots().Values.FirstOrDefault(s => s.GetPostIt() == null && s.m_benchSlot == null) is { } free)
                NativeProjection.Run(() => wc.DropPostIt(free));
            NativeCareer.Current?.Recheck();
            _dirty = true;
            Follow();
        }
        catch (Exception e) { _coop.ProjectionFailed("This game's packages and notes could not follow the host's", e); }
    }

    // ---------------- per frame ----------------

    public void Update()
    {
        if (!Active) return;
        var now = Time.realtimeSinceStartup;
        foreach (var (op, p) in _pending.ToList())
            if (now - p.At > PendingTimeout) { _pending.Remove(op); Plugin.Logger.LogWarning($"[packages] {p.What} wasn't answered; continuing."); }
        var wc = Workshop;
        if (wc.IsCaseLoading || Saves.SpawningPC) return;
        if (_world.Local == ActorSlot.Host)
        {
            if (now >= _nextObserve) { _nextObserve = now + ObserveEvery; Observe(); }
            if (now >= _nextRecover) { _nextRecover = now + RecoverEvery; RecoverDeparted(); }
            return;
        }
        if ((_dirty || now >= _nextFollow) && _pending.Count == 0) { _nextFollow = now + FollowEvery; Follow(); }
    }

    /// <summary>Host: share what its game shows that the model doesn't.</summary>
    private void Observe()
    {
        try
        {
            var benches = ReadBenches();
            foreach (var key in benches.Keys.Concat(Handler.Benches.Keys).Distinct().ToList())
            {
                var here = benches.TryGetValue(key, out var b) ? b.Package : null;
                var model = Handler.Benches.GetValueOrDefault(key);
                if (here == null ? model == null : here.Like(model)) continue;
                if (!Submit(PackageWorld.HostBench(Epoch, key, here), "bench")) return;
            }
            foreach (var (key, slot) in NoteSlots())
            {
                var job = NoteOn(slot);
                if (job == Handler.Notes.GetValueOrDefault(key)) continue;
                if (!Submit(PackageWorld.HostNote(Epoch, key, job), "note")) return;
            }
            foreach (var key in Handler.Notes.Keys.ToList())
                if (!NoteSlots().ContainsKey(key)) Submit(PackageWorld.HostNote(Epoch, key, 0), "note");
            var wc = Workshop;
            var carried = FactOf(wc.GetCarriedPackage()); var note = JobOf(wc.GetCarriedPostIt());
            var model2 = Handler.CarriedBy(ActorSlot.Host);
            if (!(carried == null ? model2 == null : carried.Like(model2)) || note != Handler.NoteCarriedBy(ActorSlot.Host))
                Submit(PackageWorld.HostCarry(Epoch, carried, note), "carry");
        }
        catch (Exception e) { _coop.ProjectionFailed("A package or note change could not be shared", e); }
    }

    private bool Submit(byte[] payload, string what)
    {
        if (payload.Length > Protocol.MaxPayloadBytes) { Plugin.Logger.LogWarning($"[packages] that {what} is too large to share."); return true; }
        return _world.Submit(CommandTypes.Packages, payload, out _);
    }

    /// <summary>Guest: its benches and note slots become the model's (host wins). Its own hands stay as they are.</summary>
    private void Follow()
    {
        _dirty = false;
        try
        {
            var benches = ReadBenches();
            var changes = 0;
            foreach (var key in benches.Keys.Concat(Handler.Benches.Keys).Distinct().ToList())
            {
                var here = benches.TryGetValue(key, out var b) ? b : default;
                var model = Handler.Benches.GetValueOrDefault(key);
                if (here.Package == null ? model == null : here.Package.Like(model)) continue;
                var bench = here.Bench ?? _coop.Objects.BenchAt(key);
                if (bench == null) { Plugin.Logger.LogWarning($"[packages] bench {key} isn't in this game."); continue; }
                if (here.Package != null) NativeProjection.Run(bench.DestroyPackage);
                if (model != null) { var status = StatusOf(model); NativeProjection.Run(() => Saves.LoadPackageToSlot(status, bench)); }
                changes++;
            }
            var wc = Workshop;
            foreach (var (key, slot) in NoteSlots())
            {
                var want = Handler.Notes.GetValueOrDefault(key);
                if (NoteOn(slot) == want) continue;
                if (want > 0 && FindJob(want) is null) continue;
                if (slot.GetPostIt() != null) NativeProjection.Run(slot.Clear);
                if (want > 0) { var job = FindJob(want); NativeProjection.Run(() => wc.SpawnJobPostItAtSlot(job, slot)); }
                changes++;
            }
            if (changes > 0) Plugin.Logger.LogInfo($"[packages] packages and notes follow the host's ({changes} change(s)).");
        }
        catch (Exception e) { _coop.ProjectionFailed("This game's packages and notes could not follow the host's", e); }
    }

    // ---------------- departure (host) ----------------

    private void RecoverDeparted()
    {
        const ActorSlot seat = ActorSlot.Guest;
        if (!Handler.HandsFull(seat) || _coop.Activity.Get(seat)?.Player is { } p && p != default) return;
        try
        {
            var bench = "";
            if (Handler.CarriedBy(seat) != null)
            {
                foreach (var b in Saves.benchSlots)
                {
                    if (b == null || b.m_packagePostItSlot == null || b.GetCaseInSlot() != null || b.m_caseLoadingToSlot || b.GetPackageInSlot() != null) continue;
                    var key = NativeObjectState.BenchKey(b);
                    if (Handler.Benches.ContainsKey(key) || _coop.Objects.Model.Pcs.Any(x => x.Location == PcLocation.Bench(key))) continue;
                    bench = key; break;
                }
            }
            var noteSlot = Handler.NoteCarriedBy(seat) > 0
                ? NoteSlots().FirstOrDefault(s => s.Value.GetPostIt() == null && s.Value.m_benchSlot == null && !Handler.Notes.ContainsKey(s.Key)).Key ?? ""
                : "";
            _world.Submit(CommandTypes.Packages, PackageWorld.Recover(Epoch, seat, bench, noteSlot), out _);
            Plugin.Logger.LogInfo($"[packages] the departed client's package goes to {(bench.Length > 0 ? "a bench" : "the inventory")}.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[packages] could not recover the departed client's package: " + e.Message); }
    }

    // ---------------- guest requests ----------------

    private bool Ready()
    {
        if (!Active) { _coop.Notice("Packages aren't shared yet; try again in a moment."); return false; }
        if (_pending.Count > 0) { _coop.Notice("Wait a moment: your last package or note action is on its way to the host."); return false; }
        return true;
    }

    private void Ask(byte[] payload, string what, Action here)
    {
        if (!Ready()) return;
        _sending = (what, here);
        try
        {
            if (!_world.Submit(CommandTypes.Packages, payload, out var op)) { _coop.Notice("Not connected to the host."); return; }
            if (_world.Local != ActorSlot.Host) _pending[op] = (what, here, Time.realtimeSinceStartup);
        }
        finally { _sending = null; }
    }

    private void OnRejected(OperationId op, RejectReason reason, string text)
    {
        if (!_pending.Remove(op, out var p) && (op.Actor != _world.Local || _sending == null)) return;
        _coop.Notice("The host refused that: " + text.Replace(Handler.Name + ": ", ""));
        _dirty = true;
    }

    internal void RequestTake(BenchSlot bench) =>
        Ask(PackageWorld.Take(Epoch, NativeObjectState.BenchKey(bench)), "package pickup", () => Workshop.PickupPackage(bench));

    internal void RequestPlace(BenchSlot bench) =>
        Ask(PackageWorld.Place(Epoch, NativeObjectState.BenchKey(bench)), "package drop", () => Workshop.DropPackage(bench));

    internal void RequestPack(PartInstance part)
    {
        var id = NativeCareer.Current?.IdForUse(part) ?? 0;
        if (id == 0) { _coop.Notice("That part isn't in the shared inventory yet; try again in a moment."); return; }
        Ask(PackageWorld.Pack(Epoch, id), "packing", () => NativeCareer.Current?.PackExact(id, exact =>
        {
            var wc = Workshop;
            if (wc.GetCarriedPackage() is { } carried) carried.Add(exact);
            else wc.TrySpawnPackageInHands(out _, exact);
        }));
    }

    internal void RequestOpen(BenchSlot bench)
    {
        // A bench package: the host's game opens it and this game follows (its parts arrive with the career update).
        if (bench != null) Ask(PackageWorld.Open(Epoch, NativeObjectState.BenchKey(bench)), "opening", () => { });
        else Ask(PackageWorld.Open(Epoch, ""), "opening", () => Workshop.EmptyPackage());
    }

    internal void RequestNoteTake(PostIt note)
    {
        var slot = NoteSlots().FirstOrDefault(s => s.Value.GetPostIt() == note);
        if (slot.Value == null) { _coop.Notice("Notes on packages aren't shared in co-op; pick up the package instead."); return; }
        Ask(PackageWorld.NoteTake(Epoch, slot.Key), "note pickup", () => Workshop.PickUpPostIt(note));
    }

    internal void RequestNoteDrop(PostItSlot slot) =>
        Ask(PackageWorld.NoteDrop(Epoch, NoteKey(slot)), "note drop", () => Workshop.DropPostIt(slot));

    internal void RequestNoteDrop(BenchSlot bench)
    {
        var slot = bench?.m_casePostItSlot;
        if (slot == null) { _coop.Notice("That bench has no note slot."); return; }
        Ask(PackageWorld.NoteDrop(Epoch, NoteKey(slot)), "note drop", () => Workshop.DropPostIt(bench));
    }

    // ---------------- IPackageNative ----------------

    public string CheckHost(in CommandContext ctx, PackageChange c)
    {
        var host = ctx.Op.Actor == ActorSlot.Host;
        switch (c.Step)
        {
            case PackageStep.Take or PackageStep.Place:
            {
                var bench = _coop.Objects.BenchAt(c.Key);
                if (bench == null) return "That bench is missing on the host's game.";
                if (bench.m_caseLoadingToSlot) return "That bench is still loading on the host's game.";
                if (c.Step == PackageStep.Take) return bench.GetPackageInSlot() == null ? "The host's bench has no package." : null;
                if (bench.GetCaseInSlot() != null || bench.GetPackageInSlot() != null) return "That bench isn't free.";
                return _coop.Objects.Model.Pcs.Any(x => x.Location == PcLocation.Bench(c.Key)) ? "That bench isn't free." : null;
            }
            case PackageStep.Pack:
                return NativeCareer.Current?.Find(c.Item) == null ? "That part is no longer in the host's inventory." : null;
            case PackageStep.Open when c.Key.Length > 0:
                return _coop.Objects.BenchAt(c.Key)?.GetPackageInSlot() == null ? "The host's bench has no package." : null;
            case PackageStep.NoteTake or PackageStep.NoteDrop:
            {
                if (!NoteSlots().TryGetValue(c.Key, out var slot)) return "That note slot is missing on the host's game.";
                if (c.Step == PackageStep.NoteTake) return slot.GetPostIt() == null ? "The host's note slot is empty." : null;
                if (slot.GetPostIt() != null) return "There's already a note there.";
                return FindJob(Handler.NoteCarriedBy(ctx.Op.Actor)) == null ? "The host's game doesn't know that note's job." : null;
            }
            default:
                return null;
        }
    }

    public (byte[] Part, int Job) PackItem(ulong item)
    {
        var part = NativeCareer.Current?.Find(item) ?? throw new InvalidDataException("That part is no longer in the host's inventory.");
        return (PackPart(part), part.GetJobId());
    }

    public void Changed(in MutationContext ctx, PackageChange c, PackageFact before)
    {
        if (ctx.FromLocalPlayer && _pending.Remove(ctx.Op, out var mine))
        {
            // This game's own action, accepted: the game's native step, here.
            try { NativeProjection.Run(mine.Here); }
            catch (Exception e) { _coop.ProjectionFailed($"Your {mine.What} could not be shown on this game", e); }
            _dirty = true;
            return;
        }
        if (_world.Local != ActorSlot.Host) { _dirty = true; return; }
        if (ctx.Op.Actor == ActorSlot.Host && c.Step != PackageStep.Recover) return; // the host's own game already shows it
        try { NativeProjection.Run(() => HostApply(c, before)); }
        catch (Exception e) { _coop.ProjectionFailed($"The client's {c.Step} could not be made on the host's game", e); }
    }

    /// <summary>Host: the guest's accepted change (or a recovery) on the host's benches and inventory.</summary>
    private void HostApply(PackageChange c, PackageFact before)
    {
        var wc = Workshop;
        switch (c.Step)
        {
            case PackageStep.Take:
                _coop.Objects.BenchAt(c.Key)?.DestroyPackage();
                break;
            case PackageStep.Place:
                Saves.LoadPackageToSlot(StatusOf(before), _coop.Objects.BenchAt(c.Key) ?? throw new InvalidDataException("the bench is missing"));
                break;
            case PackageStep.Pack:
                NativeCareer.Current?.TakeOut(c.Item);
                break;
            case PackageStep.Open when c.Key.Length > 0:
                wc.EmptyPackageSlot(_coop.Objects.BenchAt(c.Key) ?? throw new InvalidDataException("the bench is missing"));
                break;
            case PackageStep.Open:
                ReturnParts(before);
                if (before != null && FindJob(before.JobId) is { } job) wc.SpawnJobPostItAtAnySlot(job);
                break;
            case PackageStep.NoteTake:
                if (NoteSlots().TryGetValue(c.Key, out var from)) from.Clear();
                break;
            case PackageStep.NoteDrop:
                if (NoteSlots().TryGetValue(c.Key, out var to) && FindJob(c.Note) is { } dropped) wc.SpawnJobPostItAtSlot(dropped, to);
                break;
            case PackageStep.Recover:
                if (before != null)
                {
                    if (c.Key.Length > 0 && _coop.Objects.BenchAt(c.Key) is { } bench) Saves.LoadPackageToSlot(StatusOf(before), bench);
                    else ReturnParts(before);
                }
                if (c.Note > 0 && c.NoteKey.Length > 0 && NoteSlots().TryGetValue(c.NoteKey, out var slot) && FindJob(c.Note) is { } noteJob)
                    wc.SpawnJobPostItAtSlot(noteJob, slot);
                _coop.AddChat(before != null && c.Key.Length == 0 ? "Client left; their package's parts went back to the inventory." : "Client left; their package and note were put down.");
                break;
        }
    }

    private static void ReturnParts(PackageFact package)
    {
        if (package == null) return;
        foreach (var c in package.Contents) NativeCareer.Current?.AddOwned(UnpackPart(c));
    }
}
