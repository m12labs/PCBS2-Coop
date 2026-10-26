namespace PCBS2Coop.Network;

public enum SessionDelivery { ReliableOrdered, LatestState }

/// <summary>One host and one guest. All callbacks run from Poll on the caller's thread.
/// Send owns/copies its input before returning; false means no bytes were accepted.
/// Bulk sends may be retried, other overflow must terminate the link explicitly.</summary>
public interface ISessionTransport : IDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    int PingMs { get; }
    Func<byte[], string?>? ValidateHello { get; set; }
    event Action? Connected;
    event Action<string>? Disconnected;
    event Action<byte[], SessionDelivery>? Received;
    event Action<string>? Log;
    bool Start(Role role, byte[] hello, out string error);
    void Poll();
    bool Send(byte[] payload, SessionDelivery delivery, bool bulk = false);
    void Disconnect(string reason);
}
