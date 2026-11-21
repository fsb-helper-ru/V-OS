using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VOS.Core;
using VOS.Core.Flows;
using VOS.Core.Ipc;
using VOS.Core.Settings;
using VOS.Core.Timers;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Apps.Flow;

/// <summary>V-OS Flow: everyday automations — a trigger (time, app, Focus, power, workspace, start-up) and a few steps. The shell runs them.</summary>
public sealed class FlowApp : IAppModule
{
    public AppFrame Frame { get; }
    private readonly SharedJsonList<VOS.Core.Flows.Flow> _flows = new("flows.json");
    private readonly Grid _body = new();

    public FlowApp()
    {
        Frame = new AppFrame(VosApp.Find("flow")!, "Flow", 760, 520, 1000, 720);
        Frame.Body.Content = _body;
        _flows.Watch();
        _flows.Changed += () => UiThread.Post(Render);
        Frame.Closed += () => _flows.Dispose();
        Render();
    }

    public void HandleArgs(AppArgs args) { }

    private void Render()
    {
        _body.Children.Clear();
        var page = Kit.Stack(6, Kit.PageHeader("Flow", "Automate everyday routines. Flows run in the V-OS shell, even when this window is closed."));
        if (!IpcClient.IsListening(IpcNames.Shell)) page.Children.Add(Kit.Info("V-OS shell is not running", "Flows run only while the V-OS shell is running.", InfoBarSeverity.Warning));
        var add = Buttons.Accent(Kit.Row(6, Ui.Icon(Glyphs.Add, 13, "Vos.OnAccent"), Ui.Text("New flow", 13.5, "Vos.OnAccent")), () => _ = EditAsync(null));
        Buttons.AutomationName(add, "New flow");
        page.Children.Add(add);
        var list = _flows.List();
        page.Children.Add(Kit.Section(list.Count == 0 ? "Start from a template" : $"Your flows ({list.Count})"));
        foreach (var f in list) page.Children.Add(FlowCard(f));
        if (list.Count > 0) page.Children.Add(Kit.Section("Templates"));
        foreach (var t in VOS.Core.Flows.Flow.Templates().Where(t => list.All(f => f.Name != t.Name)))
        {
            var tmpl = t;
            page.Children.Add(Kit.ListRow(Kit.GlyphBadge(Glyphs.Flow, 36), tmpl.Name, $"{tmpl.Trigger.Describe()} → {string.Join(", ", tmpl.Actions.Select(a => a.Describe()))}", Ui.Text("Add", 13, "Vos.AccentText"),
                () => _flows.Mutate(l => l.Add(tmpl)), 56));
        }
        _body.Children.Add(Kit.Scroll(page, 860));
    }

    private Border FlowCard(VOS.Core.Flows.Flow f)
    {
        var g = new Grid { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Kit.GlyphBadge(Glyphs.Flow, 38, f.Enabled ? "Vos.AccentSubtle" : "Vos.Control", f.Enabled ? "Vos.AccentText" : "Vos.TextTertiary"));
        var text = Kit.Stack(3,
            Ui.Text(f.Name, 14.5, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold),
            Ui.Text(f.Trigger.Describe(), 12.5, "Vos.AccentText"),
            Ui.Text(string.Join(" → ", f.Actions.Select(a => a.Describe())), 12.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap),
            Ui.Text(f.LastRun is { } lr ? "Last ran " + lr.ToString("g", CultureInfo.CurrentCulture) : "Not run yet", 11.5, "Vos.TextTertiary"));
        Grid.SetColumn(text, 1); g.Children.Add(text);
        var id = f.Id;
        var actions = Kit.Row(4,
            Kit.Toggle(f.Enabled, on => _flows.Mutate(l => { if (l.FirstOrDefault(x => x.Id == id) is { } x) x.Enabled = on; })),
            Buttons.Icon(Glyphs.Play, () => AppHost.ShellCommand("flow:run:" + id), 32, 13, "Run now"),
            MoreButton(f));
        actions.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(actions, 2); g.Children.Add(actions);
        var card = Kit.Card(g, new Thickness(16, 14, 12, 14));
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private Button MoreButton(VOS.Core.Flows.Flow f)
    {
        var id = f.Id;
        var more = Buttons.Icon(Glyphs.More, null, 32, 13, "More");
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit", Icon = new FontIcon { Glyph = Glyphs.Rename } };
        edit.Click += (_, _) => _ = EditAsync(f);
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = Glyphs.Delete } };
        delete.Click += async (_, _) => { if (await Dialogs.ConfirmAsync(Frame.Root.XamlRoot, $"Delete “{f.Name}”?", "This flow stops running.", "Delete", destructive: true)) _flows.Mutate(l => l.RemoveAll(x => x.Id == id)); };
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        more.Flyout = menu;
        return more;
    }

    private async Task EditAsync(VOS.Core.Flows.Flow? existing)
    {
        var flow = existing != null ? VosJson.Deserialize<VOS.Core.Flows.Flow>(VosJson.Serialize(existing))! : new VOS.Core.Flows.Flow();
        var name = new TextBox { Header = "Name", Text = flow.Name };
        var triggerKind = Kit.Choice<string>([("time", "At a time"), ("startup", "When V-OS starts"), ("app", "When an app opens"), ("focus", "When Focus changes"), ("power", "When power changes"), ("workspace", "When a workspace opens")], flow.Trigger.Kind, k => flow.Trigger.Kind = k, 260);
        var triggerParams = new StackPanel { Spacing = 8 };
        void BuildParams()
        {
            triggerParams.Children.Clear();
            switch (flow.Trigger.Kind)
            {
                case "time":
                    var tp = new TimePicker { ClockIdentifier = SettingsStore.Instance.Current.Shell.Use24Hour ? "24HourClock" : "12HourClock", Time = TimeSpan.TryParse(flow.Trigger.Time, CultureInfo.InvariantCulture, out var ts) ? ts : new TimeSpan(9, 0, 0), MinuteIncrement = 5 };
                    tp.TimeChanged += (_, e) => flow.Trigger.Time = $"{e.NewTime.Hours:00}:{e.NewTime.Minutes:00}";
                    var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
                    int first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
                    for (int i = 0; i < 7; i++)
                    {
                        int d = (first + i) % 7;
                        var t = new ToggleButton { Content = names[d], MinWidth = 40, IsChecked = flow.Trigger.Days == 0 || (flow.Trigger.Days & (1 << d)) != 0 };
                        t.Checked += (_, _) => flow.Trigger.Days = (flow.Trigger.Days == 0 ? 0 : flow.Trigger.Days) | (1 << d);
                        t.Unchecked += (_, _) => { if (flow.Trigger.Days == 0) flow.Trigger.Days = 0b1111111; flow.Trigger.Days &= ~(1 << d); };
                        days.Children.Add(t);
                    }
                    triggerParams.Children.Add(tp);
                    triggerParams.Children.Add(days);
                    break;
                case "app":
                    var app = new TextBox { PlaceholderText = "Program name, for example steam.exe", Text = flow.Trigger.App };
                    app.TextChanged += (_, _) => flow.Trigger.App = app.Text.Trim();
                    triggerParams.Children.Add(app);
                    break;
                case "focus":
                    triggerParams.Children.Add(Kit.Choice<string>([("on", "Turns on"), ("off", "Turns off")], flow.Trigger.Value == "off" ? "off" : "on", v => flow.Trigger.Value = v));
                    break;
                case "power":
                    triggerParams.Children.Add(Kit.Choice<string>([("battery", "Switches to battery"), ("plugged", "Is plugged in")], flow.Trigger.Value == "plugged" ? "plugged" : "battery", v => flow.Trigger.Value = v));
                    break;
                case "workspace":
                    var ws = SettingsStore.Instance.Current.Workspaces;
                    if (ws.All(w => w.Id != flow.Trigger.Value)) flow.Trigger.Value = ws[0].Id;
                    triggerParams.Children.Add(Kit.Choice<string>(ws.Select(w => (w.Id, w.Name)), flow.Trigger.Value, v => flow.Trigger.Value = v));
                    break;
            }
        }
        triggerKind.SelectionChanged += (_, _) => BuildParams();
        BuildParams();

        var steps = new StackPanel { Spacing = 6 };
        void BuildSteps()
        {
            steps.Children.Clear();
            for (int i = 0; i < flow.Actions.Count; i++)
            {
                var a = flow.Actions[i];
                int index = i;
                var kind = Kit.Choice<string>(FlowAction.Kinds.Select(k => (k.kind, k.label)), a.Kind, k => { a.Kind = k; BuildSteps(); }, 210);
                var value = new TextBox { Text = a.Value, PlaceholderText = FlowAction.Kinds.FirstOrDefault(k => k.kind == a.Kind).hint, Width = 200 };
                value.TextChanged += (_, _) => a.Value = value.Text;
                var remove = Buttons.Icon(Glyphs.Close, () => { flow.Actions.RemoveAt(index); BuildSteps(); }, 30, 11, "Remove step");
                steps.Children.Add(Kit.Row(6, kind, value, remove));
            }
            steps.Children.Add(Buttons.Filled(Kit.Row(6, Ui.Icon(Glyphs.Add, 12), Ui.Text("Add a step", 13)), () => { flow.Actions.Add(new FlowAction()); BuildSteps(); }));
        }
        BuildSteps();

        var dlg = new ContentDialog
        {
            XamlRoot = Frame.Root.XamlRoot, Title = existing == null ? "New flow" : "Edit flow", PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, RequestedTheme = ThemeService.Current.ElementTheme,
            Content = new ScrollViewer { MaxHeight = 520, Content = Kit.Stack(12, name, Ui.Text("When", 13, "Vos.TextSecondary", Tokens.Text, FontWeights.SemiBold), triggerKind, triggerParams, Ui.Text("Do", 13, "Vos.TextSecondary", Tokens.Text, FontWeights.SemiBold), steps) },
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        flow.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "Untitled flow";
        flow.Actions.RemoveAll(a => a.ToCommand().Length == 0 || (a.Value.Trim().Length == 0 && a.Kind is not ("theme" or "focus")));
        _flows.Mutate(l =>
        {
            int i = l.FindIndex(x => x.Id == flow.Id);
            if (i >= 0) l[i] = flow; else l.Add(flow);
        });
    }
}
