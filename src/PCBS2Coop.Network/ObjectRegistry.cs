namespace PCBS2Coop.Network;

/// <summary>Hands out <see cref="NetObjectId"/>s on the host. IDs only grow; <see cref="Next"/> is saved with the world later.</summary>
public sealed class NetObjectIdAllocator
{
    private ulong _next = 1;

    /// <summary>The next ID that will be handed out. Set it when restoring a saved world.</summary>
    public ulong Next
    {
        get => _next;
        set
        {
            if (value == 0) throw new ArgumentOutOfRangeException(nameof(value), "0 is reserved for 'no object'.");
            _next = value;
        }
    }

    public NetObjectId Allocate() => new(_next++);
}

public enum ObjectLookup : byte
{
    /// <summary>The ID was never bound in this world (or the registry was cleared).</summary>
    Unknown = 0,
    Live = 1,
    /// <summary>The object existed and was deleted. Commands naming it must be rejected.</summary>
    Retired = 2,
}

/// <summary>
/// Maps network IDs to local objects for one world. Bindings and tombstones are cleared at a world reset.
/// Main thread only, like everything that touches Unity objects.
/// </summary>
public sealed class NetObjectRegistry<T> where T : class
{
    private readonly Dictionary<ulong, T> _live = new();
    private readonly Dictionary<T, ulong> _reverse = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ulong> _retired = new();

    public int Count => _live.Count;
    public int RetiredCount => _retired.Count;

    public void Bind(NetObjectId id, T obj)
    {
        if (id.IsNone) throw new ArgumentException("Cannot bind 'no object'.", nameof(id));
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        if (_retired.Contains(id.Value)) throw new InvalidOperationException($"{id} was retired and cannot be reused.");
        if (_live.ContainsKey(id.Value)) throw new InvalidOperationException($"{id} is already bound.");
        if (_reverse.ContainsKey(obj)) throw new InvalidOperationException($"Object is already bound to obj:{_reverse[obj]}.");
        _live[id.Value] = obj;
        _reverse[obj] = id.Value;
    }

    public ObjectLookup Lookup(NetObjectId id) =>
        _live.ContainsKey(id.Value) ? ObjectLookup.Live : _retired.Contains(id.Value) ? ObjectLookup.Retired : ObjectLookup.Unknown;

    public bool TryGet(NetObjectId id, out T obj) => _live.TryGetValue(id.Value, out obj!);

    public bool TryGetId(T obj, out NetObjectId id)
    {
        var ok = _reverse.TryGetValue(obj, out var v);
        id = new NetObjectId(v);
        return ok;
    }

    /// <summary>Removes the binding and remembers that the ID is dead. Returns false if it wasn't live.</summary>
    public bool Retire(NetObjectId id)
    {
        if (!_live.Remove(id.Value, out var obj)) return false;
        _reverse.Remove(obj);
        _retired.Add(id.Value);
        return true;
    }

    /// <summary>World reset: drop every binding and tombstone.</summary>
    public void Clear()
    {
        _live.Clear();
        _reverse.Clear();
        _retired.Clear();
    }
}
