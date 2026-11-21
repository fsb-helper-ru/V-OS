using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Data;
using VOS.UI;
using VOS.UI.Theme;
using Windows.System;

namespace VOS.Apps.Notes;

public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Rtf { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Pinned { get; set; }
    public bool Trashed { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    public string DisplayTitle => Title.Trim().Length > 0 ? Title.Trim() : Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() is { Length: > 0 } l ? l : "New note";
    public string Snippet => string.Join(' ', Text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())).Trim() is var s && s.Length > 140 ? s[..140] + "…" : string.Join(' ', Text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())).Trim();
}

/// <summary>Notes storage: SQLite (WAL) in the V-OS data folder. Deleting moves to the trash; the trash empties after 30 days.</summary>
public sealed class NoteStore
{
    private readonly Db _db = Db.ForModule("notes");

    public NoteStore()
    {
        _db.Migrate(
            "CREATE TABLE notes(id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '', rtf TEXT NOT NULL DEFAULT '', text TEXT NOT NULL DEFAULT '', pinned INTEGER NOT NULL DEFAULT 0, trashed INTEGER NOT NULL DEFAULT 0, created TEXT NOT NULL, updated TEXT NOT NULL);" +
            "CREATE INDEX notes_updated ON notes(updated);");
        _db.Execute("DELETE FROM notes WHERE trashed=1 AND updated < $cut", ("$cut", DateTimeOffset.Now.AddDays(-30).ToString("o")));
    }

    public List<Note> List(string filter, string query)
    {
        var sql = "SELECT * FROM notes WHERE " + (filter == "trash" ? "trashed=1" : filter == "pinned" ? "trashed=0 AND pinned=1" : "trashed=0");
        if (query.Length > 0) sql += " AND (title LIKE $q OR text LIKE $q)";
        sql += " ORDER BY pinned DESC, updated DESC";
        return _db.Query(sql, Map, ("$q", "%" + query.Replace("%", "[%]") + "%"));
    }

    public int Count(bool trashed) => (int)_db.Scalar<long>("SELECT COUNT(*) FROM notes WHERE trashed=$t", ("$t", trashed ? 1 : 0));

    public void Save(Note n)
    {
        n.Updated = DateTimeOffset.Now;
        _db.Execute("INSERT INTO notes(id,title,rtf,text,pinned,trashed,created,updated) VALUES($id,$ti,$r,$tx,$p,$tr,$c,$u) " +
                    "ON CONFLICT(id) DO UPDATE SET title=$ti, rtf=$r, text=$tx, pinned=$p, trashed=$tr, updated=$u",
            ("$id", n.Id), ("$ti", n.Title), ("$r", n.Rtf), ("$tx", n.Text), ("$p", n.Pinned ? 1 : 0), ("$tr", n.Trashed ? 1 : 0), ("$c", n.Created.ToString("o")), ("$u", n.Updated.ToString("o")));
    }

    public void DeleteForever(string id) => _db.Execute("DELETE FROM notes WHERE id=$id", ("$id", id));
    public void EmptyTrash() => _db.Execute("DELETE FROM notes WHERE trashed=1");

    private static Note Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = Db.Str(r, "id")!, Title = Db.Str(r, "title") ?? "", Rtf = Db.Str(r, "rtf") ?? "", Text = Db.Str(r, "text") ?? "",
        Pinned = Db.Bool(r, "pinned"), Trashed = Db.Bool(r, "trashed"),
        Created = DateTimeOffset.Parse(Db.Str(r, "created")!, CultureInfo.InvariantCulture), Updated = DateTimeOffset.Parse(Db.Str(r, "updated")!, CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// V-OS Notes: rich-text notes with pinning, search, trash and export. Everything is saved as you type (half a
/// second after the last change), so there is no Save button and nothing to lose.
/// </summary>
public sealed class NotesApp : IAppModule
{
    public AppFrame Frame { get; }
    private readonly NoteStore _store = new();
    private readonly VirtualList<Note> _list;
    private readonly TextBox _search, _title;
    private readonly RichEditBox _editor;
    private readonly Grid _editorPane = new();
    private readonly StackPanel _toolbar;
    private readonly Button _pin, _delete;
    private readonly TextBlock _meta, _count;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private string _filter = "all";
    private Note? _current;
    private bool _loading;

    public NotesApp()
    {
        Frame = new AppFrame(VosApp.Find("notes")!, "Notes", 760, 480, 1120, 760);
        _saveTimer = UiThread.Timer(TimeSpan.FromMilliseconds(500), SaveCurrent, repeating: false);

        // ---- list pane
        var listPane = new Grid { Padding = new Thickness(12, 4, 8, 12), RowSpacing = 10 };
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search = Kit.SearchBox("Search notes", _ => Reload());
        top.Children.Add(_search);
        var add = Buttons.Accent(Ui.Icon(Glyphs.Add, 14, "Vos.OnAccent"), NewNote, Tokens.RadiusControl, new Thickness(10, 8, 10, 8));
        ToolTipService.SetToolTip(add, "New note (Ctrl+N)");
        Buttons.AutomationName(add, "New note");
        Grid.SetColumn(add, 1);
        top.Children.Add(add);
        listPane.Children.Add(top);
        var filters = Kit.Segments([("all", "All"), ("pinned", "Pinned"), ("trash", "Trash")], _filter, f => { _filter = f; SaveCurrent(); _current = null; Reload(); ShowEditor(); });
        Grid.SetRow(filters, 1);
        listPane.Children.Add(filters);
        _list = new VirtualList<Note>(CreateRow, BindRow, new StackLayout { Spacing = 2 });
        Grid.SetRow(_list, 2);
        listPane.Children.Add(_list);
        _count = Ui.Text("", 12, "Vos.TextTertiary");
        Grid.SetRow(_count, 3);
        listPane.Children.Add(_count);

        // ---- editor pane
        _editorPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editorPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editorPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _editorPane.Padding = new Thickness(40, 18, 40, 16);
        _toolbar = Kit.Row(2,
            TextTool("B", "Bold (Ctrl+B)", () => Format(f => f.Bold = FormatEffect.Toggle), bold: true),
            TextTool("I", "Italic (Ctrl+I)", () => Format(f => f.Italic = FormatEffect.Toggle), italic: true),
            TextTool("U", "Underline (Ctrl+U)", () => Format(f => f.Underline = f.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None), decoration: Windows.UI.Text.TextDecorations.Underline),
            TextTool("S", "Strikethrough", () => Format(f => f.Strikethrough = FormatEffect.Toggle), decoration: Windows.UI.Text.TextDecorations.Strikethrough),
            Separator(),
            TextTool("H", "Heading", ToggleHeading, bold: true),
            Tool(Glyphs.List, "Bulleted list", () => List(MarkerType.Bullet)),
            TextTool("1.", "Numbered list", () => List(MarkerType.Arabic)));
        _pin = Tool(Glyphs.Pin, "Pin", TogglePin);
        _delete = Tool(Glyphs.Delete, "Move to trash", DeleteCurrent);
        var more = Tool(Glyphs.More, "More", () => { });
        more.Flyout = MoreMenu();
        var right = Kit.Row(2, _pin, _delete, more);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        var bar = new Grid();
        bar.Children.Add(_toolbar);
        bar.Children.Add(right);
        _editorPane.Children.Add(bar);

        _title = new TextBox { PlaceholderText = "Title", FontSize = 26, FontFamily = Tokens.Display, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = Clear(), Padding = new Thickness(0, 14, 0, 4) };
        Borderless(_title);
        _title.TextChanged += (_, _) => Changed();
        Kit.OnEnter(_title, () => _editor!.Focus(FocusState.Keyboard));
        Grid.SetRow(_title, 1);
        _editorPane.Children.Add(_title);

        _editor = new RichEditBox { BorderThickness = new Thickness(0), Background = Clear(), Padding = new Thickness(0, 6, 0, 0), FontSize = 15, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Start writing…", IsSpellCheckEnabled = true };
        Borderless(_editor);
        _editor.TextChanged += (_, _) => Changed();
        _meta = Ui.Text("", 12, "Vos.TextTertiary");
        var editorStack = new Grid();
        editorStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editorStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorStack.Children.Add(_editor);
        Grid.SetRow(_meta, 1);
        editorStack.Children.Add(_meta);
        Grid.SetRow(editorStack, 2);
        _editorPane.Children.Add(editorStack);

        var layer = new Border { Background = Ui.B("Vos.Layer"), BorderBrush = Ui.B("Vos.StrokeSubtle"), BorderThickness = new Thickness(1, 1, 0, 0), CornerRadius = new CornerRadius(Tokens.RadiusWindow, 0, 0, 0), Child = _editorPane };
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(listPane);
        Grid.SetColumn(layer, 1);
        root.Children.Add(layer);
        Frame.Body.Content = root;

        Frame.Root.KeyDown += OnKey;
        Frame.Closed += SaveCurrent;
        Reload();
        _current = _list.Items.FirstOrDefault();
        ShowEditor();
    }

    public void HandleArgs(AppArgs args)
    {
        if (args.Get("new") != null || args.Get("section") == "new") NewNote(args.Get("text"));
    }

    // ------------------------------------------------------------------ list

    private sealed class RowParts { public required Grid Root; public required TextBlock Title, Snippet, Date; public required FontIcon Pin; }

    private FrameworkElement CreateRow()
    {
        var g = new Grid { Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(Tokens.RadiusControl), RowSpacing = 2, Background = Clear() };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Ui.Text("", 14, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold);
        var pin = new FontIcon { Glyph = Glyphs.Pin, FontSize = 11, Foreground = Ui.B("Vos.AccentText"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var snippet = Ui.Text("", 12.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap);
        snippet.MaxLines = 2;
        var date = Ui.Text("", 11.5, "Vos.TextTertiary");
        g.Children.Add(title); g.Children.Add(pin);
        Grid.SetRow(snippet, 1); g.Children.Add(snippet);
        Grid.SetRow(date, 2); g.Children.Add(date);
        g.Tag = new RowParts { Root = g, Title = title, Snippet = snippet, Date = date, Pin = pin };
        g.PointerEntered += (_, _) => { if (g.DataContext != _current) g.Background = Ui.B("Vos.ControlHover"); };
        g.PointerExited += (_, _) => { if (g.DataContext != _current) g.Background = Clear(); };
        g.Tapped += (_, _) => { if (g.DataContext is Note n && n != _current) { SaveCurrent(); _current = n; ShowEditor(); _list.RefreshVisible(); } };
        return g;
    }

    private void BindRow(FrameworkElement e, Note n, int index)
    {
        var p = (RowParts)e.Tag;
        e.DataContext = n;
        p.Title.Text = n.DisplayTitle;
        p.Snippet.Text = n.Snippet.Length > 0 ? n.Snippet : "No additional text";
        p.Date.Text = Friendly(n.Updated);
        p.Pin.Visibility = n.Pinned ? Visibility.Visible : Visibility.Collapsed;
        p.Root.Background = n == _current ? Ui.B("Vos.Control") : Clear();
    }

    private void Reload()
    {
        var items = _store.List(_filter, _search.Text.Trim());
        if (_current != null) _current = items.FirstOrDefault(n => n.Id == _current.Id) ?? _current;
        _list.SetItems(items);
        _count.Text = _filter == "trash" ? $"{items.Count} in trash · emptied after 30 days" : $"{items.Count} note{(items.Count == 1 ? "" : "s")}";
    }

    private static string Friendly(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        if (local.Date == DateTime.Today) return local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return local.Year == DateTime.Today.Year ? local.ToString("d MMM", CultureInfo.CurrentCulture) : local.ToString("d", CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------------ editor

    private void ShowEditor()
    {
        _loading = true;
        try
        {
            bool has = _current != null;
            _editorPane.Visibility = Visibility.Visible;
            if (!has)
            {
                _editorPane.Children[^1].Visibility = Visibility.Collapsed;
                _title.Visibility = Visibility.Collapsed;
                _toolbar.Visibility = Visibility.Collapsed;
                _pin.Visibility = _delete.Visibility = Visibility.Collapsed;
                ShowEmpty();
                return;
            }
            HideEmpty();
            _editorPane.Children[^1].Visibility = Visibility.Visible;
            _title.Visibility = Visibility.Visible;
            bool trashed = _current!.Trashed;
            _toolbar.Visibility = trashed ? Visibility.Collapsed : Visibility.Visible;
            _pin.Visibility = trashed ? Visibility.Collapsed : Visibility.Visible;
            _delete.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(_delete, trashed ? "Delete forever" : "Move to trash");
            _pin.Content = Ui.Icon(_current.Pinned ? Glyphs.Unpin : Glyphs.Pin, 14);
            ToolTipService.SetToolTip(_pin, _current.Pinned ? "Unpin" : "Pin");
            _title.Text = _current.Title;
            _title.IsReadOnly = trashed;
            if (_current.Rtf.Length > 0) _editor.Document.SetText(TextSetOptions.FormatRtf, _current.Rtf);
            else _editor.Document.SetText(TextSetOptions.None, _current.Text);
            _editor.IsReadOnly = trashed;
            _meta.Text = trashed ? "In the trash. Restore it from the menu (…) to edit." : $"Edited {Friendly(_current.Updated)} · created {_current.Created.ToLocalTime():d}";
        }
        finally { _loading = false; }
    }

    private Border? _empty;

    private void ShowEmpty()
    {
        if (_empty != null) return;
        var (title, text) = _filter == "trash" ? ("The trash is empty", "Deleted notes stay here for 30 days.") : ("No note selected", "Create a note or pick one from the list. Everything is saved as you type.");
        _empty = new Border { Child = Kit.Empty(Glyphs.Note, title, text, _filter == "trash" ? null : ("New note", () => NewNote())) };
        Grid.SetRowSpan(_empty, 3);
        _editorPane.Children.Add(_empty);
    }

    private void HideEmpty()
    {
        if (_empty == null) return;
        _editorPane.Children.Remove(_empty);
        _empty = null;
    }

    private void Changed()
    {
        if (_loading || _current == null || _current.Trashed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveCurrent()
    {
        _saveTimer.Stop();
        if (_current == null || _current.Trashed) return;
        _editor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        _editor.Document.GetText(TextGetOptions.UseCrlf, out var text);
        text = text.TrimEnd('\r', '\n');
        if (rtf == _current.Rtf && text == _current.Text && _title.Text == _current.Title) return;
        _current.Rtf = rtf;
        _current.Text = text;
        _current.Title = _title.Text;
        try { _store.Save(_current); }
        catch (Exception ex) { Log.Error("Note could not be saved", ex); _meta.Text = "Could not save. Your text is still here; V-OS will try again."; return; }
        _meta.Text = $"Saved · {Friendly(_current.Updated)}";
        _list.RefreshVisible();
    }

    private void NewNote() => NewNote(null);

    private void NewNote(string? text)
    {
        SaveCurrent();
        if (_filter == "trash") { _filter = "all"; }
        var n = new Note { Text = text ?? "" };
        _store.Save(n);
        _search.Text = "";
        _current = n;
        Reload();
        ShowEditor();
        _title.Focus(FocusState.Programmatic);
    }

    private void TogglePin()
    {
        if (_current == null) return;
        SaveCurrent();
        _current.Pinned = !_current.Pinned;
        _store.Save(_current);
        Reload();
        ShowEditor();
    }

    private async void DeleteCurrent()
    {
        if (_current == null) return;
        if (_current.Trashed)
        {
            if (!await Dialogs.ConfirmAsync(Frame.Root.XamlRoot, "Delete this note forever?", "It cannot be recovered.", "Delete", destructive: true)) return;
            _store.DeleteForever(_current.Id);
        }
        else
        {
            SaveCurrent();
            _current.Trashed = true;
            _store.Save(_current);
        }
        _current = null;
        Reload();
        _current = _list.Items.FirstOrDefault();
        ShowEditor();
    }

    private MenuFlyout MoreMenu()
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, string glyph, Action a) { var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } }; i.Click += (_, _) => a(); menu.Items.Add(i); return i; }
        var restore = Item("Restore from trash", Glyphs.Restart, () => { if (_current == null) return; _current.Trashed = false; _store.Save(_current); Reload(); ShowEditor(); });
        Item("Copy as text", Glyphs.Copy, () =>
        {
            if (_current == null) return;
            SaveCurrent();
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText((_current.Title.Length > 0 ? _current.Title + "\r\n\r\n" : "") + _current.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        });
        Item("Export…", Glyphs.Share, Export);
        menu.Items.Add(new MenuFlyoutSeparator());
        var empty = Item("Empty trash", Glyphs.Trash, async () =>
        {
            if (!await Dialogs.ConfirmAsync(Frame.Root.XamlRoot, "Empty the trash?", $"{_store.Count(true)} notes will be deleted forever.", "Empty trash", destructive: true)) return;
            _store.EmptyTrash();
            if (_current?.Trashed == true) _current = null;
            Reload();
            ShowEditor();
        });
        menu.Opening += (_, _) => { restore.Visibility = _current?.Trashed == true ? Visibility.Visible : Visibility.Collapsed; empty.IsEnabled = _store.Count(true) > 0; };
        return menu;
    }

    private async void Export()
    {
        if (_current == null) return;
        SaveCurrent();
        var name = string.Concat(_current.DisplayTitle.Split(Path.GetInvalidFileNameChars())).Trim();
        var path = await Pickers.SaveAsync(Frame.Hwnd, name.Length > 0 ? name : "Note", "Rich text", ".rtf");
        if (path == null) return;
        try { await File.WriteAllTextAsync(path, path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? _current.Text : _current.Rtf); }
        catch (Exception ex) { await Dialogs.InfoAsync(Frame.Root.XamlRoot, "Export failed", ex.Message); }
    }

    // ------------------------------------------------------------------ formatting

    private void Format(Action<ITextCharacterFormat> f)
    {
        if (_current?.Trashed != false) return;
        var fmt = _editor.Document.Selection.CharacterFormat;
        f(fmt);
        _editor.Focus(FocusState.Programmatic);
    }

    private void ToggleHeading()
    {
        if (_current?.Trashed != false) return;
        var sel = _editor.Document.Selection;
        sel.Expand(TextRangeUnit.Paragraph);
        bool isHeading = sel.CharacterFormat.Size >= 19;
        sel.CharacterFormat.Size = isHeading ? 15 : 20;
        sel.CharacterFormat.Bold = isHeading ? FormatEffect.Off : FormatEffect.On;
        _editor.Focus(FocusState.Programmatic);
    }

    private void List(MarkerType type)
    {
        if (_current?.Trashed != false) return;
        var p = _editor.Document.Selection.ParagraphFormat;
        p.ListType = p.ListType == type ? MarkerType.None : type;
        _editor.Focus(FocusState.Programmatic);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        if (e.Key == VirtualKey.N) { NewNote(); e.Handled = true; }
        else if (e.Key == VirtualKey.F) { _search.Focus(FocusState.Keyboard); e.Handled = true; }
        else if (e.Key == VirtualKey.S) { SaveCurrent(); e.Handled = true; }
    }

    // ------------------------------------------------------------------ helpers

    private static SolidColorBrush Clear() => new(Microsoft.UI.Colors.Transparent);

    private static Button Tool(string glyph, string tip, Action a) => Buttons.Icon(glyph, a, 34, 14, tip);

    private static Button TextTool(string text, string tip, Action a, bool bold = false, bool italic = false, Windows.UI.Text.TextDecorations decoration = Windows.UI.Text.TextDecorations.None)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = 15, FontFamily = new FontFamily("Georgia"), TextDecorations = decoration,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var b = Buttons.Subtle(label, a, 17, new Thickness(0));
        b.Width = b.Height = 34;
        ToolTipService.SetToolTip(b, tip);
        Buttons.AutomationName(b, tip);
        return b;
    }

    private static Border Separator() => new() { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0), Background = Ui.B("Vos.Stroke"), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Text boxes that look like part of the page: no border or fill in any state.</summary>
    private static void Borderless(Control c)
    {
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled" })
            c.Resources[key] = Clear();
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            c.Resources[key] = Clear();
        c.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
    }
}

