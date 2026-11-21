using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VOS.Core;
using VOS.Core.Assistant;
using VOS.Core.Search;
using VOS.Core.Security;
using VOS.Core.Settings;
using VOS.Core.Weather;
using VOS.Platform.Shell;
using VOS.UI;
using VOS.UI.Theme;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace VOS.Apps.Assistant;

/// <summary>
/// V-OS Assistant with three providers: on this PC (deterministic commands, offline), Claude (the user's own Anthropic
/// key, V-OS actions as tools) and Ollama (a model server the user runs). Cloud use needs consent. Conversations live
/// in memory only and are gone when the window closes.
/// </summary>
public sealed class AssistantApp : IAppModule
{
    public AppFrame Frame { get; }
    private static VosSettings Cur => SettingsStore.Instance.Current;
    private static AssistantSettings A => Cur.Assistant;

    private readonly Grid _body = new();
    private readonly Grid _chatView = new();
    private readonly StackPanel _messages = new() { Spacing = 20 };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly FontIcon _sendIcon;
    private readonly TextBlock _footer = Ui.Text("", 11.5, "Vos.TextTertiary", wrap: TextWrapping.Wrap);
    private readonly TextBlock _providerLabel = Ui.Text("", 12.5, "Vos.TextSecondary");
    private readonly FontIcon _providerIcon = Ui.Icon(Glyphs.Sparkle, 13, "Vos.TextSecondary");
    private readonly List<ChatTurn> _plain = [];
    private CancellationTokenSource? _cts;
    private ClaudeChat? _claude;
    private string _session = "";
    private string _gate = "";
    private bool _busy, _hasMessages, _stickToBottom = true;

    public AssistantApp()
    {
        Frame = new AppFrame(VosApp.Find("assistant")!, "Assistant", 520, 460, 820, 720);

        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Ask anything, or tell V-OS what to do",
            FontSize = 14.5, MinHeight = 40, MaxHeight = 168, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 9, 6, 9),
        };
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _input.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Buttons.AutomationName(_input, "Message");
        _input.PreviewKeyDown += OnInputKey;

        _sendIcon = Ui.Icon(Glyphs.Send, 15, "Vos.OnAccent");
        _send = Buttons.Accent(_sendIcon, () => { if (_busy) _cts?.Cancel(); else Send(); }, 18, new Thickness(0));
        _send.Width = _send.Height = 36;
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        Buttons.AutomationName(_send, "Send");
        ToolTipService.SetToolTip(_send, "Send (Enter)");

        _scroll = new ScrollViewer { Content = Centered(_messages, new Thickness(24, 12, 24, 24)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _scroll.ViewChanged += (_, _) => _stickToBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 48;
        _messages.SizeChanged += (_, _) => { if (_stickToBottom) _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true); };
        BuildChatView();

        Frame.Body.Content = _body;
        _session = Signature();
        ShowWelcome();
        Render();
        SettingsStore.Instance.Changed += OnSettings;
        Frame.Closed += () =>
        {
            SettingsStore.Instance.Changed -= OnSettings;
            _cts?.Cancel();
            _claude?.Dispose();
        };
    }

    public void HandleArgs(AppArgs args)
    {
        if (args.Get("ask") is { Length: > 0 } ask && _gate == "chat") Send(ask);
        else if (args.Get("text") is { Length: > 0 } text) { _input.Text = text; _input.SelectionStart = text.Length; }
        FocusInput();
    }

    private void OnSettings(VosSettings s) => UiThread.Post(Render);

    // ───────────────────────────── layout ─────────────────────────────

    private void BuildChatView()
    {
        _chatView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chatView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _chatView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new Grid { Padding = new Thickness(16, 6, 16, 6) };
        var provider = Buttons.Subtle(Kit.Row(8, _providerIcon, _providerLabel), () => AppHost.Open("settings", ["section=apps"]));
        ToolTipService.SetToolTip(provider, "Assistant settings");
        Buttons.AutomationName(provider, "Assistant settings");
        toolbar.Children.Add(provider);
        var newChat = Buttons.Subtle(Kit.Row(6, Ui.Icon(Glyphs.Add, 12), Ui.Text("New chat", 13)), NewChat);
        newChat.HorizontalAlignment = HorizontalAlignment.Right;
        Buttons.AutomationName(newChat, "New chat");
        toolbar.Children.Add(newChat);
        _chatView.Children.Add(toolbar);

        Grid.SetRow(_scroll, 1);
        _chatView.Children.Add(_scroll);

        var composer = new Grid { ColumnSpacing = 8 };
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.Children.Add(_input);
        Grid.SetColumn(_send, 1);
        composer.Children.Add(_send);
        var box = Kit.Card(composer, new Thickness(10, 6, 6, 6));
        box.CornerRadius = new CornerRadius(22);
        _footer.HorizontalAlignment = HorizontalAlignment.Center;
        _footer.TextAlignment = TextAlignment.Center;
        _footer.Margin = new Thickness(0, 8, 0, 0);
        var bottom = Centered(Kit.Stack(0, box, _footer), new Thickness(24, 4, 24, 14));
        Grid.SetRow(bottom, 2);
        _chatView.Children.Add(bottom);
    }

    private static Grid Centered(UIElement child, Thickness padding)
    {
        var g = new Grid { Padding = padding };
        if (child is FrameworkElement fe) { fe.MaxWidth = 760; fe.HorizontalAlignment = HorizontalAlignment.Stretch; }
        g.Children.Add(child);
        return g;
    }

    private void Render()
    {
        var gate = Gate();
        UpdateChrome();
        if (gate == _gate) return;
        _gate = gate;
        _body.Children.Clear();
        _body.Children.Add(gate switch
        {
            "consent" => ConsentCard("Use Claude for the assistant?",
                "Messages you send are processed by Anthropic (api.anthropic.com) with your own API key, together with the results of actions the assistant runs for you, such as a weather lookup. Nothing is sent until you allow it, and you can turn it off in Settings › Privacy."),
            "consent-ollama" => ConsentCard("Send messages to this Ollama server?",
                $"Your Ollama server is not on this PC ({HostOf(A.OllamaEndpoint)}). Messages you send will go to that server. Nothing is sent until you allow it."),
            "key" => KeyCard(),
            _ => _chatView,
        });
        if (gate == "chat") FocusInput();
    }

    private static string Gate()
    {
        if (A.Provider == "claude")
        {
            if (!Cur.Privacy.CloudAssistantConsent) return "consent";
            if (string.IsNullOrWhiteSpace(Secrets.Unprotect(A.ClaudeApiKey))) return "key";
        }
        else if (A.Provider == "ollama" && !OllamaClient.IsLoopback(A.OllamaEndpoint) && !Cur.Privacy.CloudAssistantConsent) return "consent-ollama";
        return "chat";
    }

    private void UpdateChrome()
    {
        switch (A.Provider)
        {
            case "claude":
                _providerIcon.Glyph = Glyphs.Sparkle;
                _providerLabel.Text = ClaudeChat.DisplayName(A.ClaudeModel);
                _footer.Text = $"Sent to Anthropic with your API key · {(A.AllowSystemActions ? "can run V-OS actions" : "actions off")} · not saved after you close this window";
                break;
            case "ollama":
                bool local = OllamaClient.IsLoopback(A.OllamaEndpoint);
                _providerIcon.Glyph = Glyphs.Cpu;
                _providerLabel.Text = "Ollama · " + A.OllamaModel;
                _footer.Text = (local ? "Runs on this PC with Ollama" : "Sent to " + HostOf(A.OllamaEndpoint) + " (Ollama)") + " · answers only, no actions · not saved";
                break;
            default:
                _providerIcon.Glyph = Glyphs.Desktop;
                _providerLabel.Text = "On this PC";
                _footer.Text = "Commands run on this PC · weather from Open-Meteo · for open questions, choose Claude or Ollama in Settings";
                break;
        }
        if (!_hasMessages) ShowWelcome();
    }

    private void ShowWelcome()
    {
        _messages.Children.Clear();
        string[] suggestions = A.Provider switch
        {
            "claude" => ["What's the weather like today?", "Start a 25 minute focus timer", "Switch to dark theme", "Explain what workspaces do in V-OS"],
            "ollama" => ["Explain DNS in simple terms", "Write a polite reply declining a meeting", "Give me three dinner ideas", "Summarise the pros and cons of SSDs"],
            _ => ["timer 10 min", "weather", "dark mode", "open files"],
        };
        var rows = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < suggestions.Length; i++)
        {
            var s = suggestions[i];
            row.Children.Add(Kit.Chip(s, false, () => Send(s)));
            if (i % 2 == 1) { rows.Children.Add(row); row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center }; }
        }
        if (row.Children.Count > 0) rows.Children.Add(row);
        var subtitle = A.Provider switch
        {
            "claude" => A.AllowSystemActions ? "Ask a question, or ask me to open apps, start timers, change settings and more." : "Ask anything. Actions on this PC are turned off.",
            "ollama" => "Ask anything. Answers come from your Ollama model.",
            _ => "I run V-OS commands right here, offline. Try one of these:",
        };
        var welcome = Kit.Stack(14,
            Center(new AppIconView(VosApp.Find("assistant")!, 56)),
            Center(Ui.Text("How can I help?", 24, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold)),
            Center(Ui.Text(subtitle, 13.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap)),
            rows);
        welcome.Margin = new Thickness(0, 64, 0, 0);
        _messages.Children.Add(welcome);

        static FrameworkElement Center(FrameworkElement e) { e.HorizontalAlignment = HorizontalAlignment.Center; if (e is TextBlock t) t.TextAlignment = TextAlignment.Center; return e; }
    }

    private UIElement ConsentCard(string title, string body) => GateCard(title, body,
        Kit.Row(8,
            Buttons.Accent("Allow", () => SettingsStore.Instance.Update(s => s.Privacy.CloudAssistantConsent = true)),
            Buttons.Filled("Use on this PC instead", () => SettingsStore.Instance.Update(s => s.Assistant.Provider = "local"))));

    private UIElement KeyCard()
    {
        var box = new PasswordBox { PlaceholderText = "sk-ant-…", Width = 280 };
        Buttons.AutomationName(box, "Claude API key");
        var status = Ui.Text("", 12.5, "Vos.Critical", wrap: TextWrapping.Wrap);
        status.Visibility = Visibility.Collapsed;
        void Save()
        {
            var k = box.Password.Trim();
            if (k.Length < 20 || k.Any(char.IsWhiteSpace)) { status.Text = "That does not look like an API key."; status.Visibility = Visibility.Visible; return; }
            box.Password = "";
            SettingsStore.Instance.Update(s => s.Assistant.ClaudeApiKey = Secrets.Protect(k));
        }
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Save(); e.Handled = true; } };
        return GateCard("Add your Claude API key",
            "Claude uses your own API key from the Claude Console. It is stored encrypted for your Windows account and sent only to api.anthropic.com.",
            Kit.Stack(10,
                Kit.Row(8, box, Buttons.Accent("Save", Save)),
                status,
                Kit.Row(16,
                    Kit.Link("Get a key in the Claude Console", () => ShellOps.OpenUrl("https://console.anthropic.com/settings/keys")),
                    Kit.Link("Use on this PC instead", () => SettingsStore.Instance.Update(s => s.Assistant.Provider = "local")))));
    }

    private static Border GateCard(string title, string body, UIElement actions)
    {
        var card = Kit.Card(Kit.Stack(12,
            new AppIconView(VosApp.Find("assistant")!, 44) { HorizontalAlignment = HorizontalAlignment.Left },
            Ui.Text(title, 20, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold),
            Ui.Text(body, 13.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap),
            actions), new Thickness(28));
        card.MaxWidth = 500;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.Margin = new Thickness(24);
        return card;
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    // ───────────────────────────── conversation ─────────────────────────────

    /// <summary>Everything a conversation depends on. When it changes, the next message starts a new conversation:
    /// a Claude conversation's system prompt and tools must not change once it has begun.</summary>
    private static string Signature() => string.Join('|', A.Provider, A.ClaudeModel, A.ClaudeApiKey ?? "", A.AllowSystemActions,
        A.OllamaEndpoint, A.OllamaModel, string.Join(',', Cur.Workspaces.Select(w => w.Name)));

    private void NewChat()
    {
        _cts?.Cancel();
        ResetConversation();
        _hasMessages = false;
        ShowWelcome();
        FocusInput();
    }

    private void ResetConversation()
    {
        _claude?.Dispose();
        _claude = null;
        _plain.Clear();
        _session = Signature();
    }

    private void OnInputKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            if (!_busy) Send();
        }
        else if (e.Key == VirtualKey.Escape && _busy) { _cts?.Cancel(); e.Handled = true; }
    }

    private void FocusInput() => UiThread.Post(() => _input.Focus(FocusState.Programmatic));

    private async void Send(string? preset = null)
    {
        if (_busy || _gate != "chat") return;
        var text = (preset ?? _input.Text).Trim();
        if (text.Length == 0) return;
        _input.Text = "";

        if (!_hasMessages) { _messages.Children.Clear(); _hasMessages = true; }
        else if (Signature() != _session)
        {
            ResetConversation();
            _messages.Children.Add(Divider("Assistant settings changed · new conversation"));
        }
        _session = Signature();

        _messages.Children.Add(UserBubble(text));
        var reply = new Reply();
        _messages.Children.Add(reply.Root);
        _stickToBottom = true;
        SetBusy(true);
        var cts = _cts = new CancellationTokenSource();
        try
        {
            switch (A.Provider)
            {
                case "claude": await RunClaudeAsync(text, reply, cts.Token); break;
                case "ollama": await RunOllamaAsync(text, reply, cts.Token); break;
                default: await RunLocalAsync(text, reply, cts.Token); break;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { reply.Notice("Stopped.", NoticeKind.Info); }
        catch (Exception ex)
        {
            Log.Error("Assistant request failed", ex);
            reply.Notice("Something went wrong: " + ex.Message, NoticeKind.Error);
        }
        finally
        {
            reply.Finish();
            SetBusy(false);
            if (_cts == cts) _cts = null;
            cts.Dispose();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _sendIcon.Glyph = busy ? Glyphs.Stop : Glyphs.Send;
        Buttons.AutomationName(_send, busy ? "Stop" : "Send");
        ToolTipService.SetToolTip(_send, busy ? "Stop (Esc)" : "Send (Enter)");
    }

    private static string TimeContext()
    {
        var now = DateTimeOffset.Now;
        var offset = now.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return string.Create(CultureInfo.InvariantCulture, $"[Local time: {now:dddd d MMMM yyyy, HH:mm}, UTC{sign}{offset:hh\\:mm}]");
    }

    private static string SystemPrompt(bool tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the assistant built into V-OS, a desktop environment that runs on top of Windows on the user's own PC.");
        sb.AppendLine("Reply in the language the user writes in. Keep answers short and practical: a few sentences, or a short list when there are steps. Use Markdown sparingly (bold, lists, inline code, fenced code blocks); avoid tables and headings unless asked.");
        sb.AppendLine("V-OS apps: Files, Browser, Music, Weather, Notes, Time (clocks, alarms, timers, calendar), Monitor (performance and processes), Capture (screenshots), Store (installs apps with winget), Gaming, Translate, Connect (shares files over the local network), Flow (automations) and Settings. Windows apps and Windows Settings remain available alongside V-OS.");
        if (tools)
            sb.AppendLine("You can act on this PC with the tools provided. Use a tool when the user asks for something the tools cover; answer questions directly. After an action, confirm in one short sentence what happened, based on the tool result. V-OS asks the user to confirm sleep, restart, shut down and sign out, so request those only when the user clearly asks for them.");
        else
            sb.AppendLine("You cannot change anything on this PC in this conversation. When the user asks for an action, tell them briefly where to do it in V-OS; they can let the assistant act in Settings › Apps › Assistant.");
        sb.AppendLine("You cannot see the screen, files or other apps; work only with what the user writes and what tools return. A user message may begin with the current local time in brackets; use it for anything time-related and do not mention it otherwise.");
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────────── providers ─────────────────────────────

    private async Task RunClaudeAsync(string text, Reply reply, CancellationToken ct)
    {
        var chat = EnsureClaude();
        if (chat == null) { reply.Notice("Add your Claude API key in Settings › Apps › Assistant.", NoticeKind.Warning); return; }
        var events = new ClaudeTurnEvents
        {
            TextStarted = reply.StartText,
            TextDelta = reply.Append,
            ToolStarting = _ => reply.HideTyping(),
            FellBack = model => reply.Notice($"Answered by {ClaudeChat.DisplayName(model)}: {ClaudeChat.DisplayName(chat.Model)} declined this request.", NoticeKind.Info),
            Retrying = reply.DiscardLive,
            RunTool = (name, input) => RunToolAsync(reply, name, input, ct),
        };
        var result = await chat.SendAsync(text, TimeContext(), events, ct);
        if (result.End == TurnEnd.Refused) reply.DiscardLive();
        reply.FinishText();
        switch (result.End)
        {
            case TurnEnd.Refused:
                reply.Notice("Claude declined to answer this." + (result.Detail is { Length: > 0 } d ? " " + d : ""), NoticeKind.Warning);
                break;
            case TurnEnd.CutOff: reply.Notice("The answer was cut off at the length limit.", NoticeKind.Info); break;
            case TurnEnd.ContextFull: reply.Notice("This conversation is too long. Start a new chat to continue.", NoticeKind.Warning); break;
            case TurnEnd.Failed: reply.Notice(result.Detail ?? "The request failed.", NoticeKind.Error); break;
            case TurnEnd.Cancelled: reply.Notice("Stopped.", NoticeKind.Info); break;
        }
    }

    private ClaudeChat? EnsureClaude()
    {
        if (_claude != null) return _claude;
        var key = Secrets.Unprotect(A.ClaudeApiKey);
        if (string.IsNullOrWhiteSpace(key)) return null;
        bool tools = A.AllowSystemActions;
        _claude = new ClaudeChat(key, A.ClaudeModel, SystemPrompt(tools),
            tools ? ClaudeChat.ToTools(AssistantActions.Tools(Cur.Workspaces.Select(w => w.Name).ToList())) : null);
        return _claude;
    }

    private async Task<ToolOutcome> RunToolAsync(Reply reply, string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var workspaces = Cur.Workspaces.Select(w => (w.Id, w.Name)).ToList();
        if (!AssistantActions.TryValidate(name, input, workspaces, out var action, out var error, out var malformed))
        {
            Log.Info("Assistant tool input rejected", new { name, malformed });
            return new(malformed ? AssistantActions.InvalidInputContent(input, error) : error, true);
        }
        if (!A.AllowSystemActions && action.Kind == ActionKind.Shell)
            return new("Not done: actions are turned off in Settings › Apps › Assistant. Tell the user where to do this instead.", true);
        var row = reply.AddAction(action.Summary);
        var outcome = await ExecuteAsync(action, ct);
        row.Complete(!outcome.IsError, outcome.IsError ? outcome.Content : null);
        reply.ShowTyping();
        return outcome;
    }

    private static async Task<ToolOutcome> ExecuteAsync(AssistantAction a, CancellationToken ct)
    {
        if (a.Kind == ActionKind.Weather)
        {
            var (report, error) = await LoadWeatherAsync(a.City, ct);
            return report == null ? new(error!, true) : new(WeatherJson(report));
        }
        if (TryOpenInProcess(a.Command)) return new("Done.");
        var ok = await AppHost.RunInShellAsync(a.Command, a.NeedsConfirmation ? 120_000 : 10_000);
        return ok switch
        {
            true => new(a.NeedsConfirmation ? "Done: the user confirmed." : "Done."),
            false when a.NeedsConfirmation => new("Not done: the user cancelled the confirmation.", true),
            false when a.Command.StartsWith("openapp:", StringComparison.Ordinal) => new($"Not done: no installed app matches “{a.Command[8..]}”.", true),
            false => new("Not done: V-OS could not do this.", true),
            null => new("Not done: the V-OS shell is not running.", true),
        };
    }

    /// <summary>V-OS apps and Settings open directly in this process (fast, and works without the shell).</summary>
    private static bool TryOpenInProcess(string command)
    {
        if (command.StartsWith("app:", StringComparison.Ordinal))
        {
            var parts = command[4..].Split('|', 2);
            AppHost.Open(parts[0], parts.Length > 1 ? parts[1].Split('\u001F', StringSplitOptions.RemoveEmptyEntries) : []);
            return true;
        }
        if (command.StartsWith("settings:", StringComparison.Ordinal))
        {
            var section = command[9..];
            AppHost.Open("settings", section.Length > 0 ? ["section=" + section] : []);
            return true;
        }
        if (command.StartsWith("openapp:", StringComparison.Ordinal))
        {
            var name = command[8..].Trim();
            var app = VosApp.All.FirstOrDefault(x => x.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (app != null) { AppHost.Open(app.Id, []); return true; }
        }
        return false;
    }

    private async Task RunOllamaAsync(string text, Reply reply, CancellationToken ct)
    {
        var turn = new ChatTurn("user", text);
        _plain.Add(turn);
        if (_plain.Count > 40) _plain.RemoveRange(0, _plain.Count - 40);
        var messages = new List<ChatTurn> { new("system", SystemPrompt(tools: false) + "\n" + TimeContext()) };
        messages.AddRange(_plain);
        var answer = new StringBuilder();
        try
        {
            await foreach (var chunk in OllamaClient.ChatAsync(A.OllamaEndpoint, A.OllamaModel, messages, ct))
            {
                answer.Append(chunk);
                reply.Append(chunk);
            }
            reply.FinishText();
            if (answer.Length > 0) _plain.Add(new ChatTurn("assistant", answer.ToString()));
            else { _plain.Remove(turn); reply.Notice("The model returned no answer.", NoticeKind.Warning); }
        }
        catch (OllamaException ex)
        {
            _plain.Remove(turn);
            reply.DiscardLive();
            reply.Notice(ex.Message, NoticeKind.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _plain.Remove(turn);
            reply.FinishText();
            reply.Notice("Stopped.", NoticeKind.Info);
        }
    }

    private const string LocalHelp =
        "I run V-OS commands on this PC, for example:\n" +
        "- **open files**, **open spotify**\n" +
        "- **timer 10 min**, **dark mode**, **volume 30**\n" +
        "- **weather in London**, **12 * 7**, **5 km in miles**\n" +
        "- **workspace work**, **focus on**, **lock**\n\n" +
        "For open questions, choose **Claude** or **Ollama** in Settings › Apps › Assistant.";

    private static async Task RunLocalAsync(string text, Reply reply, CancellationToken ct)
    {
        var intent = CommandParser.Parse(text);
        switch (intent.Kind)
        {
            case IntentKind.None:
                reply.SetMarkdown(LocalHelp);
                return;
            case IntentKind.Calculate or IntentKind.Convert:
                reply.SetMarkdown($"**{intent.Title}**" + (intent.Detail.Length > 0 ? "\n" + intent.Detail : ""));
                return;
            case IntentKind.Weather:
                var city = intent.Arg("city");
                var (report, error) = await LoadWeatherAsync(city.Length > 0 ? city : null, ct);
                if (report == null) reply.Notice(error!, NoticeKind.Warning);
                else reply.SetMarkdown(WeatherText(report));
                return;
        }
        if (!A.AllowSystemActions)
        {
            reply.SetMarkdown("Actions are turned off. You can allow them in **Settings › Apps › Assistant**.");
            return;
        }
        var row = reply.AddAction(intent.Title);
        bool? ok = intent.Kind == IntentKind.OpenApp && TryOpenInProcess("openapp:" + intent.Arg("name"))
            ? true
            : await AppHost.RunInShellAsync("intent:" + text, intent.RequiresConfirmation ? 120_000 : 10_000);
        row.Complete(ok == true, ok switch
        {
            true => null,
            false when intent.RequiresConfirmation => "Cancelled",
            false when intent.Kind == IntentKind.OpenApp => "No app with that name was found",
            false => "V-OS could not do this",
            null => "The V-OS shell is not running",
        });
    }

    // ───────────────────────────── weather ─────────────────────────────

    private static async Task<(WeatherReport? Report, string? Error)> LoadWeatherAsync(string? city, CancellationToken ct)
    {
        WeatherLocation? location;
        if (city != null)
        {
            List<WeatherLocation> found;
            try { found = await WeatherService.Shared.SearchAsync(city, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return (null, "The weather service could not be reached.");
            }
            location = found.FirstOrDefault();
            if (location == null) return (null, $"No place called “{city}” was found.");
        }
        else
        {
            location = Cur.Weather.Selected;
            if (location == null) return (null, "No location is set in the Weather app. Ask which city the user means.");
        }
        var report = await WeatherService.Shared.GetAsync(location, Cur.Weather, TimeSpan.FromMinutes(15), ct: ct);
        return report == null ? (null, "The forecast could not be loaded. The PC may be offline.") : (report, null);
    }

    private static string WeatherJson(WeatherReport r)
    {
        var w = Cur.Weather;
        return JsonSerializer.Serialize(new
        {
            place = string.IsNullOrEmpty(r.Location.Country) ? r.Location.Display : r.Location.Display + ", " + r.Location.Country,
            source = "Open-Meteo",
            offline_copy = r.Stale,
            now = new
            {
                temperature = WeatherFormat.Temperature(r.Now.TemperatureC, w.TemperatureUnit, unit: true),
                feels_like = WeatherFormat.Temperature(r.Now.ApparentC, w.TemperatureUnit, unit: true),
                condition = WeatherCodes.Describe(r.Now.Condition, r.Now.IsDay),
                humidity_percent = r.Now.Humidity,
                wind = WeatherFormat.Wind(r.Now.WindKmh, w.WindUnit),
            },
            days = r.Daily.Take(3).Select(d => new
            {
                date = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                high = WeatherFormat.Temperature(d.MaxC, w.TemperatureUnit, unit: true),
                low = WeatherFormat.Temperature(d.MinC, w.TemperatureUnit, unit: true),
                condition = WeatherCodes.Describe(WeatherCodes.Map(d.Code)),
                precipitation_chance_percent = d.PrecipProbability,
            }),
        });
    }

    private static string WeatherText(WeatherReport r)
    {
        var w = Cur.Weather;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"**{r.Location.Display}** · {WeatherFormat.Temperature(r.Now.TemperatureC, w.TemperatureUnit, unit: true)}, {WeatherCodes.Describe(r.Now.Condition, r.Now.IsDay)}");
        sb.Append(CultureInfo.InvariantCulture, $" · feels like {WeatherFormat.Temperature(r.Now.ApparentC, w.TemperatureUnit, unit: true)}\n");
        int i = 0;
        foreach (var d in r.Daily.Take(3))
        {
            // Day names follow the UI language (English until localization lands), not the regional format.
            var day = i++ switch { 0 => "Today", 1 => "Tomorrow", _ => d.Date.ToString("dddd", CultureInfo.InvariantCulture) };
            sb.Append(CultureInfo.InvariantCulture, $"\n- **{day}**: {WeatherFormat.Temperature(d.MinC, w.TemperatureUnit)} – {WeatherFormat.Temperature(d.MaxC, w.TemperatureUnit, unit: true)}, {WeatherCodes.Describe(WeatherCodes.Map(d.Code)).ToLower(CultureInfo.CurrentCulture)}");
            if (d.PrecipProbability >= 20) sb.Append(CultureInfo.InvariantCulture, $", {d.PrecipProbability}% chance of rain");
        }
        if (r.Stale) sb.Append("\n\n_Offline: this is the last saved forecast._");
        return sb.ToString();
    }

    // ───────────────────────────── transcript elements ─────────────────────────────

    private static Border UserBubble(string text) => new()
    {
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14.5, FontFamily = Tokens.Text, Foreground = Ui.B("Vos.TextPrimary") },
        Background = Ui.B("Vos.AccentSubtle"),
        CornerRadius = new CornerRadius(18, 18, 6, 18),
        Padding = new Thickness(14, 10, 14, 10),
        HorizontalAlignment = HorizontalAlignment.Right,
        MaxWidth = 560,
        Margin = new Thickness(64, 0, 0, 0),
    };

    private static UIElement Divider(string text)
    {
        var g = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border { Height = 1, Background = Ui.B("Vos.StrokeSubtle"), VerticalAlignment = VerticalAlignment.Center };
        var label = Ui.Text(text, 12, "Vos.TextTertiary");
        var right = new Border { Height = 1, Background = Ui.B("Vos.StrokeSubtle"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); Grid.SetColumn(right, 2);
        g.Children.Add(left); g.Children.Add(label); g.Children.Add(right);
        return g;
    }

    private enum NoticeKind { Info, Warning, Error }

    /// <summary>One assistant reply: streamed text segments, action rows and notices, in arrival order.</summary>
    private sealed class Reply
    {
        public readonly StackPanel Root = new() { Spacing = 10 };
        private readonly StackPanel _content = new() { Spacing = 10 };
        private readonly StackPanel _typing;
        private readonly Button _copy;
        private readonly StringBuilder _buffer = new();
        private readonly List<string> _texts = [];
        private TextBlock? _live;

        public Reply()
        {
            var header = Kit.Row(8, new AppIconView(VosApp.Find("assistant")!, 20), Ui.Text("Assistant", 12.5, "Vos.TextSecondary", weight: FontWeights.SemiBold));
            _typing = Kit.Row(8, new ProgressRing { Width = 14, Height = 14, IsActive = true }, Ui.Text("Thinking…", 13, "Vos.TextTertiary"));
            _copy = Buttons.Icon(Glyphs.Copy, Copy, 28, 12, "Copy answer");
            _copy.Visibility = Visibility.Collapsed;
            Root.Children.Add(header);
            Root.Children.Add(_content);
            Root.Children.Add(_copy);
            _content.Children.Add(_typing);
        }

        public void ShowTyping()
        {
            _content.Children.Remove(_typing);
            _content.Children.Add(_typing);
            _typing.Visibility = Visibility.Visible;
        }

        public void HideTyping() => _typing.Visibility = Visibility.Collapsed;

        public void StartText()
        {
            FinishText();
            HideTyping();
            _live = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14.5, FontFamily = Tokens.Text,
                LineHeight = Math.Round(14.5 * 1.45), Foreground = Ui.B("Vos.TextPrimary"),
            };
            _content.Children.Add(_live);
        }

        public void Append(string delta)
        {
            if (_live == null) StartText();
            _buffer.Append(delta);
            _live!.Text = _buffer.ToString();
        }

        /// <summary>Replaces the live plain-text segment with formatted Markdown.</summary>
        public void FinishText()
        {
            if (_live == null) return;
            var markdown = _buffer.ToString().Trim();
            int index = _content.Children.IndexOf(_live);
            if (markdown.Length > 0) { _content.Children[index] = MarkdownView.Render(markdown); _texts.Add(markdown); }
            else _content.Children.RemoveAt(index);
            _buffer.Clear();
            _live = null;
        }

        public void DiscardLive()
        {
            if (_live != null) _content.Children.Remove(_live);
            _live = null;
            _buffer.Clear();
        }

        public void SetMarkdown(string markdown)
        {
            FinishText();
            HideTyping();
            _content.Children.Add(MarkdownView.Render(markdown));
            _texts.Add(markdown);
        }

        public ActionRow AddAction(string summary)
        {
            FinishText();
            HideTyping();
            var row = new ActionRow(summary);
            _content.Children.Add(row.Root);
            return row;
        }

        public void Notice(string text, NoticeKind kind)
        {
            FinishText();
            HideTyping();
            var (glyph, brush) = kind switch
            {
                NoticeKind.Error => (Glyphs.Error, "Vos.Critical"),
                NoticeKind.Warning => (Glyphs.Warning, "Vos.Warning"),
                _ => (Glyphs.Info, "Vos.TextTertiary"),
            };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = Ui.Icon(glyph, 13, brush);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 0, 0);
            var label = Ui.Text(text, 13, kind == NoticeKind.Info ? "Vos.TextTertiary" : "Vos.TextSecondary", wrap: TextWrapping.Wrap);
            label.IsTextSelectionEnabled = true;
            Grid.SetColumn(label, 1);
            row.Children.Add(icon);
            row.Children.Add(label);
            _content.Children.Add(row);
        }

        public void Finish()
        {
            FinishText();
            HideTyping();
            if (_content.Children.Count == 1 && _content.Children[0] == _typing) Notice("No answer.", NoticeKind.Info);
            _copy.Visibility = _texts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Copy()
        {
            var package = new DataPackage();
            package.SetText(string.Join("\n\n", _texts.Select(t => Markdown.ToPlainText(Markdown.Parse(t)))));
            Clipboard.SetContent(package);
        }
    }

    /// <summary>A transcript row for an action: spinner while it runs, then a check or the reason it did not happen.</summary>
    private sealed class ActionRow
    {
        public readonly Grid Root = new()
        {
            ColumnSpacing = 10, Padding = new Thickness(12, 9, 14, 9), CornerRadius = new CornerRadius(12),
            Background = Ui.B("Vos.Card"), BorderBrush = Ui.B("Vos.StrokeSubtle"), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        private readonly Grid _status = new() { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        private readonly TextBlock _detail = Ui.Text("", 12, "Vos.TextTertiary", wrap: TextWrapping.Wrap);

        public ActionRow(string summary)
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _status.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
            Root.Children.Add(_status);
            _detail.Visibility = Visibility.Collapsed;
            var text = Kit.Stack(2, Ui.Text(summary, 13.5, "Vos.TextPrimary", wrap: TextWrapping.Wrap), _detail);
            Grid.SetColumn(text, 1);
            Root.Children.Add(text);
            Buttons.AutomationName(Root, summary);
        }

        public void Complete(bool ok, string? detail)
        {
            _status.Children.Clear();
            _status.Children.Add(Ui.Icon(ok ? Glyphs.Check : Glyphs.Error, 14, ok ? "Vos.Success" : "Vos.Critical"));
            if (!string.IsNullOrEmpty(detail)) { _detail.Text = detail; _detail.Visibility = Visibility.Visible; }
        }
    }
}
