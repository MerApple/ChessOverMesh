using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// The Nodes page — the MAUI port of the desktop nodes window. Lists every known node with its name, hardware
/// model, role, signal and last-heard time, and a DM / Block toggle per node (persisted via DeviceCache and
/// wired into chat: DM lists the node as a chat TX target; Block ignores its incoming DMs). An "Update nodes"
/// button refreshes the list from the device, and "Map" opens the node map. Shares MainPage's live node state.
///
/// The list is a virtualizing <see cref="CollectionView"/> bound to a stable set of <see cref="NodeRowVM"/>
/// instances: only the visible rows are realized (fast even with hundreds of nodes), and incoming data updates a
/// node's row in place rather than rebuilding the whole list. Live updates are coalesced onto a 1-second timer so
/// a burst of packets doesn't thrash the list.
/// </summary>
public sealed class NodesPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");
    static readonly Color Rule = Color.FromArgb("#3F3F46");
    static readonly Color Accent = Color.FromArgb("#7FB2E5");

    readonly MainPage _main;
    readonly TaskCompletionSource<bool> _tcs = new();
    public Task Completion => _tcs.Task;

    readonly Entry _search;
    readonly Picker _sort;
    readonly Label _status, _header;
    readonly CollectionView _cv;
    readonly ObservableCollection<NodeRowVM> _items = new();
    readonly Dictionary<uint, NodeRowVM> _byNum = new();
    readonly Button _updateBtn, _mapBtn;
    bool _busy;

    IDispatcherTimer? _refreshTimer;
    bool _dirty;   // a NodesChanged arrived; the next timer tick recomputes the list

    string _nodeFontFamily = "monospace";   // from the Colour/Fonts "Nodes" setting
    double _nodeFontSize = 13;

    // The sort modes, matching the desktop nodes window.
    static readonly string[] SortModes = { "Name", "Favorite", "Type", "Hardware", "Heard", "Signal", "DM", "Blocked", "Environment" };

    public NodesPage(MainPage main)
    {
        _main = main;
        (_nodeFontFamily, _nodeFontSize) = main.NodesFont;   // seed from the persisted Nodes font
        Title = "Nodes";
        BackgroundColor = Bg;

        _header = new Label { Text = "Nodes", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold };
        _status = new Label { TextColor = Dim, FontSize = 12 };

        _search = new Entry { Placeholder = "Search name or !hex…", TextColor = Fg, BackgroundColor = Bg };
        _search.TextChanged += (_, _) => Rebuild();   // filtering is cheap now (only visible rows render)

        _sort = new Picker { Title = "Sort", TextColor = Fg, BackgroundColor = Bg };
        foreach (var s in SortModes) _sort.Items.Add(s);
        _sort.SelectedIndex = 0;   // Name
        _sort.SelectedIndexChanged += (_, _) => Rebuild();
        var sortRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        sortRow.Add(new Label { Text = "Sort", TextColor = Dim, VerticalOptions = LayoutOptions.Center }, 0, 0);
        sortRow.Add(_sort, 1, 0);

        _updateBtn = new Button { Text = "Update nodes", HeightRequest = 40, Padding = new Thickness(12, 0) };
        _updateBtn.Clicked += OnUpdate;
        _mapBtn = new Button { Text = "Map", HeightRequest = 40, Padding = new Thickness(12, 0) };
        _mapBtn.Clicked += OnMap;
        var btnRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        btnRow.Add(_updateBtn, 0, 0);
        btnRow.Add(_mapBtn, 1, 0);

        var close = new Button { Text = "Close", HeightRequest = 44, Margin = new Thickness(0, 8, 0, 0) };
        close.Clicked += async (_, _) => await CloseAsync();

        // Pinned controls (top) and Close (bottom); only the node list scrolls, so Close stays visible no matter
        // how many nodes there are.
        var controls = new VerticalStackLayout { Spacing = 8 };
        controls.Add(_header);
        controls.Add(_status);
        controls.Add(_search);
        controls.Add(sortRow);
        controls.Add(btnRow);
        controls.Add(new BoxView { HeightRequest = 1, Color = Rule });

        _cv = new CollectionView
        {
            ItemsSource = _items,
            ItemTemplate = NodeTemplate(),
            SelectionMode = SelectionMode.None,
            VerticalOptions = LayoutOptions.Fill,
        };

        var root = new Grid
        {
            Padding = 12,
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),   // controls
                new RowDefinition(GridLength.Star),   // scrolling node list (virtualized)
                new RowDefinition(GridLength.Auto),   // close button
            },
        };
        root.Add(controls, 0, 0);
        root.Add(_cv, 0, 1);
        root.Add(close, 0, 2);
        Content = root;
    }

    /// <summary>Restyles the node rows live when the "Nodes" font/size setting changes.</summary>
    public void ApplyFont(string family, double size)
    {
        _nodeFontFamily = family; _nodeFontSize = size;
        foreach (var vm in _byNum.Values) { vm.FontFamily = family; vm.FontSize = size; }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _main.NodesPageRef = this;   // let a Nodes font/size change restyle us live
        _main.NodesChanged += OnNodesChanged;
        // Coalesce live updates: NodesChanged just marks the list dirty; this timer applies them at most once a
        // second, so a burst of incoming packets updates rows smoothly instead of rebuilding on every packet.
        _refreshTimer = Dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.Tick += (_, _) => { if (_dirty) { _dirty = false; Rebuild(); } };
        _refreshTimer.Start();
        Rebuild();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_main.NodesPageRef == this) _main.NodesPageRef = null;
        _main.NodesChanged -= OnNodesChanged;
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    void OnNodesChanged() => MainThread.BeginInvokeOnMainThread(() => _dirty = true);

    async void OnUpdate(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true; SetBusy(true);
        try { int n = await _main.FetchNodesAsync(t => _status.Text = t); _status.Text = $"Done — {n} node(s) known."; }
        catch (Exception ex) { _status.Text = $"Update failed: {ex.Message}"; }
        finally { _busy = false; SetBusy(false); Rebuild(); }
    }

    async void OnMap(object? sender, EventArgs e)
    {
        try { await Navigation.PushModalAsync(new MapPage(_main.GetNodePositions())); }
        catch (Exception ex) { _status.Text = $"Could not open the map: {ex.Message}"; }
    }

    // Long-press menu on a node — mirrors the desktop right-click menu.
    async Task ShowNodeMenuAsync(MeshNode n)
    {
        // Request info / position / traceroute now live inside the "Show all info" window. For our own node we also
        // offer the manual broadcasts (announce this node / its position to the whole mesh).
        var opts = new List<string> { "Show all info", "Open in Google Maps" };
        if (n.IsSelf) { opts.Add("Broadcast my node info"); opts.Add("Broadcast my position"); }
        else
        {
            opts.Add(n.IsFavorite ? "Remove favorite" : "Mark as favorite");
            opts.Add(n.IsIgnored ? "Un-ignore on device" : "Ignore on device");
            opts.Add("Remote admin…");
            opts.Add("Remove from device");
        }
        string choice = await DisplayActionSheet(n.Display, "Cancel", null, opts.ToArray());
        switch (choice)
        {
            case "Mark as favorite":
            case "Remove favorite":
                try { await _main.SetFavoriteNodeAsync(n.Num, !n.IsFavorite); _status.Text = $"{(n.IsFavorite ? "Removed favorite" : "Marked favorite")}: {n.Display}"; }
                catch (Exception ex) { _status.Text = $"Favorite failed: {ex.Message}"; }
                break;
            case "Ignore on device":
            case "Un-ignore on device":
                try { await _main.SetIgnoredNodeAsync(n.Num, !n.IsIgnored); _status.Text = $"{(n.IsIgnored ? "Un-ignored" : "Ignored")} {n.Display} on the device."; }
                catch (Exception ex) { _status.Text = $"Ignore failed: {ex.Message}"; }
                break;
            case "Remote admin…":
                await ShowRemoteAdminMenuAsync(n);
                break;
            case "Remove from device":
                bool ok = await DisplayAlert("Remove node",
                    $"Remove {n.Display} from the device's node database?\n\nUse this when the node reinstalled its firmware and direct messages stopped working (its stored public key is stale). After removal the device re-learns the node — and its new key — the next time it hears from it.",
                    "Remove", "Cancel");
                if (!ok) break;
                _status.Text = $"Removing {n.Display} from the device…";
                try { await _main.RemoveNodeAsync(n.Num); _status.Text = $"Removed {n.Display}. It returns (with a fresh key) when next heard — then DM should work."; }
                catch (Exception ex) { _status.Text = $"Remove failed: {ex.Message}"; }
                break;
            case "Open in Google Maps":
                var url = _main.NodeMapsUrl(n.Num);
                if (url == null)
                    await DisplayAlert("No location",
                        $"No position data found for {n.Display}.\n\nThis node hasn't shared its location yet. Use \"Request position\" to ask for it, then try again.", "OK");
                else
                    await Launcher.Default.OpenAsync(url);
                break;
            case "Show all info":
                await Navigation.PushModalAsync(new TelemetryPage(_main, n));
                break;
            case "Broadcast my node info":
                _status.Text = "Broadcasting node info to the mesh…";
                try { await _main.BroadcastOwnNodeInfoAsync(); _status.Text = "Node info broadcast sent."; }
                catch (Exception ex) { _status.Text = $"Broadcast failed: {ex.Message}"; }
                break;
            case "Broadcast my position":
                _status.Text = "Broadcasting position to the mesh…";
                try { await _main.BroadcastOwnPositionAsync(); _status.Text = "Position broadcast sent."; }
                catch (Exception ex) { _status.Text = $"Broadcast failed: {ex.Message}"; }
                break;
        }
    }

    // Remote-admin submenu: send admin commands to ANOTHER node (needs a shared "admin" channel on both nodes).
    async Task ShowRemoteAdminMenuAsync(MeshNode n)
    {
        string choice = await DisplayActionSheet($"Remote admin — {n.Display}", "Cancel", null,
            "Reboot", "Shut down", "Reset node DB", "Factory reset");
        var (action, warn) = choice switch
        {
            "Reboot" => ("reboot", "Reboots this remote node in a few seconds."),
            "Shut down" => ("shutdown", "Shuts this remote node down — it must be powered on again by hand."),
            "Reset node DB" => ("nodedb", "Clears this remote node's list of known nodes."),
            "Factory reset" => ("factory", "FACTORY-RESETS this remote node — ALL its settings are wiped. This cannot be undone."),
            _ => (null, null),
        };
        if (action == null) return;
        bool ok = await DisplayAlert($"Remote {action}?",
            $"{warn}\n\nTarget: {n.Display}\n\nRemote admin needs a shared channel named \"admin\" on BOTH this node and the target. Continue?",
            "Send", "Cancel");
        if (!ok) return;
        _status.Text = $"Sending remote {action} to {n.Display}… (negotiating session key)";
        try
        {
            var err = await _main.RemoteAdminAsync(n.Num, action);
            _status.Text = err == null ? $"Remote {action} sent to {n.Display}." : $"Remote {action} failed: {err}";
        }
        catch (Exception ex) { _status.Text = $"Remote {action} failed: {ex.Message}"; }
    }

    void SetBusy(bool busy) { _updateBtn.IsEnabled = !busy; _search.IsEnabled = !busy; }

    // Recomputes the filtered/sorted node set and reconciles it into _items, reusing the existing view-model per
    // node so its row (and toggle state) stays put and only changed fields re-render.
    void Rebuild()
    {
        string query = (_search.Text ?? "").Trim();

        // The device's known nodes, plus placeholder rows for any node we hold a DM/Block pref for that isn't in
        // the device DB (e.g. a DM arrived from a node we never got NodeInfo for) — so its pref stays toggleable.
        var nodes = _main.GetNodes().ToList();
        var known = nodes.Select(n => n.Num).ToHashSet();
        foreach (var num in _main.NodePrefNums)
            if (!known.Contains(num) && num != _main.MyNodeNum)
                nodes.Add(new MeshNode(num, string.Empty, string.Empty, false));

        var filtered = nodes.Where(n => query.Length == 0 || n.Display.Contains(query, StringComparison.OrdinalIgnoreCase));

        // Self always pins to the top; then the chosen sort, with name as the tiebreaker — matching the desktop.
        string mode = _sort.SelectedItem as string ?? "Name";
        var ordered = filtered.OrderByDescending(n => n.IsSelf);
        ordered = mode switch
        {
            "Favorite"    => ordered.ThenByDescending(n => n.IsFavorite),   // favorites first (after self)
            "Type"        => ordered.ThenBy(n => string.IsNullOrEmpty(n.Role) ? "￿" : n.Role, StringComparer.OrdinalIgnoreCase),
            "Hardware"    => ordered.ThenBy(n => string.IsNullOrEmpty(n.HwModel) ? "￿" : n.HwModel, StringComparer.OrdinalIgnoreCase),
            "Heard"       => ordered.ThenByDescending(n => n.LastHeard),                       // most recently heard first
            "Signal"      => ordered.ThenByDescending(n => _main.NodeSignalSortKey(n.Num)),    // best link first
            "DM"          => ordered.ThenByDescending(n => _main.NodePrefFor(n.Num).Dm),
            "Blocked"     => ordered.ThenByDescending(n => _main.NodePrefFor(n.Num).Block),
            "Environment" => ordered.ThenByDescending(n => _main.NodeHasEnvironment(n.Num)),
            _             => ordered,
        };

        var sorted = ordered.ThenBy(SortName, StringComparer.OrdinalIgnoreCase).ToList();

        var desired = new List<NodeRowVM>(sorted.Count);
        foreach (var n in sorted)
        {
            if (!_byNum.TryGetValue(n.Num, out var vm))
            {
                _byNum[n.Num] = vm = new NodeRowVM(_main, n.Num, n.IsSelf);
                vm.FontFamily = _nodeFontFamily; vm.FontSize = _nodeFontSize;
            }
            var (dm, block) = _main.NodePrefFor(n.Num);
            vm.Update(n, NameText(n), n.IsSelf ? Accent : Fg, Detail(n), dm, block);
            desired.Add(vm);
        }

        // Forget view-models for nodes that are gone (e.g. filtered out / removed) so the cache doesn't grow.
        if (_byNum.Count > desired.Count)
        {
            var keep = desired.Select(v => v.Num).ToHashSet();
            foreach (var gone in _byNum.Keys.Where(k => !keep.Contains(k)).ToList()) _byNum.Remove(gone);
        }

        Sync(desired);

        _header.Text = $"Known nodes: {sorted.Count}";
        if (sorted.Count == 0 && !_busy)
            _status.Text = "No nodes loaded yet. Tap \"Update nodes\" to fetch them from the device.";
    }

    // Reconciles _items to match `desired` in order, using granular add/move/remove so unchanged rows are left
    // alone (preserves scroll position and only re-renders what actually moved).
    void Sync(List<NodeRowVM> desired)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            if (!desired.Contains(_items[i])) _items.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            var vm = desired[i];
            int cur = _items.IndexOf(vm);
            if (cur < 0) _items.Insert(i, vm);
            else if (cur != i) _items.Move(cur, i);
        }
    }

    string NameText(MeshNode n) => (n.IsSelf ? "★ " : "") + (n.IsFavorite ? "⭐ " : "") + (n.IsIgnored ? "🚫 " : "") + n.Display;

    string Detail(MeshNode n)
    {
        var bits = new List<string>();
        string hw = !string.IsNullOrEmpty(n.HwModel) ? n.HwModel : (n.IsSelf ? _main.OwnHardwareModel ?? "" : "");
        if (hw.Length > 0) bits.Add(hw);
        if (!string.IsNullOrEmpty(n.Role)) bits.Add($"[{n.Role}]");
        if (_main.NodeSignal(n.Num) is { } sig) bits.Add(sig);
        if (n.LastHeard > 0)
        {
            // Last-heard is stamped with the radio's own clock; if that clock is set ahead of real time the stamp
            // lands in the future. Flag that explicitly instead of showing a confusing blank/future "ago".
            if (DateTimeOffset.FromUnixTimeSeconds(n.LastHeard) > DateTimeOffset.UtcNow.AddMinutes(1))
                bits.Add($"heard {HeardAt(n.LastHeard)} (device clock ahead)");
            else
                bits.Add($"heard {Ago(n.LastHeard)} ({HeardAt(n.LastHeard)})");
        }
        return string.Join("  ·  ", bits);
    }

    // The recycled row template. Fields bind to the row's NodeRowVM, so a cell scrolled into view (or rebound to a
    // different node) just picks up that node's current values.
    DataTemplate NodeTemplate() => new(() =>
    {
        var nameLbl = new Label { FontAttributes = FontAttributes.Bold };
        nameLbl.SetBinding(Label.TextProperty, nameof(NodeRowVM.NameText));
        nameLbl.SetBinding(Label.TextColorProperty, nameof(NodeRowVM.NameColor));
        nameLbl.SetBinding(Label.FontFamilyProperty, nameof(NodeRowVM.FontFamily));
        nameLbl.SetBinding(Label.FontSizeProperty, nameof(NodeRowVM.FontSize));

        var detailLbl = new Label { TextColor = Dim, FontSize = 12 };
        detailLbl.SetBinding(Label.TextProperty, nameof(NodeRowVM.DetailText));
        detailLbl.SetBinding(VisualElement.IsVisibleProperty, nameof(NodeRowVM.HasDetail));

        var info = new VerticalStackLayout { Spacing = 2 };
        info.Add(nameLbl);
        info.Add(detailLbl);

        // Long-press the node info → the same menu as the desktop right-click. The native Android LongClick reads
        // the row's current BindingContext, so it works correctly even as the cell is recycled to other nodes.
#if ANDROID
        void OnInfoLongClick(object? s, Android.Views.View.LongClickEventArgs e)
        {
            e.Handled = true;
            if (info.BindingContext is NodeRowVM vm)
                MainThread.BeginInvokeOnMainThread(() => _ = ShowNodeMenuAsync(vm.Node));
        }
        info.HandlerChanged += (_, _) =>
        {
            if (info.Handler?.PlatformView is Android.Views.View av)
            {
                av.LongClickable = true;
                av.LongClick -= OnInfoLongClick;
                av.LongClick += OnInfoLongClick;
            }
        };
#endif

        var dmSwitch = new Switch { VerticalOptions = LayoutOptions.Center };
        dmSwitch.SetBinding(Switch.IsToggledProperty, new Binding(nameof(NodeRowVM.Dm), BindingMode.TwoWay));
        dmSwitch.SetBinding(Switch.IsEnabledProperty, nameof(NodeRowVM.DmEnabled));

        var blkSwitch = new Switch { VerticalOptions = LayoutOptions.Center };
        blkSwitch.SetBinding(Switch.IsToggledProperty, new Binding(nameof(NodeRowVM.Block), BindingMode.TwoWay));

        var right = new HorizontalStackLayout { Spacing = 12, VerticalOptions = LayoutOptions.Center };
        right.Add(MiniToggle("DM", dmSwitch));
        right.Add(MiniToggle("Block", blkSwitch));
        right.SetBinding(VisualElement.IsVisibleProperty, nameof(NodeRowVM.ShowToggles));   // own node has no toggles

        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 8),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        grid.Add(info, 0, 0);
        grid.Add(right, 1, 0);

        var cell = new VerticalStackLayout { Spacing = 0 };
        cell.Add(grid);
        cell.Add(new BoxView { HeightRequest = 1, Color = Rule });
        return cell;
    });

    static View MiniToggle(string label, Switch sw)
    {
        var col = new VerticalStackLayout { Spacing = 0, HorizontalOptions = LayoutOptions.Center };
        col.Add(new Label { Text = label, TextColor = Dim, FontSize = 10, HorizontalOptions = LayoutOptions.Center });
        col.Add(sw);
        return col;
    }

    async Task CloseAsync()
    {
        _tcs.TrySetResult(true);
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        _tcs.TrySetResult(true);
        return base.OnBackButtonPressed();
    }

    // Name used for the alphabetical tiebreaker; unnamed nodes (~hex) sort after named ones.
    static string SortName(MeshNode n) =>
        !string.IsNullOrWhiteSpace(n.LongName) ? n.LongName
        : !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName
        : $"~{n.Num:x8}";

    // The absolute local date + time a node was last heard, e.g. "2026-06-28 09:30".
    static string HeardAt(long epoch) =>
        DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    static string Ago(long epoch)
    {
        if (epoch <= 0) return "";
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (span.TotalSeconds < 0) return "";
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s ago";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    /// <summary>One node's row state. Display fields raise change notifications so an in-place update re-renders
    /// just that row; the DM/Block toggles are two-way bound and persist through <see cref="MainPage"/>. Reused
    /// across rebuilds (keyed by node number) so the visible row and toggle state are stable.</summary>
    sealed class NodeRowVM : INotifyPropertyChanged
    {
        readonly MainPage _main;
        bool _suppress;   // true while we apply persisted prefs, so the toggle bindings don't write back

        public NodeRowVM(MainPage main, uint num, bool isSelf) { _main = main; Num = num; IsSelf = isSelf; }

        public uint Num { get; }
        public bool IsSelf { get; }
        public bool ShowToggles => !IsSelf;
        public MeshNode Node { get; private set; }   // set on first Update, before the row is ever shown

        string _nameText = "";
        public string NameText { get => _nameText; private set => Set(ref _nameText, value); }

        Color _nameColor = Fg;
        public Color NameColor { get => _nameColor; private set => Set(ref _nameColor, value); }

        string _detailText = "";
        public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

        string _fontFamily = "monospace";
        public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }

        double _fontSize = 13;
        public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }

        bool _hasDetail;
        public bool HasDetail { get => _hasDetail; private set => Set(ref _hasDetail, value); }

        bool _dm;
        public bool Dm
        {
            get => _dm;
            set { if (_dm == value) return; _dm = value; Raise(); if (!_suppress) Persist(); }
        }

        bool _block;
        public bool Block
        {
            get => _block;
            set
            {
                if (_block == value) return;
                _block = value; Raise(); Raise(nameof(DmEnabled));
                if (_suppress) return;
                if (_block && _dm) { _dm = false; Raise(nameof(Dm)); }   // Block wins — a blocked node can't be DM-enabled
                Persist();
            }
        }

        public bool DmEnabled => !_block;

        // Refresh the row from the latest node + persisted prefs without triggering a write-back.
        public void Update(MeshNode node, string name, Color nameColor, string detail, bool dm, bool block)
        {
            Node = node;
            NameText = name;
            NameColor = nameColor;
            DetailText = detail;
            HasDetail = detail.Length > 0;
            _suppress = true;
            Block = block;
            Dm = dm;
            _suppress = false;
        }

        void Persist() => _main.SetNodePref(Num, !_block && _dm, _block);

        public event PropertyChangedEventHandler? PropertyChanged;

        void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value; Raise(name);
        }

        void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
