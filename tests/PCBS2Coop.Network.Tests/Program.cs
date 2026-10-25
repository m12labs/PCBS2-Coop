using System.Diagnostics;
using PCBS2Coop.Network;

// Loopback acceptance test: host + client in one process on 127.0.0.1.
var failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

bool PumpUntil(Func<bool> done, params CoopSession[] sessions)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < 3000)
    {
        foreach (var s in sessions) s.Poll();
        if (done()) return true;
        Thread.Sleep(5);
    }
    return false;
}

const int port = 47777;
using var host = new CoopSession();
using var client = new CoopSession();
PlayerState? atHost = null, atClient = null;
string? clientDrop = null;
var hostConnected = false;
host.PeerConnected += () => hostConnected = true;
host.RemoteState += s => atHost = s;
client.RemoteState += s => atClient = s;
client.PeerDisconnected += r => clientDrop = r;

Check(host.Host(port, out _), "host binds port");
Check(!new CoopSession().Host(port, out var busy) && busy.Contains("port"), "second host on same port reports an error");
Check(!client.Join("not-an-ip", port, out _), "invalid address rejected before connecting");
Check(client.Join("127.0.0.1", port, out _), "client starts connecting");
Check(PumpUntil(() => hostConnected && client.HasPeer, host, client), "both sides connected");

client.SendState(new PlayerState(1, 2, 3, 90, true));
host.SendState(new PlayerState(-4, 0, 5, 180, true));
Check(PumpUntil(() => atHost != null && atClient != null, host, client), "states exchanged");
Check(atHost == new PlayerState(1, 2, 3, 90, true), "host received client state intact");
Check(atClient == new PlayerState(-4, 0, 5, 180, true), "client received host state intact");

using var third = new CoopSession();
string? thirdDrop = null;
third.PeerDisconnected += r => thirdDrop = r;
third.Join("127.0.0.1", port, out _);
Check(PumpUntil(() => thirdDrop != null, host, client, third), "third player rejected (two-player session)");

host.Stop();
Check(PumpUntil(() => clientDrop != null, client), $"client notified when host stops ({clientDrop})");

using var lonely = new CoopSession();
string? noHost = null;
lonely.PeerDisconnected += r => noHost = r;
lonely.Join("127.0.0.1", port + 1, out _);
var sw = Stopwatch.StartNew();
while (noHost == null && sw.ElapsedMilliseconds < 10000) { lonely.Poll(); Thread.Sleep(10); }
Check(noHost != null, $"join with no host fails with a reason ({noHost})");

Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) failed.");
return failures == 0 ? 0 : 1;
