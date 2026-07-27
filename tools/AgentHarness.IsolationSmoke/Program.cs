using System.Diagnostics;
using AgentHarness.Isolation;

// Adversarial smoke test for the termination ladder. No test framework, no packages beyond
// AgentHarness.Isolation itself — this must be runnable on any box with the .NET SDK.
//
// The workload is deliberately hostile: it traps SIGTERM (ignores the polite ask) and forks a
// child that outlives its parent. That is the exact shape of the wedge this project exists to
// prevent (a live Telegram lane froze with aborted=false drained=false forceCleared=true
// released=0 after execution fell back to an embedded, non-killable runtime).
//
//   dotnet run --project tools/AgentHarness.IsolationSmoke

var allowFallback = true;
var factory = new IsolationBoundaryFactory(allowFallback);
Console.WriteLine($"isolation kind: {factory.SelectedKind}");

await using var boundary = factory.Create("smoke-" + Guid.NewGuid().ToString("N")[..8]);

// Newline-joined, not "; "-joined: `sleep 600 &` followed by `;` is a bash syntax error, and
// with stderr correctly drained you never see it — the workload simply never exists and the
// probe reports a clean box.
var script = string.Join("\n", new[]
{
    "trap '' TERM", // ignore SIGTERM outright
    "sleep 600 &",  // child that must also die
    "echo started",
    "wait",
});

var spec = new IsolatedProcessSpec(
    FileName: "bash",
    Arguments: new[] { "-c", script },
    WorkingDirectory: Environment.CurrentDirectory,
    Environment: new Dictionary<string, string>());

var handle = await boundary.StartAsync(spec, CancellationToken.None);
Console.WriteLine($"started pid {handle.Pid}");

await Task.Delay(500);

var liveBefore = await boundary.HasLiveProcessesAsync(CancellationToken.None);
Console.WriteLine($"live before termination: {liveBefore}");

var ladder = new StagedTermination(
    protocolGrace: TimeSpan.FromMilliseconds(300),
    sigtermGrace: TimeSpan.FromMilliseconds(700),
    verifyTimeout: TimeSpan.FromSeconds(2));

var sw = Stopwatch.StartNew();
var outcome = await ladder.TerminateAsync(
    boundary,
    handle.Exited,
    sendProtocolCancel: null, // no worker protocol in this harness-free probe
    CancellationToken.None);
sw.Stop();

Console.WriteLine($"stages: {string.Join(" -> ", outcome.StagesEntered)}");
Console.WriteLine($"confirmed gone: {outcome.ProcessesConfirmedGone}");
Console.WriteLine($"elapsed: {sw.ElapsedMilliseconds}ms");

var liveAfter = await boundary.HasLiveProcessesAsync(CancellationToken.None);
Console.WriteLine($"live after termination: {liveAfter}");

var pass = liveBefore && !liveAfter && outcome.ProcessesConfirmedGone;
Console.WriteLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
return pass ? 0 : 1;
