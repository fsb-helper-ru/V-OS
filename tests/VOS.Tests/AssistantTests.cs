using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VOS.Apps.Assistant;
using VOS.Core.Assistant;
using Xunit;

namespace VOS.Tests;

public class AssistantActionTests
{
    private static readonly (string id, string name)[] Workspaces = [("ws-work", "Work"), ("ws-personal", "Personal")];

    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    private static AssistantAction Valid(string tool, object input)
    {
        Assert.True(AssistantActions.TryValidate(tool, Input(input), Workspaces, out var action, out var error, out _), error);
        return action;
    }

    private static (string error, bool malformed) Invalid(string tool, object input)
    {
        Assert.False(AssistantActions.TryValidate(tool, Input(input), Workspaces, out _, out var error, out var malformed));
        Assert.False(string.IsNullOrWhiteSpace(error));
        return (error, malformed);
    }

    [Fact]
    public void Timer_input_is_checked_and_mapped_to_the_shell_command()
    {
        var a = Valid("start_timer", new { seconds = 300, label = "Tea" });
        Assert.Equal("timer:300:Tea", a.Command);
        Assert.Contains("5 min", a.Summary);
        Assert.Equal("timer:90", Valid("start_timer", new { seconds = "90" }).Command); // numeric string accepted
        Assert.True(Invalid("start_timer", new { seconds = 0 }).malformed);
        Assert.True(Invalid("start_timer", new { seconds = 100_000 }).malformed);
        Assert.True(Invalid("start_timer", new { seconds = 2.5 }).malformed);
        Assert.True(Invalid("start_timer", new { label = "x" }).malformed);
        // Control characters (the shell's argument separator among them) never reach a command.
        Assert.Equal("timer:60:Teax", Valid("start_timer", new { seconds = 60, label = "Tea\u001Fx" }).Command);
    }

    [Fact]
    public void Urls_are_limited_to_http_and_https()
    {
        Assert.Equal("url:https://example.com/", Valid("open_website", new { url = "https://example.com" }).Command);
        foreach (var bad in new[] { "javascript:alert(1)", "file:///C:/Windows/System32/calc.exe", "ms-settings:display", "example.com", "https://" })
        {
            var (_, malformed) = Invalid("open_website", new { url = bad });
            Assert.False(malformed);
        }
    }

    [Fact]
    public void Workspaces_must_exist()
    {
        Assert.Equal("workspace:ws-work", Valid("switch_workspace", new { name = "work" }).Command);
        var (error, malformed) = Invalid("switch_workspace", new { name = "Holiday" });
        Assert.False(malformed);
        Assert.Contains("Personal", error);
    }

    [Fact]
    public void Volume_power_and_enums_validate()
    {
        Assert.Equal("volume:mute", Valid("set_volume", new { mute = true }).Command);
        Assert.Equal("volume:30", Valid("set_volume", new { level = 30 }).Command);
        Assert.Equal("volume:unmute", Valid("set_volume", new { mute = false }).Command);
        Invalid("set_volume", new { });
        Invalid("set_volume", new { level = 101 });
        Invalid("set_volume", new { mute = "yes" });

        Assert.True(Valid("power", new { action = "sleep" }).NeedsConfirmation);
        Assert.False(Valid("power", new { action = "lock" }).NeedsConfirmation);
        Invalid("power", new { action = "format" });

        Assert.Equal("theme:dark", Valid("set_theme", new { mode = "Dark" }).Command);
        Assert.Equal("media:playpause", Valid("media_control", new { action = "toggle" }).Command);
        Assert.Equal("perf:minimal", Valid("set_performance_profile", new { profile = "minimal" }).Command);
        Assert.Equal("settings:privacy", Valid("open_settings", new { section = "privacy" }).Command);
        Assert.Equal("settings:", Valid("open_settings", new { }).Command);
        Assert.Equal("focus:on", Valid("set_focus", new { on = true }).Command);
    }

    [Fact]
    public void Notes_keep_line_breaks_and_weather_is_answered_in_app()
    {
        var note = Valid("create_note", new { text = "Line 1\r\nLine 2" });
        Assert.Equal("app:notes|new=1\u001Ftext=Line 1\nLine 2", note.Command);
        var weather = Valid("get_weather", new { city = "Paris" });
        Assert.Equal(ActionKind.Weather, weather.Kind);
        Assert.Equal("Paris", weather.City);
        Assert.Null(Valid("get_weather", new { }).City);
    }

    [Fact]
    public void Unknown_tools_are_rejected_and_invalid_input_is_reported_as_json()
    {
        var (_, malformed) = Invalid("delete_everything", new { });
        Assert.False(malformed);
        var content = AssistantActions.InvalidInputContent(Input(new { seconds = "abc\"" }), "bad");
        using var doc = JsonDocument.Parse(content);
        Assert.Contains("abc", doc.RootElement.GetProperty("INVALID_JSON").GetString());
        Assert.Equal("bad", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Tool_definitions_are_consistent()
    {
        var tools = AssistantActions.Tools(["Work", "Personal"]);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        foreach (var t in tools)
        {
            Assert.All(t.Required, r => Assert.True(t.Properties.ContainsKey(r), $"{t.Name}.{r}"));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        }
        Assert.Contains("Work, Personal", tools.Single(t => t.Name == "switch_workspace").Description);
    }
}

public class MarkdownTests
{
    [Fact]
    public void Blocks_are_recognised()
    {
        var blocks = Markdown.Parse("# Title\nFirst line\nsecond line\n\n- one\n  - nested\n2. two\n> quote\n---\n```cs\nvar x = 1;\n  indented\n```\n| a | b |\n| 1 | 2 |");
        Assert.Equal(
            [MdBlockKind.Heading, MdBlockKind.Paragraph, MdBlockKind.Bullet, MdBlockKind.Bullet, MdBlockKind.Numbered, MdBlockKind.Quote, MdBlockKind.Rule, MdBlockKind.Code, MdBlockKind.Code],
            blocks.Select(b => b.Kind));
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("First line\nsecond line", blocks[1].Inlines.Single().Text);
        Assert.Equal(1, blocks[3].Level);
        Assert.Equal("2.", blocks[4].Marker);
        Assert.Equal("var x = 1;\n  indented", blocks[7].Code);
        Assert.Equal("| a | b |\n| 1 | 2 |", blocks[8].Code);
    }

    [Fact]
    public void Inline_styles_and_safe_links()
    {
        var inl = Markdown.ParseInlines("Use **bold**, *italic*, `code` and [docs](https://example.com/x).");
        Assert.Equal([MdInlineKind.Text, MdInlineKind.Bold, MdInlineKind.Text, MdInlineKind.Italic, MdInlineKind.Text, MdInlineKind.Code, MdInlineKind.Text, MdInlineKind.Link, MdInlineKind.Text],
            inl.Select(i => i.Kind));
        Assert.Equal("https://example.com/x", inl[7].Url);

        var unsafeLink = Markdown.ParseInlines("[run](javascript:alert(1))");
        Assert.DoesNotContain(unsafeLink, i => i.Kind == MdInlineKind.Link);

        Assert.All(Markdown.ParseInlines("snake_case_name and 2 * 3 * 4"), i => Assert.Equal(MdInlineKind.Text, i.Kind));
        Assert.Equal("a*b", string.Concat(Markdown.ParseInlines(@"a\*b").Select(i => i.Text)));
    }

    [Fact]
    public void Plain_text_copy_drops_markup()
    {
        var text = Markdown.ToPlainText(Markdown.Parse("**Steps**\n\n1. Open *Files*\n- `cmd`"));
        Assert.Equal("Steps\n1. Open Files\n• cmd", text);
    }
}

public class OllamaTests
{
    [Fact]
    public void Stream_lines_parse()
    {
        Assert.Equal(("Hel", false, (string?)null), OllamaClient.ParseLine("{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"Hel\"},\"done\":false}"));
        Assert.True(OllamaClient.ParseLine("{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}").Done);
        Assert.Equal("model 'x' not found", OllamaClient.ParseLine("{\"error\":\"model 'x' not found\"}").Error);
        Assert.Equal((null, false, null), OllamaClient.ParseLine("not json"));
    }

    [Fact]
    public void Endpoints_are_classified()
    {
        Assert.True(OllamaClient.IsLoopback("http://localhost:11434"));
        Assert.True(OllamaClient.IsLoopback("http://127.0.0.1:11434"));
        Assert.False(OllamaClient.IsLoopback("http://192.168.1.20:11434"));
        Assert.False(OllamaClient.IsValidEndpoint("ftp://localhost"));
        Assert.True(OllamaClient.IsValidEndpoint("https://ollama.example.org"));
    }
}

/// <summary>
/// The Claude conversation driven through the real SDK against a fake HTTP handler: request shape, the tool loop,
/// refusals, length limits, the fallback boundary and error handling. No network, no key, no cost.
/// </summary>
public class ClaudeChatTests
{
    private sealed class FakeApi(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responses);
        public readonly List<(string Url, string Betas, JsonNode Body)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var betas = request.Headers.TryGetValues("anthropic-beta", out var v) ? string.Join(",", v) : "";
            Requests.Add((request.RequestUri!.ToString(), betas, JsonNode.Parse(body)!));
            return _responses.Dequeue()();
        }
    }

    private static Func<HttpResponseMessage> Stream(params object[] events) => () =>
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            var json = JsonSerializer.Serialize(e);
            var type = JsonNode.Parse(json)!["type"]!.GetValue<string>();
            sb.Append("event: ").Append(type).Append('\n').Append("data: ").Append(json).Append("\n\n");
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    };

    private static object Start(string model = "claude-opus-5-5") => new
    {
        type = "message_start",
        message = new { id = "msg_1", type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 12, output_tokens = 1 } },
    };
    private static object BlockStart(int index, object block) => new { type = "content_block_start", index, content_block = block };
    private static object Delta(int index, object delta) => new { type = "content_block_delta", index, delta };
    private static object BlockStop(int index) => new { type = "content_block_stop", index };
    private static object End(string stop, object? details = null) => new { type = "message_delta", delta = new { stop_reason = stop, stop_sequence = (string?)null, stop_details = details }, usage = new { output_tokens = 20 } };
    private static object Stop() => new { type = "message_stop" };

    private static object[] Thinking(int i, string signature) =>
        [BlockStart(i, new { type = "thinking", thinking = "", signature = "" }), Delta(i, new { type = "signature_delta", signature }), BlockStop(i)];
    private static object[] Text(int i, string text) =>
        [BlockStart(i, new { type = "text", text = "" }), Delta(i, new { type = "text_delta", text }), BlockStop(i)];
    private static object[] ToolUse(int i, string id, string name, string json) =>
        [BlockStart(i, new { type = "tool_use", id, name, input = new { }, caller = new { type = "direct" } }), Delta(i, new { type = "input_json_delta", partial_json = json }), BlockStop(i)];
    private static object[] Fallback(int i, string from, string to) =>
        [BlockStart(i, new { type = "fallback", from = new { model = from }, to = new { model = to }, trigger = new { type = "refusal", category = "cyber" } }), BlockStop(i)];

    private static object[] Events(params object[][] parts) => parts.SelectMany(p => p).ToArray();

    private static (ClaudeChat chat, FakeApi api) Create(string model, params Func<HttpResponseMessage>[] responses)
    {
        var api = new FakeApi(responses);
        var tools = ClaudeChat.ToTools(AssistantActions.Tools(["Work"]));
        return (new ClaudeChat("sk-ant-test", model, "You are a test.", tools, new HttpClient(api)), api);
    }

    private static ClaudeTurnEvents Recorder(List<string> log, Func<string, IReadOnlyDictionary<string, JsonElement>, ToolOutcome>? tool = null) => new()
    {
        TextDelta = t => log.Add("text:" + t),
        FellBack = m => log.Add("fallback:" + m),
        RunTool = (name, input) => { log.Add($"tool:{name}:{JsonSerializer.Serialize(input)}"); return Task.FromResult(tool?.Invoke(name, input) ?? new ToolOutcome("Done.")); },
    };

    [Fact]
    public async Task Request_opts_into_fallbacks_binding_policy_and_eager_tool_input()
    {
        var (chat, api) = Create("claude-opus-5-5", Stream(Events([Start()], Text(0, "Hi"), [End("end_turn"), Stop()])));
        var log = new List<string>();
        var result = await chat.SendAsync("Hello", "[Local time: test]", Recorder(log), CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, result.End);
        Assert.Equal(["text:Hi"], log);
        var (url, betas, body) = api.Requests.Single();
        Assert.Contains("/v1/messages", url);
        Assert.Contains("server-side-fallback-2026-07-01", betas);
        Assert.Contains("thinking-binding-controls-2026-08-01", betas);
        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("default", body["fallbacks"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("drop_block", body["thinking"]!["block_binding"]!["prefix_mismatch_behavior"]!.GetValue<string>());
        Assert.Equal("medium", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(body["tool_choice"]);
        var tools = body["tools"]!.AsArray();
        Assert.NotEmpty(tools);
        Assert.All(tools, t => Assert.True(t!["eager_input_streaming"]!.GetValue<bool>()));
        // The time travels inside the new user message, so the system prompt never changes during a conversation.
        Assert.Equal("You are a test.", body["system"]!.GetValue<string>());
        var first = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("[Local time: test]", first[0]!["text"]!.GetValue<string>());
        Assert.Equal("Hello", first[1]!["text"]!.GetValue<string>());
        Assert.Equal(2, chat.History.Count);
    }

    [Fact]
    public async Task Tool_loop_echoes_thinking_and_returns_one_result_per_call()
    {
        var (chat, api) = Create("claude-opus-5-5",
            Stream(Events([Start()], Thinking(0, "sig-1"), Text(1, "Starting it."), ToolUse(2, "toolu_1", "start_timer", "{\"seconds\": 300}"), [End("tool_use"), Stop()])),
            Stream(Events([Start()], Text(0, "Your timer is running."), [End("end_turn"), Stop()])));
        var log = new List<string>();
        var result = await chat.SendAsync("Timer 5 min", null, Recorder(log), CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, result.End);
        Assert.Contains("tool:start_timer:{\"seconds\":300}", log);
        Assert.Equal(2, api.Requests.Count);
        var messages = api.Requests[1].Body["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        var assistant = messages[1]!["content"]!.AsArray();
        Assert.Equal(["thinking", "text", "tool_use"], assistant.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("sig-1", assistant[0]!["signature"]!.GetValue<string>());
        Assert.Equal(300, assistant[2]!["input"]!["seconds"]!.GetValue<int>());
        var results = messages[2]!["content"]!.AsArray();
        Assert.Equal("tool_result", results.Single()!["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", results.Single()!["tool_use_id"]!.GetValue<string>());
        Assert.Equal(4, chat.History.Count);
    }

    [Fact]
    public async Task Refusal_runs_no_tools_and_leaves_no_trace_in_history()
    {
        var (chat, _) = Create("claude-opus-5-5",
            Stream(Events([Start()], Text(0, "Sure, "), ToolUse(1, "toolu_1", "power", "{\"action\": \"shut"), [End("refusal", new { type = "refusal", category = "cyber", explanation = "Declined by policy." }), Stop()])));
        var log = new List<string>();
        var result = await chat.SendAsync("Do something risky", null, Recorder(log), CancellationToken.None);

        Assert.Equal(TurnEnd.Refused, result.End);
        Assert.Equal("Declined by policy.", result.Detail);
        Assert.DoesNotContain(log, l => l.StartsWith("tool:", StringComparison.Ordinal));
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task Length_limit_keeps_the_text_but_never_runs_a_cut_off_tool_call()
    {
        var (chat, _) = Create("claude-opus-5-5",
            Stream(Events([Start()], Text(0, "Here goes"), ToolUse(1, "toolu_1", "create_note", "{\"text\": \"unfinished"), [End("max_tokens"), Stop()])));
        var log = new List<string>();
        var result = await chat.SendAsync("Write a long note", null, Recorder(log), CancellationToken.None);

        Assert.Equal(TurnEnd.CutOff, result.End);
        Assert.DoesNotContain(log, l => l.StartsWith("tool:", StringComparison.Ordinal));
        Assert.Equal(2, chat.History.Count);
        Assert.True(chat.History[1].TryPickAssistant(out var assistant));
        Assert.True(assistant!.Content.TryPickBetaContentBlockParams(out var blocks));
        Assert.All(blocks!, b => Assert.True(b.TryPickText(out _)));
    }

    [Fact]
    public async Task Fallback_boundary_drops_the_declined_models_thinking_and_tool_calls()
    {
        var (chat, _) = Create("claude-opus-5-5",
            Stream(Events([Start()], Thinking(0, "sig-a"), ToolUse(1, "toolu_a", "open_app", "{\"name\": \"files\"}"), Fallback(2, "claude-opus-5-5", "claude-opus-4-8"),
                Thinking(3, "sig-b"), Text(4, "Answer from the fallback."), [End("end_turn"), Stop()])));
        var log = new List<string>();
        var result = await chat.SendAsync("Question", null, Recorder(log), CancellationToken.None);

        Assert.Equal(TurnEnd.Completed, result.End);
        Assert.Contains("fallback:claude-opus-4-8", log);
        Assert.DoesNotContain(log, l => l.StartsWith("tool:", StringComparison.Ordinal));
        Assert.True(chat.History[1].TryPickAssistant(out var assistant));
        Assert.True(assistant!.Content.TryPickBetaContentBlockParams(out var blocks));
        Assert.Equal(3, blocks!.Count);
        Assert.True(blocks[0].TryPickFallback(out _));
        Assert.True(blocks[1].TryPickThinking(out var thinking));
        Assert.Equal("sig-b", thinking!.Signature);
        Assert.True(blocks[2].TryPickText(out _));
    }

    [Fact]
    public async Task Haiku_requests_leave_out_fallbacks()
    {
        var (chat, api) = Create("claude-haiku-5-5", Stream(Events([Start("claude-haiku-5-5")], Text(0, "Hi"), [End("end_turn"), Stop()])));
        await chat.SendAsync("Hello", null, Recorder([]), CancellationToken.None);
        var (_, betas, body) = api.Requests.Single();
        Assert.Null(body["fallbacks"]);
        Assert.DoesNotContain("server-side-fallback", betas);
        Assert.Contains("thinking-binding-controls-2026-08-01", betas);
    }

    [Fact]
    public async Task Api_errors_are_explained_and_rolled_back()
    {
        var (chat, _) = Create("claude-opus-5-5", () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}", Encoding.UTF8, "application/json"),
        });
        var result = await chat.SendAsync("Hello", null, Recorder([]), CancellationToken.None);
        Assert.Equal(TurnEnd.Failed, result.End);
        Assert.Contains("API key", result.Detail);
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task Invalid_tool_input_from_the_model_is_answered_without_running()
    {
        var (chat, api) = Create("claude-opus-5-5",
            Stream(Events([Start()], ToolUse(0, "toolu_1", "start_timer", "{\"seconds\": -5}"), [End("tool_use"), Stop()])),
            Stream(Events([Start()], Text(0, "Sorry, that was not valid."), [End("end_turn"), Stop()])));
        var ran = false;
        var events = new ClaudeTurnEvents
        {
            RunTool = (name, input) =>
            {
                // What the app does: validate first, run only valid input.
                if (!AssistantActions.TryValidate(name, input, [], out _, out var error, out var malformed))
                    return Task.FromResult(new ToolOutcome(malformed ? AssistantActions.InvalidInputContent(input, error) : error, true));
                ran = true;
                return Task.FromResult(new ToolOutcome("Done."));
            },
        };
        await chat.SendAsync("Timer", null, events, CancellationToken.None);
        Assert.False(ran);
        var result = api.Requests[1].Body["messages"]![2]!["content"]![0]!;
        Assert.True(result["is_error"]!.GetValue<bool>());
        Assert.Contains("INVALID_JSON", result["content"]!.GetValue<string>());
    }

    /// <summary>Serves the first part of a response, then waits until the test releases the rest.</summary>
    private sealed class GatedStream(byte[] first, byte[] rest, Task gate) : Stream
    {
        private int _stage;
        private int _offset;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_stage == 1 && _offset == first.Length) { await gate.WaitAsync(ct); _stage = 2; _offset = 0; }
            var source = _stage <= 1 ? first : rest;
            if (_stage == 0) _stage = 1;
            int n = Math.Min(buffer.Length, source.Length - _offset);
            source.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Text_reaches_the_ui_before_the_response_ends()
    {
        static string Sse(params object[] events) => string.Concat(events.Select(e =>
        {
            var json = JsonSerializer.Serialize(e);
            return $"event: {JsonNode.Parse(json)!["type"]!.GetValue<string>()}\ndata: {json}\n\n";
        }));
        var firstText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var head = Encoding.UTF8.GetBytes(Sse(Events([Start()], [BlockStart(0, new { type = "text", text = "" }), Delta(0, new { type = "text_delta", text = "Hel" })])));
        var tail = Encoding.UTF8.GetBytes(Sse(Delta(0, new { type = "text_delta", text = "lo" }), BlockStop(0), End("end_turn"), Stop()));
        var api = new FakeApi(() =>
        {
            var content = new StreamContent(new GatedStream(head, tail, firstText.Task));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var chat = new ClaudeChat("sk-ant-test", "claude-opus-5-5", "Test", null, new HttpClient(api));
        var deltas = new List<string>();
        var events = new ClaudeTurnEvents
        {
            TextDelta = t => { deltas.Add(t); firstText.TrySetResult(); },
            RunTool = (_, _) => Task.FromResult(new ToolOutcome("")),
        };
        // If the SDK buffered the whole response before yielding events, the gate would never open.
        var result = await chat.SendAsync("Hi", null, events, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TurnEnd.Completed, result.End);
        Assert.Equal(["Hel", "lo"], deltas);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Claude Opus 5.5")]
    [InlineData("claude-opus-4-8", "Claude Opus 4.8")]
    [InlineData("claude-haiku-5-5", "Claude Haiku 5.5")]
    [InlineData("custom-model", "custom-model")]
    public void Model_names_display(string id, string name) => Assert.Equal(name, ClaudeChat.DisplayName(id));
}
