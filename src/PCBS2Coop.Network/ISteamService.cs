namespace PCBS2Coop.Network;

public sealed record SteamFriend(ulong Id, string Name, bool Online);

/// <summary>Optional Steam integration. Contains no Steam SDK or Unity types. Call on one main thread.</summary>
public interface ISteamService : IDisposable
{
    bool Available { get; }
    string UnavailableReason { get; }
    uint AppId { get; }
    bool Busy { get; }
    bool HasLobby { get; }
    bool CanInvite { get; }
    event Action<ulong>? InviteRequested;
    event Action<string>? Failed;
    void Poll();
    void BeginHost(PeerIdentity identity, Action<ISessionTransport?, string?> completed);
    void BeginJoin(ulong lobbyId, PeerIdentity identity, Action<ISessionTransport?, string?> completed);
    void SetHostReady(bool ready);
    void Cancel();
    bool OpenInviteOverlay();
    IReadOnlyList<SteamFriend> GetFriends();
    bool Invite(ulong friendId);
}
