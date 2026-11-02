using System.Security.Cryptography;
using LiteNetLib.Utils;

namespace PCBS2Coop.Network;

/// <summary>The specialist tables shared in phase 9 (owner decisions, November 2, 2026). Case modding is refused in
/// co-op until phase 11 and never claims a table.</summary>
public enum BenchTool : byte { Delid = 1, WaterCooling = 2 }

/// <summary>One player at one tool table (<see cref="Bench"/> is the table's bench key).</summary>
public sealed record BenchUse(string Bench, ActorSlot Actor, ulong Epoch, BenchTool Tool);

/// <summary>Native side of the tool tables. Both run on both peers, in host order, after the model changed.</summary>
public interface IToolBenchNative
{
    void Used(in MutationContext ctx, BenchUse use);
    /// <summary><paramref name="result"/>: what the table changed in the user's inventory (compressed, already hash
    /// checked), or null when nothing changed.</summary>
    void Left(in MutationContext ctx, BenchUse use, byte[]? result);
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
public sealed class ToolBenchWorld : ICommandHandler
{
    private enum Step : byte { Use = 1, Chunk = 2, Leave = 3 }
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

    public static string ToolName(BenchTool tool) => tool == BenchTool.Delid ? "delidding" : "water-cooling";
    /// <summary>Why <paramref name="requester"/> can't use that table now, or null.</summary>
    public string? Busy(string bench, ActorSlot requester) =>
        At(bench) is { } u && u.Actor != requester ? $"Someone is using the {ToolName(u.Tool)} table." : null;

    private static byte[] Encode(Action<NetDataWriter> f) { var w = new NetDataWriter(); f(w); return w.CopyData(); }
    public static byte[] Use(ulong epoch, string bench, BenchTool tool) => Encode(w => { w.Put((byte)Step.Use); w.Put(epoch); w.Put(bench); w.Put((byte)tool); });
    public static byte[] Chunk(ulong epoch, string bench, int index, int total, byte[] data) => Encode(w =>
    { w.Put((byte)Step.Chunk); w.Put(epoch); w.Put(bench); w.Put(index); w.Put(total); Protocol.WriteBlob(w, data); });
    /// <summary>End the session; <paramref name="result"/> null (or empty) when the table changed nothing.</summary>
    public static byte[] Leave(ulong epoch, string bench, byte[]? result) => Encode(w =>
    {
        w.Put((byte)Step.Leave); w.Put(epoch); w.Put(bench);
        var length = result?.Length ?? 0; w.Put(length);
        Protocol.WriteBlob(w, length == 0 ? Array.Empty<byte>() : SHA256.HashData(result!));
    });

    private sealed record Command(Step Step, ulong Epoch, string Bench, BenchTool Tool, int Index, int Total, byte[] Data, int Length, byte[] Hash);

    private static Command Parse(NetDataReader r)
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
                var length = r.GetInt();
                var hash = Protocol.ReadBlob(r, HashBytes, out var declared) ?? throw new FormatException("Invalid tool table hash.");
                if (length is < 0 or > MaxBytes || (length == 0 ? declared != 0 : declared != HashBytes)) throw new FormatException("Invalid tool table result.");
                c = new(step, epoch, bench, default, 0, 0, Array.Empty<byte>(), length, hash);
                break;
            }
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
        if (At(c.Bench) is not { } use || use.Actor != requester) return "You're not using that tool table.";
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
        var error = Check(ctx.Op.Actor, Parse(payload));
        return error == null ? CommandVerdict.Accept : CommandVerdict.Reject(error);
    }

    public void Apply(in MutationContext ctx, NetDataReader payload)
    {
        var c = Parse(payload);
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
        var left = _uses[c.Bench];
        byte[]? data = null;
        if (c.Length > 0 && _chunks.TryGetValue(c.Bench, out var got) && got.Chunks.Count == got.Total)
        {
            data = got.Chunks.SelectMany(x => x).ToArray();
            if (data.Length != c.Length || !SHA256.HashData(data).AsSpan().SequenceEqual(c.Hash)) data = null;
        }
        _uses.Remove(c.Bench); _chunks.Remove(c.Bench);
        _native.Left(ctx, left, data);
    }
}
