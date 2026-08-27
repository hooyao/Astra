namespace Astra.Core.Context;

/// <summary>
/// A layer-c provider that injects a fixed reminder every N turns and nothing in
/// between. This is the mechanism behind the task-reminder seen in a real Claude Code
/// request (the identical 421-char <c>role:system</c> message re-appearing at
/// messages[8] == [13] == [20], driven by <c>TODO_REMINDER_CONFIG</c>).
/// </summary>
/// <remarks>
/// D6. Kept deliberately simple: it proves the per-turn attachment seam and the
/// "recompute every turn, emit periodically" pattern without pulling in Claude Code's
/// 30+ attachment types. The turn counter lives here (the provider is stateful across
/// turns), while the <i>content</i> is transient — exactly the c-layer shape.
/// </remarks>
public sealed class PeriodicReminderProvider(string reminderText, int everyNTurns = 5) : IAttachmentProvider
{
    private int _turn;

    public string Name => "reminder";

    public ValueTask<string?> GetAsync(CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref _turn); // 1-based turn count
        var emit = everyNTurns > 0 && n % everyNTurns == 0;
        return new ValueTask<string?>(emit ? reminderText : null);
    }
}
