using System.Diagnostics;
using PCBS2Coop.Network;
using PCBS2Coop.Steam;

// Explicit opt-in: changes this process's Steam presence and creates a private empty test lobby.
// No game/saves are opened, and no friends are invited or named in output.
if (!args.Contains("--steam")) { Console.WriteLine("Use --steam with the Steam client running to test App ID 480 locally."); return 0; }
var appId = 480u;
using var steam = new SteamService(appId, AppContext.BaseDirectory, Console.WriteLine);
if (!steam.Available) { Console.Error.WriteLine(steam.UnavailableReason); return 1; }
using (var competing = new SteamService(appId, AppContext.BaseDirectory, Console.WriteLine))
{
    if (competing.Available || !steam.Available) { Console.Error.WriteLine("Existing runtime was not preserved."); return 1; }
}
Console.WriteLine("PASS: competing runtime refused without shutting down the existing context.");
Console.WriteLine($"Steam initialized; friend count={steam.GetFriends().Count}.");
using var session = new CoopSession();
session.Log += Console.WriteLine;
var identity = new PeerIdentity(new PlayerId(Guid.NewGuid()), ModVersion.Value, new GameIdentity("smoke", "smoke"), new ContentIdentity(Array.Empty<string>(), "", 0));
bool complete = false;
string? failure = null;
steam.BeginHost(identity, (transport, error) =>
{
    failure = error;
    if (transport != null)
    {
        if (!session.Host(transport, identity, out var startError)) failure = startError;
        else session.World!.BeginGeneration("Steam smoke test");
    }
    complete = true;
});
var timer = Stopwatch.StartNew();
while ((!complete || timer.ElapsedMilliseconds < 1500) && timer.ElapsedMilliseconds < 35_000)
{
    steam.Poll(); session.Poll();
    if (session.IsRunning) steam.SetHostReady(true);
    Thread.Sleep(10);
}
if (!complete || failure != null) { Console.Error.WriteLine(failure ?? "Steam smoke test timed out."); return 1; }
if (!steam.HasLobby || !steam.CanInvite) { Console.Error.WriteLine("Steam lobby was not ready for invitations."); return 1; }
Console.WriteLine("PASS: private lobby, relay listener and friend enumeration. No invitation or remote route tested.");
session.Stop("Steam smoke test ended."); steam.Cancel();
if (steam.HasLobby || session.IsRunning) { Console.Error.WriteLine("Cleanup failed."); return 1; }
Console.WriteLine("PASS: session/lobby cleanup.");
return 0;
