using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VOS.Core;
using VOS.Core.Settings;
using VOS.Core.Translate;
using VOS.UI;
using VOS.UI.Theme;
using Windows.ApplicationModel.DataTransfer;

namespace VOS.Apps.Translate;

/// <summary>
/// V-OS Translate: text and clipboard translation through the provider you choose. Nothing is sent until you allow
/// online translation; the provider in use is always shown.
/// </summary>
public sealed class TranslateApp : IAppModule
{
    public AppFrame Frame { get; }
    private static VosSettings Cur => SettingsStore.Instance.Current;
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Type or paste text", FontSize = 16, MinHeight = 220, BorderThickness = new Thickness(0) };
    private readonly TextBlock _output = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, IsTextSelectionEnabled = true, Foreground = Ui.B("Vos.TextPrimary") };
    private readonly TextBlock _status = Ui.Text("", 12, "Vos.TextTertiary");
    private readonly ComboBox _from, _to;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _debounce;
    private readonly Grid _body = new();
    private CancellationTokenSource? _cts;
    private static readonly (string code, string name)[] s_languages = Settings.SettingsApp.Languages;

    public TranslateApp()
    {
        Frame = new AppFrame(VosApp.Find("translate")!, "Translate", 760, 480, 1040, 640);
        _from = Kit.Choice<string>([("auto", "Detect language"), .. s_languages], "auto", _ => Schedule(0), 200);
        _to = Kit.Choice<string>(s_languages, Cur.Translate.TargetLanguage, v => { SettingsStore.Instance.Update(s => s.Translate.TargetLanguage = v); Schedule(0); }, 200);
        Buttons.AutomationName(_from, "Translate from");
        Buttons.AutomationName(_to, "Translate into");
        Buttons.AutomationName(_input, "Text to translate");
        _debounce = UiThread.Timer(TimeSpan.FromMilliseconds(700), () => _ = TranslateAsync(), repeating: false);
        _input.TextChanged += (_, _) => Schedule(700);
        Frame.Body.Content = _body;
        Render();
        SettingsStore.Instance.Changed += OnSettings;
        Frame.Closed += () => SettingsStore.Instance.Changed -= OnSettings;
    }

    private void OnSettings(VosSettings s) => UiThread.Post(Render);

    public void HandleArgs(AppArgs args)
    {
        if (args.Get("text") is { Length: > 0 } text) { _input.Text = text; Schedule(0); }
        else if (args.Get("clipboard") == "1") _ = PasteClipboardAsync();
    }

    private void Render()
    {
        _body.Children.Clear();
        if (!Cur.Privacy.OnlineTranslationConsent)
        {
            var provider = ProviderName();
            var card = Kit.Card(Kit.Stack(12,
                Kit.GlyphBadge(Glyphs.Translate, 44),
                Ui.Text("Allow online translation?", 20, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold),
                Ui.Text($"Text you translate is sent to {provider}. Nothing is sent until you allow it, and you can turn it off any time in Settings › Privacy.", 13.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap),
                Kit.Row(8, Buttons.Accent("Allow", () => SettingsStore.Instance.Update(s => s.Privacy.OnlineTranslationConsent = true)), Buttons.Filled("Choose provider", () => AppHost.Open("settings", ["section=apps"])))), new Thickness(28));
            card.MaxWidth = 480;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            _body.Children.Add(card);
            return;
        }
        var root = new Grid { Padding = new Thickness(28, 16, 28, 20), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = new Grid { ColumnSpacing = 10 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.Children.Add(Kit.Row(8, _from, Buttons.Icon(Glyphs.Paste, () => _ = PasteClipboardAsync(), 34, 14, "Paste from clipboard")));
        var swap = Buttons.Icon(Glyphs.Sort, Swap, 36, 14, "Swap languages");
        Grid.SetColumn(swap, 1); bar.Children.Add(swap);
        var right = Kit.Row(8, _to, Buttons.Icon(Glyphs.Copy, () => { var dp = new DataPackage(); dp.SetText(_output.Text); Clipboard.SetContent(dp); _status.Text = "Copied"; }, 34, 14, "Copy translation"));
        Grid.SetColumn(right, 2); bar.Children.Add(right);
        root.Children.Add(bar);

        var panes = new Grid { ColumnSpacing = 14 };
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        var inputCard = Kit.Card(_input, new Thickness(10));
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _input.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        panes.Children.Add(inputCard);
        var outputCard = Kit.Card(new ScrollViewer { Content = _output, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 6, 6, 6) }, new Thickness(10));
        outputCard.Background = Ui.B("Vos.Control");
        Grid.SetColumn(outputCard, 1); panes.Children.Add(outputCard);
        Grid.SetRow(panes, 1); root.Children.Add(panes);
        var foot = new Grid();
        foot.Children.Add(_status);
        var provider2 = Ui.Text($"Provider: {ProviderName()}", 12, "Vos.TextTertiary");
        provider2.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Children.Add(provider2);
        Grid.SetRow(foot, 2); root.Children.Add(foot);
        _body.Children.Add(root);
    }

    private static string ProviderName() => Cur.Translate.Provider switch { "deepl" => "DeepL", "libretranslate" => "LibreTranslate (" + Cur.Translate.LibreTranslateUrl + ")", _ => "MyMemory (translated.net)" };

    private void Schedule(int ms)
    {
        _debounce.Stop();
        if (ms == 0) { _ = TranslateAsync(); return; }
        _debounce.Interval = TimeSpan.FromMilliseconds(ms);
        _debounce.Start();
    }

    private async Task TranslateAsync()
    {
        var text = _input.Text;
        if (string.IsNullOrWhiteSpace(text)) { _output.Text = ""; _status.Text = ""; return; }
        if (!Cur.Privacy.OnlineTranslationConsent) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var from = _from.SelectedIndex <= 0 ? "auto" : s_languages[_from.SelectedIndex - 1].code;
        var to = s_languages[Math.Max(0, _to.SelectedIndex)].code;
        _status.Text = "Translating…";
        try
        {
            var r = await Translator.TranslateAsync(text, from, to, Cur.Translate, cts.Token);
            if (cts.IsCancellationRequested) return;
            _output.Text = r.Text;
            var srcName = s_languages.FirstOrDefault(l => l.code == r.SourceLanguage).name ?? r.SourceLanguage;
            _status.Text = from == "auto" ? $"Detected: {srcName}" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) _status.Text = "Translation failed: " + ex.Message; Log.Warn("Translation failed", ex); }
    }

    private void Swap()
    {
        if (_from.SelectedIndex <= 0) return;
        int fromIndex = _from.SelectedIndex - 1, toIndex = _to.SelectedIndex;
        _from.SelectedIndex = toIndex + 1;
        _to.SelectedIndex = fromIndex;
        if (_output.Text.Length > 0) _input.Text = _output.Text;
    }

    private async Task PasteClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text)) { _input.Text = await content.GetTextAsync(); Schedule(0); }
        }
        catch (Exception ex) { Log.Debug("Clipboard unavailable", ex.Message); }
    }
}
