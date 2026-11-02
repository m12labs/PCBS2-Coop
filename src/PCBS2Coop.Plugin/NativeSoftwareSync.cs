using System.Security.Cryptography;
using PCBS2Coop.Network;
using UnityEngine;

namespace PCBS2Coop;

/// <summary>Publishes settled host software records through the ordered world. Native PC saves
/// remain the durable owner; this stream brings an already joined guest's drive record up to date.</summary>
internal sealed class NativeSoftwareSync : ISoftwareNative
{
    private sealed class Observation
    {
        public string Hash = "";
        public string Published = "";
        public float ChangedAt;
        public string DrivePath = "";
    }

    private readonly CoopController _coop;
    private readonly WorldSync _world;
    private readonly Dictionary<NetObjectId, Observation> _observed = new();
    private float _nextSample;
    public SoftwareWorld Handler { get; }

    internal NativeSoftwareSync(CoopController coop, WorldSync world)
    {
        _coop = coop; _world = world;
        Handler = new SoftwareWorld(coop.Objects.Model, coop.Activity, this, world.IsAuthority);
        world.GenerationStarted += (uint _, string _) => { _observed.Clear(); _host.Clear(); _nextSample = 0; };
    }

    public string CheckHost(in CommandContext ctx, PcFact pc, PartLink bootDrive)
    {
        if (pc.Location.Place != PcPlace.Bench) return "Software PC is not on a bench.";
        var record = _coop.Objects.RecordFor(pc.Id);
        if (record == null || _coop.Objects.IdOf(record) != pc.Id) return "The host PC record is not ready.";
        var bios = record.GetBiosConfig(false);
        if (!NativeObjectState.Parts(record).TryGetValue(bootDrive.Path, out var part) || part == null ||
            part.Pointer != bios?.GetBootDrive()?.Pointer)
            return "Software boot drive changed.";
        return null;
    }

    public void Changed(in MutationContext ctx, PcFact pc, PartLink bootDrive, byte[] record)
    {
        if (_world.IsAuthority) return; // The source is already installed on the host's native drive.
        // Phase 6 mirrors desktop input, so this game usually reaches the same record by itself. Installing an OS (and a
        // PC's first power-on) generates random files on each game, though; keep the host's latest record and adopt it
        // once this game's PC has settled (Reconcile).
        _host[pc.Id] = new HostRecord
        {
            Generation = ctx.Generation, DrivePath = bootDrive.Path, Bytes = record,
            Hash = Convert.ToHexString(SHA256.HashData(record)), Received = Time.realtimeSinceStartup,
        };
    }

    private sealed class HostRecord
    {
        public uint Generation;
        public string DrivePath = "", Hash = "", LocalHash = "";
        public byte[] Bytes = Array.Empty<byte>();
        public float Received, LocalChangedAt;
        public bool Reported;
    }

    private readonly Dictionary<NetObjectId, HostRecord> _host = new();
    private float _nextReconcile;

    /// <summary>Guest: compares this game's boot-drive record with the host's latest and adopts the host's when they
    /// still differ after both have been stable for a while and no mirrored input for that PC is waiting.</summary>
    private void Reconcile()
    {
        if (_world.IsAuthority || _host.Count == 0 || Time.realtimeSinceStartup < _nextReconcile) return;
        _nextReconcile = Time.realtimeSinceStartup + 1f;
        foreach (var (id, host) in _host.ToArray())
        {
            var pc = _coop.Objects.Model.Pcs.SingleOrDefault(x => x.Id == id);
            if (host.Generation != _world.Generation || pc == null || pc.Location.Place != PcPlace.Bench) { _host.Remove(id); continue; }
            try
            {
                var vc = _coop.Objects.FindBench(id)?.m_workStation?.m_virtualComputer;
                var record = vc?.ComputerSave ?? _coop.Objects.RecordFor(id);
                if (record == null || _coop.Objects.IdOf(record) != id) continue;
                var drive = record.GetBiosConfig(false)?.GetBootDrive();
                if (drive?.m_software == null) continue;
                if (!NativeObjectState.Parts(record).TryGetValue(host.DrivePath, out var hostDrive) || hostDrive?.Pointer != drive.Pointer)
                    continue; // the boot drive differs here; a later host record or the PC reload settles it
                var local = Convert.ToHexString(SHA256.HashData(NativeSoftwareCodec.Write(drive.m_software)));
                if (local == host.Hash)
                {
                    if (!host.Reported) Plugin.Logger.LogInfo($"[software] PC {id}: this game's software matches the host's.");
                    _host.Remove(id);
                    continue;
                }
                if (local != host.LocalHash) { host.LocalHash = local; host.LocalChangedAt = Time.realtimeSinceStartup; continue; }
                var quiet = Time.realtimeSinceStartup - Math.Max(host.LocalChangedAt, host.Received);
                if (quiet < 2f || _coop.Desktop?.Busy(id) == true || (vc != null && NativeDesktopInput.SurfaceOf(vc) == DesktopSurface.Boot && quiet < 10f))
                    continue;
                Project(pc, host.DrivePath, host.Bytes);
                host.LocalHash = host.Hash;
                host.Reported = true;
            }
            catch (Exception e)
            {
                _host.Remove(id);
                _coop.ProjectionFailed($"Software on PC {id} could not settle here", e);
            }
        }
    }

    private void Project(PcFact pc, string path, byte[] bytes)
    {
        var record = _coop.Objects.RecordFor(pc.Id);
        if (record == null) throw new InvalidOperationException("The native PC record is still loading.");
        if (_coop.Objects.IdOf(record) != pc.Id) throw new InvalidDataException("The software record belongs to another PC.");
        var bios = record.GetBiosConfig(false);
        if (!NativeObjectState.Parts(record).TryGetValue(path, out var drive) || drive == null ||
            bios?.GetBootDrive()?.Pointer != drive.Pointer)
            throw new InvalidDataException("The accepted boot drive is not present here.");
        var software = NativeSoftwareCodec.Read(bytes); // Host-produced bytes only.
        // Phase 11: keep the light objects the running lights hold, with the host's settings.
        NativeProjection.Run(() => { NativeCustomization.KeepLights(drive.m_software, software); drive.m_software = software; });
        Plugin.Logger.LogInfo($"[software] PC {pc.Id} boot drive {path}: this game's software differed from the host's; adopted the host's record ({bytes.Length} bytes).");
    }

    internal void Update()
    {
        if (_world.Admitted && _coop.WorldReady && _coop.Objects.Initialized) Reconcile();
        if (!_world.IsAuthority || !_world.Admitted || !_coop.WorldReady || !_coop.Objects.Initialized ||
            Time.realtimeSinceStartup < _nextSample) return;
        _nextSample = Time.realtimeSinceStartup + .5f;
        foreach (var pc in _coop.Objects.Model.Pcs.Where(x => x.Location.Place == PcPlace.Bench))
        {
            try { Observe(pc); }
            catch (Exception e) { _coop.ProjectionFailed($"Host software on PC {pc.Id} could not be captured", e); return; }
        }
    }

    private void Observe(PcFact pc)
    {
        if (_coop.Activity.Actors.Any(x => x.Pending?.Pc == pc.Id) || Handler.Busy) return;
        var vc = _coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer;
        var record = vc?.ComputerSave;
        if (record == null || _coop.Objects.IdOf(record) != pc.Id || vc.InOS() != true)
            return;
        var drive = record.GetBiosConfig(false)?.GetBootDrive();
        var software = drive?.m_software;
        if (software == null) return;
        var path = NativeObjectState.Parts(record).FirstOrDefault(x => x.Value?.Pointer == drive.Pointer).Key;
        if (path == null || !pc.Links.Any(x => x.Path == path)) throw new InvalidDataException("OS boot drive has no shared part path.");
        var bytes = NativeSoftwareCodec.Write(software);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!_observed.TryGetValue(pc.Id, out var seen))
        {
            seen = new Observation { Hash = hash, ChangedAt = Time.realtimeSinceStartup, DrivePath = path };
            _observed.Add(pc.Id, seen);
            return;
        }
        if (seen.Hash != hash || seen.DrivePath != path)
        { seen.Hash = hash; seen.DrivePath = path; seen.ChangedAt = Time.realtimeSinceStartup; return; }
        if (seen.Published == hash || Time.realtimeSinceStartup - seen.ChangedAt < 1f) return;
        Publish(pc.Id, path, bytes);
        seen.Published = hash;
    }

    private void Publish(NetObjectId pc, string path, byte[] bytes)
    {
        var actor = _coop.Activity.Get(ActorSlot.Host);
        if (actor == null) throw new InvalidOperationException("Host activity is not ready for software publication.");
        var hash = SHA256.HashData(bytes);
        Submit(SoftwareWorld.Begin(actor.Epoch, pc, path, bytes.Length, hash));
        var total = (bytes.Length + SoftwareWorld.MaxChunkBytes - 1) / SoftwareWorld.MaxChunkBytes;
        for (var index = 0; index < total; index++)
        {
            var offset = index * SoftwareWorld.MaxChunkBytes;
            var length = Math.Min(SoftwareWorld.MaxChunkBytes, bytes.Length - offset);
            Submit(SoftwareWorld.Chunk(actor.Epoch, pc, index, total, bytes.AsSpan(offset, length).ToArray()));
        }
        Submit(SoftwareWorld.Commit(actor.Epoch, pc));
        Plugin.Logger.LogInfo($"[software] PC {pc}: published {bytes.Length} host software bytes in {total} chunks.");
    }

    private void Submit(byte[] payload)
    {
        if (!_world.Submit(CommandTypes.Software, payload, out _))
            throw new InvalidOperationException("The host's software result could not be submitted.");
    }
}
