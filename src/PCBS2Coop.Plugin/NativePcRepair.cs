using System.IO.Compression;
using System.Security.Cryptography;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>Owner decision (November 1, 2026): when this game can't show a change on a PC, reload that PC with a
/// notice instead of going out of sync. The host's game is the reference. A guest that couldn't show an edit (or
/// couldn't rebind a PC afterwards) waits until nothing is being edited on that PC, asks for it, and reloads it from the
/// host's record. A host that couldn't show the guest's edit keeps what its own game shows; the guest's copy then no
/// longer matches and is reloaded the same way.</summary>
internal sealed class NativePcRepair : IPcRepairNative
{
    internal const string Message = "PC reloaded to avoid a desync. Please report if this keeps happening.";
    private const float RequestTimeout = 45f, ReloadTimeout = 60f, Quiet = 1.5f, PowerDelay = 3f;

    private sealed class Need { public string Why = ""; public float Since, QuietSince, RequestedAt; public bool Requested; }
    private sealed class Reload
    {
        public NetObjectId Pc;
        public BenchSlot Bench;
        public long OldMesh;
        public bool Powered, Pressed;
        public float Started, QuietSince, PressedAt;
    }

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<NetObjectId, Need> _needs = new();
    private readonly List<NetObjectId> _send = new();
    private readonly List<Reload> _reloads = new();
    private float _sendQuietSince;
    public PcRepairWorld Handler { get; }

    public NativePcRepair(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new PcRepairWorld(coop.Objects.Model, coop.Activity, this);
        world.GenerationStarted += (uint _, string _) => { _needs.Clear(); _send.Clear(); _reloads.Clear(); Handler.Reset(); };
        world.Rejected += (OperationId op, RejectReason reason, string text) =>
        {
            if (op.Actor == world.Local && text.Contains(Handler.Name)) Plugin.Logger.LogWarning($"[repair] {op} refused: {text}");
        };
    }

    internal bool Reloading(NetObjectId pc) => _reloads.Any(r => r.Pc == pc) || (_needs.TryGetValue(pc, out var n) && n.Requested);

    /// <summary>This game couldn't show a change on that PC. A guest repairs it; the host keeps its own result.</summary>
    internal void Failed(NetObjectId pc, string what, Exception e)
    {
        if (_world.IsAuthority)
        {
            // The host is the reference: the world keeps what its game shows; the guest's copy gets reloaded to match.
            Plugin.Logger.LogError($"[repair] {what}: {e}. The host keeps what its game shows; the guest's PC will be reloaded to match.");
            return;
        }
        Plugin.Logger.LogError($"[repair] {what}: {e}. Reloading PC {pc} from the host's record.");
        if (!_needs.ContainsKey(pc)) _needs[pc] = new Need { Why = what, Since = Time.realtimeSinceStartup };
    }

    /// <summary>Nothing runs on that PC here and no edit claims it.</summary>
    private bool Settled(NetObjectId pc)
    {
        if (_coop.Activity.Actors.Any(a => a.Pending?.Pc == pc)) return false;
        if (_coop.Edits?.Reloading(pc) == true) return false;
        if (!_coop.Objects.Projections.TryGet(pc, out var mesh) || mesh == null) return false;
        if (CoopController.Instance?.Replays.Running(mesh) == true) return false;
        return !NativeComponentEdits.CablesBusy(mesh);
    }

    public void Update()
    {
        if (!_coop.WorldReady || _world is not { Admitted: true } || !_coop.Objects.Initialized) return;
        var now = Time.realtimeSinceStartup;
        foreach (var (pc, need) in _needs.ToArray())
        {
            if (_coop.Objects.Model.Pcs.All(p => p.Id != pc)) { _needs.Remove(pc); continue; }
            if (need.Requested)
            {
                if (now - need.RequestedAt > RequestTimeout)
                {
                    _needs.Remove(pc);
                    _coop.ProjectionFailed($"PC {pc} could not be reloaded from the host ({need.Why})", new TimeoutException("no record from the host"));
                }
                continue;
            }
            if (!Settled(pc)) { need.QuietSince = 0; continue; }
            if (need.QuietSince == 0) { need.QuietSince = now; continue; }
            if (now - need.QuietSince < Quiet) continue;
            var actor = _coop.Activity.Get(_world.Local);
            if (actor == null || !_world.Submit(CommandTypes.PcRepair, PcRepairWorld.Request(actor.Epoch, pc), out var op)) continue;
            need.Requested = true; need.RequestedAt = now;
            Plugin.Logger.LogInfo($"[repair] PC {pc}: asked the host for its record ({op}).");
        }
        if (_world.IsAuthority) SendNext(now);
        UpdateReloads(now);
    }

    // ---------------- host: send the PC as its game shows it ----------------

    public void Requested(in MutationContext ctx, PcFact pc)
    {
        if (!_world.IsAuthority) return;
        if (!_send.Contains(pc.Id)) _send.Add(pc.Id);
        Plugin.Logger.LogInfo($"[repair] the guest asked for PC {pc.Id}; sending once it is quiet here.");
    }

    private void SendNext(float now)
    {
        if (_send.Count == 0 || Handler.Busy) return;
        var pc = _send[0];
        var fact = _coop.Objects.Model.Pcs.SingleOrDefault(p => p.Id == pc);
        if (fact == null || fact.Location.Place != PcPlace.Bench) { _send.RemoveAt(0); return; }
        if (!Settled(pc)) { _sendQuietSince = 0; return; }
        if (_sendQuietSince == 0) { _sendQuietSince = now; return; }
        if (now - _sendQuietSince < Quiet) return;
        _sendQuietSince = 0;
        _send.RemoveAt(0);
        var actor = _coop.Activity.Get(ActorSlot.Host);
        try
        {
            if (actor == null) throw new InvalidOperationException("host activity is not ready");
            var record = _coop.Objects.CaptureRecord(pc) ?? throw new InvalidDataException("that PC has no native record");
            var bytes = Deflate(NativePcCodec.Write(record));
            if (bytes.Length > PcRepairWorld.MaxBytes) throw new InvalidDataException($"the PC record is too large ({bytes.Length} bytes)");
            _coop.Objects.Projections.TryGet(pc, out var mesh);
            Submit(PcRepairWorld.Begin(actor.Epoch, pc, bytes.Length, SHA256.HashData(bytes), NativeComponentEdits.Running(mesh)));
            var total = (bytes.Length + PcRepairWorld.MaxChunkBytes - 1) / PcRepairWorld.MaxChunkBytes;
            for (var i = 0; i < total; i++)
                Submit(PcRepairWorld.Chunk(actor.Epoch, pc, i, total,
                    bytes.AsSpan(i * PcRepairWorld.MaxChunkBytes, Math.Min(PcRepairWorld.MaxChunkBytes, bytes.Length - i * PcRepairWorld.MaxChunkBytes)).ToArray()));
            Submit(PcRepairWorld.Commit(actor.Epoch, pc));
            Plugin.Logger.LogInfo($"[repair] PC {pc}: sent the host's record ({bytes.Length} bytes in {total} chunks).");
        }
        catch (Exception e) { Plugin.Logger.LogError($"[repair] PC {pc}: the host's record could not be sent: {e}"); }
    }

    private void Submit(byte[] payload)
    {
        if (!_world.Submit(CommandTypes.PcRepair, payload, out _)) throw new InvalidOperationException("the repair could not be submitted");
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) zip.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); var chunk = new byte[16384]; int read;
        while ((read = zip.Read(chunk, 0, chunk.Length)) != 0)
        {
            if (output.Length + read > CoopSavePair.MaxCarrierBytes) throw new InvalidDataException("The PC record expands beyond limits.");
            output.Write(chunk, 0, read);
        }
        return output.ToArray();
    }

    // ---------------- guest: reload that PC ----------------

    public void Arrived(in MutationContext ctx, PcFact pc, byte[] record, bool powered)
    {
        if (_world.IsAuthority) return;
        _needs.Remove(pc.Id);
        try
        {
            var save = NativePcCodec.Read(Inflate(record));
            var bench = _coop.Objects.FindBench(pc.Id) ?? throw new InvalidDataException("the PC is not on a bench here");
            var old = bench.GetCaseInSlot();
            _coop.Objects.ReloadOnBench(pc, bench, save, "this game reloads the PC this player was using to match the host; leaving its view.");
            _reloads.Add(new Reload { Pc = pc.Id, Bench = bench, OldMesh = old == null ? 0 : old.Pointer.ToInt64(), Powered = powered, Started = Time.realtimeSinceStartup });
            Plugin.Logger.LogInfo($"[repair] PC {pc.Id}: reloading it from the host's record.");
        }
        catch (Exception e) { _coop.ProjectionFailed($"PC {pc.Id} could not be reloaded from the host's record", e); }
    }

    private void UpdateReloads(float now)
    {
        foreach (var r in _reloads.ToArray())
        {
            var mesh = r.Bench == null ? null : r.Bench.GetCaseInSlot();
            var loaded = mesh != null && mesh.Pointer.ToInt64() != r.OldMesh && !r.Bench.m_caseLoadingToSlot && _coop.Objects.IdOf(mesh) == r.Pc;
            if (!loaded || NativeComponentEdits.CablesBusy(mesh))
            {
                r.QuietSince = 0;
                if (now - r.Started > ReloadTimeout) Finish(r, "the PC didn't finish reloading");
                continue;
            }
            if (r.Powered && !NativeComponentEdits.Running(mesh))
            {
                if (!r.Pressed)
                {
                    r.Pressed = true; r.PressedAt = now;
                    try { NativeProjection.Run(() => mesh.GetMotherboard()?.TurnOn()); }
                    catch (Exception e) { Finish(r, "turning the reloaded PC on failed: " + e.Message); }
                    continue;
                }
                if (now - r.PressedAt < PowerDelay) continue;
            }
            if (r.QuietSince == 0) { r.QuietSince = now; continue; }
            if (now - r.QuietSince < Quiet) continue;
            Finish(r, null);
        }
    }

    private void Finish(Reload r, string error)
    {
        _reloads.Remove(r);
        if (error != null) { _coop.ProjectionFailed($"PC {r.Pc} could not be reloaded from the host's record", new InvalidDataException(error)); return; }
        _coop.Objects.Assembly.ClearFault(r.Pc);
        _coop.Objects.Assembly.RequestRebind(r.Pc);
        Plugin.Logger.LogWarning($"[repair] PC {r.Pc}: reloaded from the host's record.");
        _coop.Notice(Message);
        _coop.AddChat(Message);
    }
}
