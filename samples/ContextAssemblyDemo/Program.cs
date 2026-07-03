using System.Security.Cryptography;
using System.Text;
using Astra.Core;
using Astra.Core.Context;
using ContextAssemblyDemo;
using Microsoft.Extensions.AI;

// D6 payoff demo. No LLM required: a capturing fake client records the exact context
// AgentLoop assembles each turn, and we print it so you can SEE the three layers
// behave the way the real Claude Code trace showed:
//   - the a+b system prefix hashes identically across turns (cache-stable);
//   - layer-c attachments appear/disappear per turn on the user message;
//   - a hung attachment provider is dropped at the deadline, the turn still sends.
//
// Run: dotnet run --project samples/ContextAssemblyDemo

static string Sha(string s) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();

static string SystemText(List<ChatMessage> turn) => turn.First(m => m.Role == ChatRole.System).Text ?? "";
static string LastUserText(List<ChatMessage> turn) => turn.Last(m => m.Role == ChatRole.User).Text ?? "";

Console.WriteLine("=== D6 Context Assembly — live demo (no LLM) ===\n");

// -------------------------------------------------------------------------
// PART 1 — layers a + b: byte-stable system prefix across 3 turns.
// b is the REAL git status of this Astra repo, memoized once.
// -------------------------------------------------------------------------
Console.WriteLine("PART 1: a (system prompt) + b (REAL git status, memoized) across 3 turns\n");

var model1 = new CapturingClient();
var loop1 = new AgentLoop(
    model1, tools: [], systemPrompt: "You are Astra, a coding agent.",
    sessionContext: new MemoizedSessionContext(new GitStatusContextProvider()));

for (var i = 1; i <= 3; i++)
    await foreach (var _ in loop1.SubmitAsync($"turn {i} question")) { }

for (var i = 0; i < model1.Calls.Count; i++)
{
    var sys = SystemText(model1.Calls[i]);
    Console.WriteLine($"  turn {i + 1}:  system prefix sha256[:12] = {Sha(sys)}   (len={sys.Length})");
}
var allSame = model1.Calls.Select(c => SystemText(c)).Distinct().Count() == 1;
Console.WriteLine($"\n  --> all three identical? {allSame}   (this is what the prompt cache needs)");
Console.WriteLine("  --> b ran the git subprocess ONCE; turns 2 and 3 reused the frozen snapshot.\n");
Console.WriteLine("  first 3 lines of the assembled system prefix:");
foreach (var line in SystemText(model1.Calls[0]).Split('\n').Take(6))
    Console.WriteLine($"      | {line}");

// -------------------------------------------------------------------------
// PART 2 — layer c: a periodic reminder appears every 2nd turn, on the USER
// message, without touching the system prefix. (msg[8]==[13]==[20] pattern.)
// -------------------------------------------------------------------------
Console.WriteLine("\n\nPART 2: c (periodic reminder every 2 turns) rides the user message\n");

var model2 = new CapturingClient();
var loop2 = new AgentLoop(
    model2, tools: [], systemPrompt: "You are Astra.",
    attachmentProviders: [new PeriodicReminderProvider("[reminder] track your tasks", everyNTurns: 2)]);

for (var i = 1; i <= 4; i++)
    await foreach (var _ in loop2.SubmitAsync($"turn {i}")) { }

for (var i = 0; i < model2.Calls.Count; i++)
{
    var user = LastUserText(model2.Calls[i]);
    var hasReminder = user.Contains("[reminder]");
    Console.WriteLine($"  turn {i + 1}:  user message {(hasReminder ? "HAS" : "no ")} attachment   sysPrefix={Sha(SystemText(model2.Calls[i]))}");
}
Console.WriteLine("\n  --> attachment appears turns 2 & 4 only; system prefix hash unchanged all 4 turns.");

// -------------------------------------------------------------------------
// PART 3 — layer c timeout: one fast provider + one hung provider, 200ms budget.
// The hung one is dropped; the turn still sends with the fast content.
// -------------------------------------------------------------------------
Console.WriteLine("\n\nPART 3: c timeout — a hung provider (30s) dropped at a 200ms deadline\n");

var model3 = new CapturingClient();
var loop3 = new AgentLoop(
    model3, tools: [], systemPrompt: "You are Astra.",
    attachmentProviders: [new PeriodicReminderProvider("[fast] ok", everyNTurns: 1), new HangingProvider()],
    attachmentDeadline: TimeSpan.FromMilliseconds(200));

var sw = System.Diagnostics.Stopwatch.StartNew();
await foreach (var _ in loop3.SubmitAsync("go")) { }
sw.Stop();

var u = LastUserText(model3.Calls[0]);
Console.WriteLine($"  turn sent after {sw.ElapsedMilliseconds}ms (NOT 30000ms — bounded by the deadline)");
Console.WriteLine($"  fast provider survived?  {u.Contains("[fast] ok")}");
Console.WriteLine($"  hung provider appeared?  {u.Contains("SHOULD-NEVER-APPEAR")}   (must be False)");
Console.WriteLine("\n  --> one slow source cannot hold the turn hostage; it is simply omitted this turn.\n");
