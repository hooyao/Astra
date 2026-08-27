namespace Astra.Core.Context;

/// <summary>
/// Gathers layer-c attachments for one turn under a single shared deadline. Every
/// provider runs concurrently; whatever completes before the deadline contributes its
/// text, and anything still running (or throwing) is dropped. This is Astra's version
/// of Claude Code's <c>getAttachments()</c> running under one <c>AbortController</c>
/// with <c>setTimeout(ac =&gt; ac.abort(), 1000)</c> (attachments.ts).
/// </summary>
/// <remarks>
/// D6. Two design points worth stating:
/// <list type="number">
/// <item>One deadline for the <i>whole</i> gather, not per provider — the turn's
/// latency budget is a single number the user feels. A linked CTS combines the
/// caller's token with a timeout; when it fires, in-flight providers observe
/// cancellation.</item>
/// <item>A slow/hung provider must not delay the fast ones. We await each provider's
/// task defensively: on timeout or fault, that one result is dropped, the rest still
/// return. The gather's own wall-clock is therefore bounded by the deadline, not by
/// the slowest provider.</item>
/// </list>
/// The result preserves provider order (deterministic assembly), so the attachment
/// block is byte-stable given the same surviving inputs — it does not reorder by
/// completion time the way the D3 concurrent tool batch's events do.
/// </remarks>
public sealed class AttachmentGatherer(
    IReadOnlyList<IAttachmentProvider> providers,
    TimeSpan deadline)
{
    /// <summary>
    /// Run every provider concurrently under the shared <see cref="deadline"/> and
    /// return the surviving attachments in provider order. Never throws for a
    /// provider failure or timeout — layer c is best-effort. Still honors a caller
    /// cancellation that is NOT the deadline (that propagates).
    /// </summary>
    public async ValueTask<IReadOnlyList<Attachment>> GatherAsync(CancellationToken ct = default)
    {
        if (providers.Count == 0)
            return [];

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(deadline);
        var budget = timeoutCts.Token;

        // Kick every provider off concurrently, each wrapped so a throw/timeout maps
        // to a null result instead of faulting the whole gather.
        var tasks = new Task<string?>[providers.Count];
        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            tasks[i] = RunOneAsync(provider, budget);
        }

        var texts = await Task.WhenAll(tasks);

        // If the CALLER (not our deadline) cancelled, surface it rather than silently
        // returning a partial set — a real cancel is not "the attachments were slow".
        ct.ThrowIfCancellationRequested();

        var results = new List<Attachment>(providers.Count);
        for (var i = 0; i < providers.Count; i++)
            if (texts[i] is { Length: > 0 } text)
                results.Add(new Attachment(providers[i].Name, text));
        return results;
    }

    private static async Task<string?> RunOneAsync(IAttachmentProvider provider, CancellationToken budget)
    {
        try
        {
            return await provider.GetAsync(budget);
        }
        catch (OperationCanceledException)
        {
            return null; // deadline hit (or provider-internal cancel) -> drop this one
        }
        catch
        {
            return null; // provider fault -> drop this one, keep the rest (best-effort)
        }
    }
}

/// <summary>One surviving layer-c attachment: a provider name and its text for this turn.</summary>
public sealed record Attachment(string Name, string Text);
