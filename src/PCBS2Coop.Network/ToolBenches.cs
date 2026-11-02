using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>The specialist tables: delidding and water cooling (phase 9), case modding (phase 11; owner decisions,
/// November 2, 2026).</summary>
public enum BenchTool : byte { Delid = 1, WaterCooling = 2, CaseModding = 3 }

/// <summary>One player at one tool table (<see cref="Bench"/> is the table's bench key).</summary>
public sealed record BenchUse(string Bench, ActorSlot Actor, ulong Epoch, BenchTool Tool);

/// <summary>Native side of the tool tables. Both run on both peers, in host order, after the model changed.</summary>
public interface IToolBenchNative
{
    void Used(in MutationContext ctx, BenchUse use);
    /// <summary><paramref name="result"/>: what the table changed (compressed, already hash checked): the user's
    /// inventory change, or for case modding the finished case design; null when nothing changed.
    /// <paramref name="design"/>: the case design ID the host gave out (case modding), or 0.</summary>
    void Left(in MutationContext ctx, BenchUse use, byte[]? result, int design);
    /// <summary>Host only, while accepting a case-modding result: the design ID the PC gets (above every ID either game
    /// may have used), or 0 to keep the editor's own.</summary>
    int DesignId(BenchUse use, byte[] result);
    /// <summary>A table was switched to another tool (<see cref="ToolBenchWorld.Kinds"/>), or back to working on the PC.</summary>
    void Switched(in MutationContext ctx, string bench, byte kind, bool orbit);
}

/// <summary>
/// Phase 9 tool tables (owner decisions, November 2, 2026): one player at a time at the delidding table and the
/// water-cooling table, and only the result is shared. <b>Use</b> claims a table for its tool session ("Someone is using
/// the delidding table." for the other player). The session itself runs on the user's game only. When it ends, the user
/// sends what the table changed in its inventory (the finished delidded CPU, the water-cooled card) in ordered
/// <b>Chunk</b>s, then <b>Leave</b> with the length and SHA-256; the other game makes the same inventory change.
/// A use belongs to an actor epoch: when that player leaves the session (new epoch or gone), the table is free again.
/// Not saved and not in snapshots: the host announces its own use again when a guest arrives.
/// </summary>
/// <para>Phase 11 adds case modding (the result is the finished case design; the host gives out its design ID while
/// accepting the leave) and <b>Switch</b>: switching a table between delidding, water cooling and case modding, refused
/// while someone else uses it.</para>
public sealed class ToolBenchWorld : ICanonicalCommandHandler
{
    private enum Step : byte { Use = 1, Chunk = 2, Leave = 3, Switch = 4 }
    /// <summary>The game's table types (<c>BenchToolSlot.Type</c>): delidding, water cooling, case modding; 4 is none (working on the PC).</summary>
    public static readonly byte[] Kinds = { 0, 1, 2, 4 };
    public const byte NoTool = 4;
    public const int MaxKey = 512, MaxChunkBytes = 15 * 1024, MaxBytes = 1024 * 1024, MaxChunks = MaxBytes / MaxChunkBytes + 1, HashBytes = 32;

    private readonly ActivityWorld _activity;
    private readonly IToolBenchNative _native;
    private readonly Dictionary<string, BenchUse> _uses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Total, List<byte[]> Chunks)> _chunks = new(StringComparer.Ordinal);

    public ToolBenchWorld(ActivityWorld activity, IToolBenchNative native) { _activity = activity; _native = native; }
    public string Name => "tool bench";
    public void Reset() { _uses.Clear(); _chunks.Clear(); }

    /// <summary>Uses whose player is still in the session with the same epoch.</summary>
    public IEnumerable<BenchUse> Uses => _uses.Values.Where(Live);
    private bool Live(BenchUse u) => _activity.Get(u.Actor)?.Epoch == u.Epoch;
    /// <summary>Who uses that table now, if anyone.</summary>
    public BenchUse? At(string bench) => _uses.TryGetValue(bench ?? "", out var u) && Live(u) ? u : null;
    /// <summary>The table this player uses now, if any.</summary>
    public BenchUse? Of(ActorSlot actor) => Uses.FirstOrDefault(u => u.Actor == actor);

    public static string ToolName(BenchTool tool) => tool switch
    {
        BenchTool.Delid => "delidding", BenchTool.WaterCooling => "water-cooling", BenchTool.CaseModding => "case modding", _ => "tool",
    };
    /// <summary>Why <paramref name="requester"/> can't use that table now, or null.</summary>
    public string? Busy(string bench, ActorSlot requester) =>
        At(bench) is { } u && u.Actor != requester ? $"Someone is using the {ToolName(u.Tool)} table." : null;

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }
    public static byte[] Use(ulong epoch, string bench, BenchTool tool) => Encode(w => { w.Put((byte)Step.Use); w.Put(epoch); w.Put(bench); w.Put((byte)tool); });
    public static byte[] Chunk(ulong epoch, string bench, int index, int total, byte[] data) => Encode(w =>
    { w.Put((byte)Step.Chunk); w.Put(epoch); w.Put(bench); w.Put(index); w.Put(total); Protocol.WriteBlob(w, data); });
    /// <summary>End the session; <paramref name="result"/> null (or empty) when the table changed nothing.</summary>
    public static byte[] Leave(ulong epoch, string bench, BenchTool tool, byte[]? result) => Encode(w =>
    {
        w.Put((byte)Step.Leave); w.Put(epoch); w.Put(bench); w.Put((byte)tool);
        var length = result?.Length ?? 0; w.Put(length);
        Protocol.WriteBlob(w, length == 0 ? Array.Empty<byte>() : SHA256.HashData(result!));
    });
    /// <summary>Switch a table to another tool (<paramref name="kind"/> one of <see cref="Kinds"/>).</summary>
    public static byte[] Switch(ulong epoch, string bench, byte kind, bool orbit) => Encode(w =>
    { w.Put((byte)Step.Switch); w.Put(epoch); w.Put(bench); w.Put(kind); w.Put(orbit); });

    private sealed record Command(Step Step, ulong Epoch, string Bench, BenchTool Tool, int Index, int Total, byte[] Data, int Length, byte[] Hash,
        int Design = 0, byte Kind = 0, bool Orbit = false);

    private static Command Parse(NetDataReader r, bool canonical)
    {
        var step = (Step)r.GetByte(); var epoch = r.GetULong(); var bench = r.GetString(MaxKey);
        Command c;
        switch (step)
        {
            case Step.Use:
                c = new(step, epoch, bench, (BenchTool)r.GetByte(), 0, 0, Array.Empty<byte>(), 0, Array.Empty<byte>());
                break;
            case Step.Chunk:
            {
                var index = r.GetInt(); var total = r.GetInt();
                var data = Protocol.ReadBlob(r, MaxChunkBytes, out _) ?? throw new FormatException("Invalid tool table chunk.");
                c = new(step, epoch, bench, default, index, total, data, 0, Array.Empty<byte>());
                break;
            }
            case Step.Leave:
            {
                var tool = (BenchTool)r.GetByte();
                var length = r.GetInt();
                var hash = Protocol.ReadBlob(r, HashBytes, out var declared) ?? throw new FormatException("Invalid tool table hash.");
                if (length is < 0 or > MaxBytes || (length == 0 ? declared != 0 : declared != HashBytes)) throw new FormatException("Invalid tool table result.");
                var design = canonical ? r.GetInt() : 0;
                if (design < 0) throw new FormatException("Invalid case design ID.");
                c = new(step, epoch, bench, tool, 0, 0, Array.Empty<byte>(), length, hash, design);
                break;
            }
            case Step.Switch:
                c = new(step, epoch, bench, default, 0, 0, Array.Empty<byte>(), 0, Array.Empty<byte>(), Kind: r.GetByte(), Orbit: r.GetBool());
                break;
            default: throw new FormatException("Unknown tool table step.");
        }
        if (r.AvailableBytes != 0) throw new FormatException("Trailing tool table bytes.");
        if (bench.Length == 0 || bench.Any(char.IsControl)) throw new FormatException("Invalid tool table.");
        return c;
    }

    private string? Check(ActorSlot requester, Command c)
    {
        if (_activity.Get(requester) is not { } actor || actor.Epoch != c.Epoch) return "Actor presence changed.";
        if (c.Step == Step.Use)
        {
            if (!Enum.IsDefined(c.Tool)) return "Unknown tool table.";
            if (Busy(c.Bench, requester) is { } busy) return busy;
            if (Of(requester) is { } other && other.Bench != c.Bench) return "You're already using another tool table.";
            return null;
        }
        if (c.Step == Step.Switch)
        {
            if (!Kinds.Contains(c.Kind)) return "Unknown tool table type.";
            return Busy(c.Bench, requester);
        }
        if (At(c.Bench) is not { } use || use.Actor != requester) return "You're not using that tool table.";
        if (c.Step == Step.Leave && c.Tool != use.Tool) return "That tool table was started with another tool.";
        var (total, chunks) = _chunks.TryGetValue(c.Bench, out var got) ? got : (0, new List<byte[]>());
        if (c.Step == Step.Chunk)
        {
            if (c.Total is < 1 or > MaxChunks || c.Data.Length is < 1 or > MaxChunkBytes) return "Invalid tool table chunk.";
            if (chunks.Count > 0 && c.Total != total || c.Index != chunks.Count) return "Tool table chunk out of order.";
            if (chunks.Sum(x => x.Length) + c.Data.Length > MaxBytes) return "The tool table result is too large.";
            return null;
        }
        if (c.Length == 0) return chunks.Count == 0 ? null : "The tool table result is missing its header.";
        if (chunks.Count != total || total == 0) return "The tool table result hasn't fully arrived.";
        var data = chunks.SelectMany(x => x).ToArray();
        return data.Length == c.Length && SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash) ? null : "The tool table result doesn't match.";
    }

    public CommandVerdict Validate(in CommandContext ctx, NetDataReader payload)
    {
        var error = Check(ctx.Op.Actor, Parse(payload, false));
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    /// <summary>The host adds the design ID of a case-modding result to the leave (0 for every other leave).</summary>
    public byte[] Canonicalize(in CommandContext ctx, NetDataReader request)
    {
        var bytes = request.RawData.AsSpan(request.Position, request.AvailableBytes).ToArray();
        var c = Parse(request, false);
        var w = new NetDataWriter(); w.Put(bytes);
        if (c.Step != Step.Leave) return w.CopyData();
        var design = 0;
        if (c.Tool == BenchTool.CaseModding && c.Length > 0 && At(c.Bench) is { } use && _chunks.TryGetValue(c.Bench, out var got))
            design = Math.Max(0, _native.DesignId(use, got.Chunks.SelectMany(x => x).ToArray()));
        w.Put(design);
        return w.CopyData();
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload, true);
        // Uses of players who left the session or came back with a new epoch end here.
        foreach (var stale in _uses.Values.Where(u => !Live(u)).ToArray()) { _uses.Remove(stale.Bench); _chunks.Remove(stale.Bench); }
        if (c.Step == Step.Use)
        {
            if (Check(ctx.Op.Actor, c) is { } error) throw new InvalidDataException("Tool table change no longer applies: " + error);
            var use = new BenchUse(c.Bench, ctx.Op.Actor, c.Epoch, c.Tool);
            _uses[c.Bench] = use;
            _native.Used(ctx, use);
            return;
        }
        if (c.Step == Step.Switch)
        {
            if (Check(ctx.Op.Actor, c) is { } error) throw new InvalidDataException("Tool table change no longer applies: " + error);
            _native.Switched(ctx, c.Bench, c.Kind, c.Orbit);
            return;
        }
        // The host checked ownership. A guest that joined during the session never saw its Use (uses aren't in
        // snapshots, and the host announces its own again after the snapshot), so adopt it rather than diverge.
        if (_activity.Get(ctx.Op.Actor)?.Epoch != c.Epoch) throw new InvalidDataException("Tool table change no longer applies: actor presence changed.");
        if (At(c.Bench) is not { } owned || owned.Actor != ctx.Op.Actor)
        {
            _uses[c.Bench] = new BenchUse(c.Bench, ctx.Op.Actor, c.Epoch, BenchTool.Delid);
            _chunks.Remove(c.Bench);
        }
        if (c.Step == Step.Chunk)
        {
            if (!_chunks.TryGetValue(c.Bench, out var r))
            {
                if (c.Index != 0) return; // joined halfway through a result: this peer can't assemble it
                _chunks[c.Bench] = r = (c.Total, new List<byte[]>());
            }
            if (c.Index == r.Chunks.Count) r.Chunks.Add(c.Data);
            return;
        }
        var left = _uses[c.Bench] with { Tool = c.Tool };
        byte[]? data = null;
        if (c.Length > 0 && _chunks.TryGetValue(c.Bench, out var got) && got.Chunks.Count == got.Total)
        {
            data = got.Chunks.SelectMany(x => x).ToArray();
            if (data.Length != c.Length || !SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash)) data = null;
        }
        _uses.Remove(c.Bench); _chunks.Remove(c.Bench);
        _native.Left(ctx, left, data, data == null ? 0 : c.Design);
    }
}
