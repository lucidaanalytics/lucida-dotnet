using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucida;

/// <summary>One chat message of a completion-mode AI Config variation.</summary>
public sealed record AiMessage(string Role, string Content);

/// <summary>A tool an agent-mode variation offers the model; <c>Parameters</c> is a JSON Schema object.</summary>
public sealed record AiTool(string Name, string? Description = null, JsonElement? Parameters = null);

/// <summary>
/// A rendered AI Config variation (LC-FLG-25, P-M49): completion mode (<c>Messages</c>) or agent mode
/// (<c>Instructions</c> plus <c>Tools</c>). <c>Enabled</c> is false when the flag served nothing and this is the
/// fallback. The SDK only selects and renders it; evaluating model output is Watchtower's job. Mirrors
/// <c>packages/sdk-core/src/ai-config.ts</c>.
/// </summary>
public sealed record AiConfig(
    string Model,
    IReadOnlyList<AiMessage>? Messages = null,
    string? Instructions = null,
    IReadOnlyList<AiTool>? Tools = null,
    string? Mode = null,
    string? Provider = null,
    JsonElement? Parameters = null,
    bool Enabled = false,
    string? Variation = null,
    int? Version = null)
{
    private static readonly Regex Var = new(@"\{\{\s*([\w.-]+)\s*\}\}", RegexOptions.Compiled);

    internal static string Fill(string s, IReadOnlyDictionary<string, string> vars) =>
        Var.Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The config served by <paramref name="result"/> (or <paramref name="fallback"/>) with <c>{{var}}</c> placeholders filled.</summary>
    public static AiConfig From(FlagResult? result, AiConfig fallback, IReadOnlyDictionary<string, string>? vars = null)
    {
        var v = vars ?? new Dictionary<string, string>();
        AiConfig b;
        if (result?.Variant is not null && result.Payload is { ValueKind: JsonValueKind.Object } p && Str(p, "model") is { } model)
        {
            var msgs = new List<AiMessage>();
            if (p.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    msgs.Add(new AiMessage(Str(m, "role") ?? "user", Str(m, "content") ?? ""));
            var tools = new List<AiTool>();
            if (p.TryGetProperty("tools", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var t in ts.EnumerateArray())
                    tools.Add(new AiTool(Str(t, "name") ?? "", Str(t, "description"),
                        t.TryGetProperty("parameters", out var tp) ? (JsonElement?)tp.Clone() : null));
            b = new AiConfig(model, msgs, Str(p, "instructions"), tools,
                Str(p, "mode") == "agent" ? "agent" : "completion", Str(p, "provider"),
                p.TryGetProperty("parameters", out var pp) && pp.ValueKind == JsonValueKind.Object ? (JsonElement?)pp.Clone() : null,
                true, result.Variant,
                p.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out var n) ? (int?)n : null);
        }
        else
        {
            b = fallback with
            {
                Enabled = false,
                Mode = fallback.Mode ?? (fallback.Instructions is not null ? "agent" : "completion"),
                Messages = fallback.Messages ?? Array.Empty<AiMessage>(),
                Tools = fallback.Tools ?? Array.Empty<AiTool>(),
            };
        }
        return b with
        {
            Messages = (b.Messages ?? Array.Empty<AiMessage>()).Select(m => m with { Content = Fill(m.Content, v) }).ToList(),
            Instructions = b.Instructions is null ? null : Fill(b.Instructions, v),
        };
    }
}
