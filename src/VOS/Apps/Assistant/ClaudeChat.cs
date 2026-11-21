using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using VOS.Core.Assistant;

namespace VOS.Apps.Assistant;

/// <summary>What the caller learns while a Claude turn streams. Callbacks run on the caller's synchronization context.</summary>
public sealed class ClaudeTurnEvents
{
    /// <summary>A new text block begins.</summary>
    public Action? TextStarted { get; init; }
    public Action<string>? TextDelta { get; init; }
    /// <summary>A tool call begins streaming (its input is not complete yet).</summary>
    public Action<string>? ToolStarting { get; init; }
    /// <summary>The model declined and another model continued the answer (server-side fallback). Argument: the model id.</summary>
    public Action<string>? FellBack { get; init; }
    /// <summary>The partial output so far is void: the request is being sent again.</summary>
    public Action? Retrying { get; init; }
    /// <summary>Runs one validated or rejected tool call; returns the tool_result content.</summary>
    public required Func<string, IReadOnlyDictionary<string, JsonElement>, Task<ToolOutcome>> RunTool { get; init; }
}

public sealed record ToolOutcome(string Content, bool IsError = false);

public enum TurnEnd { Completed, Refused, CutOff, ContextFull, Failed, Cancelled }

public sealed record TurnResult(TurnEnd End, string? Detail = null);

/// <summary>
/// One conversation with Claude through the official SDK: streaming, client tools with eager input streaming, and
/// server-side refusal fallbacks. The history only ever holds complete, valid turns: a turn that fails, is cancelled
/// or is refused is rolled back, so the next request is always well-formed.
/// <para>The conversation is append-only. The system prompt and the tool set are fixed for its lifetime (thinking
/// blocks are bound to the exact prefix that produced them), so per-turn context such as the time travels inside the
/// new user message, and a change of settings starts a new conversation instead of editing this one.</para>
/// </summary>
public sealed class ClaudeChat : IDisposable
{
    public const int MaxTokens = 16000;
    /// <summary>Tool rounds allowed for one user message before the turn stops asking for more.</summary>
    public const int MaxToolRounds = 8;

    private readonly AnthropicClient _client;
    private readonly List<BetaMessageParam> _history = [];

    public string Model { get; }
    public string System { get; }
    public IReadOnlyList<BetaToolUnion> Tools { get; }
    public bool HasHistory => _history.Count > 0;
    /// <summary>For tests and diagnostics: the conversation as it will be sent.</summary>
    public IReadOnlyList<BetaMessageParam> History => _history;

    public ClaudeChat(string apiKey, string model, string system, IReadOnlyList<BetaToolUnion>? tools = null, HttpClient? http = null, string? baseUrl = null)
    {
        Model = model;
        System = system;
        Tools = tools ?? [];
        var options = new ClientOptions { ApiKey = apiKey, MaxRetries = 2 };
        if (http != null) options.HttpClient = http;
        if (baseUrl != null) options.BaseUrl = baseUrl;
        _client = new AnthropicClient(options);
    }

    /// <summary>Server-side fallback ("default" routing) is offered for Opus and Sonnet; Haiku has none, so the
    /// parameter is left out there.</summary>
    public static bool SupportsServerFallback(string model) => !model.Contains("haiku", StringComparison.OrdinalIgnoreCase);

    /// <summary>"claude-opus-4-8" → "Claude Opus 4.8".</summary>
    public static string DisplayName(string model)
    {
        var parts = model.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "claude") return model;
        var family = string.Join(' ', parts.Skip(1).TakeWhile(p => !char.IsDigit(p[0])).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        var version = string.Join('.', parts.SkipWhile(p => !char.IsDigit(p[0])).TakeWhile(p => p.All(char.IsDigit) && p.Length <= 2));
        return version.Length > 0 ? $"Claude {family} {version}" : $"Claude {family}";
    }

    public static List<BetaToolUnion> ToTools(IEnumerable<ToolSpec> specs) =>
    [
        .. specs.Select(s => (BetaToolUnion)new BetaTool
        {
            Name = s.Name,
            Description = s.Description,
            InputSchema = new InputSchema { Properties = s.Properties, Required = [.. s.Required] },
            // Inputs stream as generated; RunTool validates every input before anything runs.
            EagerInputStreaming = true,
        }),
    ];

    public MessageCreateParams BuildRequest()
    {
        var betas = new List<ApiEnum<string, AnthropicBeta>> { AnthropicBeta.ThinkingBindingControls2026_08_01 };
        var request = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = MaxTokens,
            System = System,
            Messages = [.. _history],
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            // Adaptive thinking (the model's default) with an explicit binding policy: should a replayed thinking block
            // ever fail the conversation check, the API drops it instead of rejecting the request. The history is
            // append-only, so this is a safety net rather than the normal path.
            Thinking = new BetaThinkingConfigAdaptive
            {
                BlockBinding = new BetaThinkingBlockBinding { PrefixMismatchBehavior = BetaThinkingPrefixMismatchBehavior.DropBlock },
            },
        };
        if (Tools.Count > 0) request = request with { Tools = Tools };
        if (SupportsServerFallback(Model))
        {
            // A declined request is re-served by the model Anthropic recommends for that refusal category.
            request = request with { Fallbacks = new Default() };
            betas.Add(AnthropicBeta.ServerSideFallback2026_07_01);
        }
        return request with { Betas = betas };
    }

    /// <summary>Sends one user message and runs the tool loop until Claude answers, declines, or a limit is hit.
    /// <paramref name="context"/> (such as the local time) is sent as a separate text block before the message.</summary>
    public async Task<TurnResult> SendAsync(string text, string? context, ClaudeTurnEvents events, CancellationToken ct)
    {
        int checkpoint = _history.Count;
        var content = new List<BetaContentBlockParam>();
        if (!string.IsNullOrWhiteSpace(context)) content.Add(new BetaTextBlockParam { Text = context });
        content.Add(new BetaTextBlockParam { Text = text });
        _history.Add(new BetaUserMessageParam { Content = content });
        try
        {
            for (int round = 1; ; round++)
            {
                var message = await StreamOnceAsync(BuildRequest(), events, ct);
                if (message.InputTransformations is { Count: > 0 } dropped)
                    VOS.Core.Log.Info("Claude dropped replayed thinking blocks", new { count = dropped.Count, type = dropped[0].Type.Raw() });
                var stop = message.StopReason?.Raw();
                switch (stop)
                {
                    case "refusal":
                        // A declined turn is discarded whole, partial output included; nothing in it is executed.
                        Rollback(checkpoint);
                        return new(TurnEnd.Refused, message.StopDetails?.Explanation);
                    case "model_context_window_exceeded":
                        Rollback(checkpoint);
                        return new(TurnEnd.ContextFull);
                    case "max_tokens":
                        // A tool input cut off by the limit can still parse as a valid object: never run this turn's tools.
                        var (textOnly, _) = Echo(message, textOnly: true);
                        if (textOnly.Any(b => b.TryPickText(out _))) _history.Add(new BetaAssistantMessageParam { Content = textOnly });
                        else Rollback(checkpoint);
                        return new(TurnEnd.CutOff);
                }

                var (blocks, toolUses) = Echo(message, textOnly: false);
                if (blocks.Count == 0) { Rollback(checkpoint); return new(TurnEnd.Failed, "Claude returned an empty answer."); }
                _history.Add(new BetaAssistantMessageParam { Content = blocks });
                if (stop != "tool_use" || toolUses.Count == 0) return new(TurnEnd.Completed);

                // One tool_result per tool_use, all in one user message.
                var results = new List<BetaContentBlockParam>();
                foreach (var use in toolUses)
                {
                    ToolOutcome outcome;
                    if (round > MaxToolRounds) outcome = new("Stopped: too many actions for one request. Tell the user what was done so far.", true);
                    else
                    {
                        try { outcome = await events.RunTool(use.Name, use.Input); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { outcome = new("The action failed: " + ex.Message, true); }
                    }
                    results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = outcome.Content, IsError = outcome.IsError ? true : null });
                }
                _history.Add(new BetaUserMessageParam { Content = results });
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Rollback(checkpoint);
            return new(TurnEnd.Cancelled);
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            Rollback(checkpoint);
            VOS.Core.Log.Warn("Claude request failed", ex, new { type = ex.GetType().Name });
            return new(TurnEnd.Failed, message);
        }
    }

    private async Task<BetaMessage> StreamOnceAsync(MessageCreateParams request, ClaudeTurnEvents events, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var aggregator = new BetaMessageContentAggregator();
            try
            {
                await foreach (var ev in aggregator.CollectAsync(_client.Beta.Messages.CreateStreaming(request, ct)).WithCancellation(ct))
                {
                    if (ev.TryPickContentBlockStart(out var start))
                    {
                        if (start.ContentBlock.TryPickBetaText(out _)) events.TextStarted?.Invoke();
                        else if (start.ContentBlock.TryPickBetaToolUse(out var use)) events.ToolStarting?.Invoke(use.Name);
                        else if (start.ContentBlock.TryPickBetaFallback(out var fallback)) events.FellBack?.Invoke(fallback.To.Model.Raw());
                    }
                    else if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                        events.TextDelta?.Invoke(text.Text);
                }
                return aggregator.Message();
            }
            catch (JsonException) when (attempt == 0 && !ct.IsCancellationRequested)
            {
                // Streamed tool input the SDK could not parse at all: there is no tool_use id to answer, so ask once more.
                events.Retrying?.Invoke();
            }
        }
    }

    /// <summary>
    /// Rebuilds the assistant turn for the history. After a mid-output fallback, thinking and tool calls from before
    /// the last fallback block belong to the model that declined and are left out; text and the fallback blocks stay in
    /// place. Only tool calls after the boundary are returned for execution.
    /// </summary>
    public static (List<BetaContentBlockParam> Blocks, List<BetaToolUseBlock> ToolUses) Echo(BetaMessage message, bool textOnly)
    {
        var blocks = new List<BetaContentBlockParam>();
        var uses = new List<BetaToolUseBlock>();
        int boundary = -1;
        for (int i = 0; i < message.Content.Count; i++) if (message.Content[i].TryPickFallback(out _)) boundary = i;
        for (int i = 0; i < message.Content.Count; i++)
        {
            var block = message.Content[i];
            if (block.TryPickText(out var text))
            {
                if (text.Text.Length > 0) blocks.Add(new BetaTextBlockParam { Text = text.Text });
            }
            else if (block.TryPickFallback(out var fallback))
            {
                blocks.Add(new BetaFallbackBlockParam
                {
                    From = new BetaFallbackInfoParam { Model = fallback.From.Model },
                    To = new BetaFallbackInfoParam { Model = fallback.To.Model },
                    Trigger = fallback.RawData.TryGetValue("trigger", out var trigger) ? trigger : (JsonElement?)null,
                });
            }
            else if (textOnly || i < boundary) continue;
            else if (block.TryPickThinking(out var thinking))
                blocks.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature }); // signature unchanged
            else if (block.TryPickRedactedThinking(out var redacted))
                blocks.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            else if (block.TryPickToolUse(out var use))
            {
                blocks.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                uses.Add(use);
            }
        }
        return (blocks, uses);
    }

    private void Rollback(int count)
    {
        if (_history.Count > count) _history.RemoveRange(count, _history.Count - count);
    }

    /// <summary>A message for the user, or null for exceptions that are not request failures (those propagate).</summary>
    private string? Describe(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => "The Claude API key was not accepted. Check it in Settings › Apps › Assistant.",
        AnthropicPaymentRequiredException => "The Anthropic account has no available credit. Check billing in the Claude Console.",
        AnthropicForbiddenException => $"This API key is not allowed to use {DisplayName(Model)}.",
        AnthropicNotFoundException => $"{DisplayName(Model)} is not available for this API key. Choose another model in Settings › Apps › Assistant.",
        AnthropicRateLimitException => "Claude's rate limit was reached. Try again in a moment.",
        AnthropicOverloadedException or Anthropic5xxException => "Claude is temporarily unavailable. Try again shortly.",
        AnthropicBadRequestException bad => "The request was not accepted: " + ApiErrorMessage(bad),
        AnthropicApiException api => $"Claude returned an error ({(int)api.StatusCode}): " + ApiErrorMessage(api),
        AnthropicSseException => "The answer was interrupted. Try again.",
        AnthropicIOException => "Could not reach api.anthropic.com. Check your internet connection.",
        AnthropicException => "The request to Claude failed: " + ex.Message,
        JsonException => "Claude's reply could not be read. Try again.",
        _ => null,
    };

    private static string ApiErrorMessage(AnthropicApiException ex)
    {
        try
        {
            using var doc = JsonDocument.Parse(ex.ResponseBody ?? "");
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } s) return s;
        }
        catch (JsonException) { }
        return ex.Message;
    }

    public void Dispose() => _client.Dispose();
}
