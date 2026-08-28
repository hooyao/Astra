using System.Collections;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Astra.Core.Compaction;

/// <summary>
/// A fast, provider-neutral UTF-8 estimate. Three bytes per token is deliberately
/// conservative for mixed English/CJK agent traffic. Binary content uses a fixed
/// 2,000-token estimate, matching the source policy studied for D7.
/// </summary>
public sealed class RoughChatTokenEstimator : IChatTokenEstimator
{
    private const int MessageOverheadTokens = 4;
    private const int BinaryContentTokens = 2_000;

    public int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        long total = 0;
        foreach (var message in messages)
        {
            total += MessageOverheadTokens;
            total += EstimateText(message.Role.ToString());
            total += EstimateText(message.AuthorName);

            foreach (var content in message.Contents)
            {
                total += content switch
                {
                    TextContent text => EstimateText(text.Text),
                    FunctionCallContent call =>
                        EstimateText(call.Name) + EstimateText(call.CallId) + EstimateValue(call.Arguments),
                    FunctionResultContent result =>
                        EstimateText(result.CallId) + EstimateValue(result.Result),
                    DataContent => BinaryContentTokens,
                    UsageContent => 0,
                    _ => EstimateText(content.ToString()),
                };
            }
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    private static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return Math.Max(1, (Encoding.UTF8.GetByteCount(text) + 2) / 3);
    }

    private static int EstimateValue(object? value, int depth = 0)
    {
        if (value is null)
            return 1;
        if (depth >= 4)
            return 8;

        return value switch
        {
            string text => EstimateText(text),
            JsonElement json => EstimateText(json.GetRawText()),
            IDictionary<string, object?> dictionary => dictionary.Sum(
                pair => EstimateText(pair.Key) + EstimateValue(pair.Value, depth + 1)),
            IDictionary dictionary => EstimateDictionary(dictionary, depth + 1),
            IEnumerable sequence => EstimateSequence(sequence, depth + 1),
            IFormattable formattable => EstimateText(formattable.ToString(null, null)),
            _ => EstimateText(value.ToString()),
        };
    }

    private static int EstimateDictionary(IDictionary dictionary, int depth)
    {
        var total = 0;
        foreach (DictionaryEntry pair in dictionary)
            total += EstimateValue(pair.Key, depth) + EstimateValue(pair.Value, depth);
        return total;
    }

    private static int EstimateSequence(IEnumerable sequence, int depth)
    {
        var total = 0;
        foreach (var item in sequence)
            total += EstimateValue(item, depth);
        return total;
    }
}
