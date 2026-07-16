using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// The Nodes page — the MAUI port of the desktop nodes window. A horizontally-scrolling table with one
/// column per attribute (★/name/type/hardware/heard/signal/environment/DM/block); tapping a column title
/// sorts by it (tapping again reverses), and a Type picker filters to one device role (e.g. only Routers)
/// alongside the search box. DM / Block toggles per node persist via DeviceCache and wire into chat (DM
/// lists the node as a chat TX target; Block ignores its incoming DMs). An "Update nodes" button refreshes
/// the list from the device, and "Map" opens the node map. Shares MainPage's live node state.
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
    readonly Picker _type;
    readonly Label _status, _header;
    readonly Label[] _headerCells;
    readonly CollectionView _cv;
    readonly ObservableCollection<NodeRowVM> _items = new();
    readonly Dictionary<uint, NodeRowVM> _byNum = new();
    readonly Button _updateBtn, _mapBtn;
    bool _busy;

    IDispatcherTimer? _refreshTimer;
    bool _dirty;   // a NodesChanged arrived; the next timer tick recomputes the list

    string _nodeFontFamily = "monospace";   // from the Colour/Fonts "Nodes" setting
    double _nodeFontSize = 13;

    const string AllTypes = "All types";
    const string NoType = "(no type)";

    // The columns, matching the desktop nodes window: caption, sort key, the direction a first tap sorts
    // in, and the column width (dp). Tapping a column title sorts by it; tapping it again reverses. The
    // table is wider than the screen and scrolls horizontally (header + rows together).
    static readonly (string Caption, string Key, bool DefaultAsc, double W)[] Columns =
    {
        ("★", "Fav", false, 34),
        ("Name", "Name", true, 240),
        ("Type", "Type", true, 92),
        ("Hardware", "Hardware", true, 116),
        ("Heard", "Heard", false, 84),
        ("Signal", "Signal", false, 150),
        ("Environment", "Env", false, 170),
        ("DM", "DM", false, 70),
        ("Block", "Block", false, 70),
    };
    static double TableWidth => Columns.Sum(c => c.W);

    // Column-sort state: tapping a column title selects it (with its natural first direction); tapping
    // the same title again reverses.
    string _sortKey = "Name";
    bool _sortAsc = true;
    bool _rebuildingTypes;   // suppresses SelectedIndexChanged while the Type filter's items are rebuilt

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

        _type = new Picker { Title = "Type", TextColor = Fg, BackgroundColor = Bg };
        _type.Items.Add(AllTypes);
        _type.SelectedIndex = 0;
        _type.SelectedIndexChanged += (_, _) => { if (!_rebuildingTypes) Rebuild(); };
        var typeRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        typeRow.Add(new Label { Text = "Type", TextColor = Dim, VerticalOptions = LayoutOptions.Center }, 0, 0);
        typeRow.Add(_type, 1, 0);

        _updateBtn = new Button { Text = "Update nodes", MinimumHeightRequest = 40, Padding = new Thickness(12, 0) };
        _updateBtn.Clicked += OnUpdate;
        _mapBtn = new Button { Text = "Map", MinimumHeightRequest = 40, Padding = new Thickness(12, 0) };
        _mapBtn.Clicked += OnMap;
        var btnRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        btnRow.Add(_updateBtn, 0, 0);
        btnRow.Add(_mapBtn, 1, 0);

        // Solicit info from any node by its id — even one we've never heard from. Sends a NodeInfo
        // request (want_response) to the entered node number; its reply adds it to the list.
        var reqIdBtn = new Button { Text = "Request info by ID", MinimumHeightRequest = 40, Padding = new Thickness(12, 0) };
        reqIdBtn.Clicked += OnRequestById;

        var close = new Button { Text = "Close", MinimumHeightRequest = 44, Margin = new Thickness(0, 8, 0, 0) };
        close.Clicked += async (_, _) => await CloseAsync();

        // Pinned controls (top) and Close (bottom); only the node list scrolls, so Close stays visible no matter
        // how many nodes there are.
        var controls = new VerticalStackLayout { Spacing = 8 };
        controls.Add(_header);
        controls.Add(_status);
        controls.Add(_search);
        controls.Add(typeRow);
        controls.Add(btnRow);
        controls.Add(reqIdBtn);
        controls.Add(new BoxView { HeightRequest = 1, Color = Rule });

        _cv = new CollectionView
        {
            ItemsSource = _items,
            ItemTemplate = NodeTemplate(),
            SelectionMode = SelectionMode.None,
            VerticalOptions = LayoutOptions.Fill,
        };

        // Header row of tappable column titles (the active one shows ▲/▼), sharing the table's column
        // widths so the titles stay aligned with the cells below.
        var headerGrid = new Grid { ColumnSpacing = 0 };
        _headerCells = new Label[Columns.Length];
        for (int i = 0; i < Columns.Length; i++)
        {
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Columns[i].W)));
            var col = Columns[i];
            var lbl = new Label
            {
                Text = col.Caption, TextColor = Dim, FontAttributes = FontAttributes.Bold, FontSize = 13,
                VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.NoWrap, Padding = new Thickness(2, 6),
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (_sortKey == col.Key) _sortAsc = !_sortAsc;
                else { _sortKey = col.Key; _sortAsc = col.DefaultAsc; }
                UpdateHeaderArrows();
                Rebuild();
            };
            lbl.GestureRecognizers.Add(tap);
            _headerCells[i] = lbl;
            headerGrid.Add(lbl, i, 0);
        }
        UpdateHeaderArrows();

        // Header + list live in one fixed-width table inside a horizontal ScrollView, so the wide rows
        // scroll sideways together with their titles while the list itself still scrolls (and virtualizes)
        // vertically.
        var table = new Grid
        {
            WidthRequest = TableWidth,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),   // column titles
                new RowDefinition(GridLength.Auto),   // rule under the titles
                new RowDefinition(GridLength.Star),   // scrolling node list (virtualized)
            },
        };
        table.Add(headerGrid, 0, 0);
        table.Add(new BoxView { HeightRequest = 1, Color = Rule }, 0, 1);
        table.Add(_cv, 0, 2);

        var hScroll = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = table, VerticalOptions = LayoutOptions.Fill };

        var root = new Grid
        {
            Padding = 12,
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),   // controls
                new RowDefinition(GridLength.Star),   // the table (titles + virtualized list)
                new RowDefinition(GridLength.Auto),   // close button
            },
        };
        root.Add(controls, 0, 0);
        root.Add(hScroll, 0, 1);
        root.Add(close, 0, 2);
        Content = root;
    }

    // The active column title carries the sort direction (▲/▼) and brightens; the rest show plain captions.
    void UpdateHeaderArrows()
    {
        for (int i = 0; i < Columns.Length; i++)
        {
            var c = Columns[i];
            bool active = c.Key == _sortKey;
            _headerCells[i].Text = active ? $"{c.Caption} {(_sortAsc ? "▲" : "▼")}" : c.Caption;
            _headerCells[i].TextColor = active ? Fg : Dim;
        }
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

    async void OnRequestById(object? sender, EventArgs e)
    {
        var entered = await ThemedDialogs.Prompt(this, "Request node info", "Enter a node ID (!hex or number)",
                                                 placeholder: "!a1b2c3d4", keyboard: Keyboard.Default);
        if (entered == null) return;   // cancelled
        if (!MeshtasticHttpClient.TryParseNodeId(entered, out var num))
        {
            _status.Text = $"Couldn't read a node id from \"{entered.Trim()}\". Use !hex (e.g. !a1b2c3d4) or a number.";
            return;
        }
        _status.Text = await _main.RequestNodeInfoForAsync(num);
    }

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
        try { await Navigation.PushModalAsync(new MapPage(_main.GetNodePositions(), _main.GetPositionHistoryMap(),
            liveSnapshot: () => ChessOverMesh.Mesh.NodeMap.SerializeNodes(_main.GetNodePositions(), _main.GetPositionHistoryMap()))); }
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
        string choice = await ThemedDialogs.ActionSheet(this, n.Display, "Cancel", null, opts.ToArray());
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
                bool ok = await ThemedDialogs.Alert(this, "Remove node",
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
                    await ThemedDialogs.Alert(this, "No location",
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
        string choice = await ThemedDialogs.ActionSheet(this, $"Remote admin — {n.Display}", "Cancel", null,
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
        bool ok = await ThemedDialogs.Alert(this, $"Remote {action}?",
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

        RefreshTypeFilter(nodes);
        string typeFilter = _type.SelectedItem as string ?? AllTypes;

        IEnumerable<MeshNode> filtered = nodes;
        if (typeFilter == NoType) filtered = filtered.Where(n => string.IsNullOrEmpty(n.Role));
        else if (typeFilter != AllTypes) filtered = filtered.Where(n => string.Equals(n.Role, typeFilter, StringComparison.OrdinalIgnoreCase));
        if (query.Length > 0)
            filtered = filtered.Where(n => n.Display.Contains(query, StringComparison.OrdinalIgnoreCase));

        // Applies the tapped direction to the given column key.
        IOrderedEnumerable<MeshNode> Then<TKey>(IOrderedEnumerable<MeshNode> src, Func<MeshNode, TKey> k, IComparer<TKey>? c = null) =>
            _sortAsc ? src.ThenBy(k, c) : src.ThenByDescending(k, c);

        // Self always pins to the top; then the tapped column, with name as the tiebreaker — matching the desktop.
        var ordered = filtered.OrderByDescending(n => n.IsSelf);
        ordered = _sortKey switch
        {
            "Fav"      => Then(ordered, n => n.IsFavorite),
            "Type"     => Then(ordered, n => string.IsNullOrEmpty(n.Role) ? "￿" : n.Role, StringComparer.OrdinalIgnoreCase),
            "Hardware" => Then(ordered, n => string.IsNullOrEmpty(n.HwModel) ? "￿" : n.HwModel, StringComparer.OrdinalIgnoreCase),
            "Heard"    => Then(ordered, n => n.LastHeard),                       // descending = most recently heard first
            "Signal"   => Then(ordered, n => _main.NodeSignalSortKey(n.Num)),    // descending = best link first
            "Env"      => Then(ordered, n => _main.NodeHasEnvironment(n.Num)),
            "DM"       => Then(ordered, n => _main.NodePrefFor(n.Num).Dm),
            "Block"    => Then(ordered, n => _main.NodePrefFor(n.Num).Block),
            _          => Then(ordered, SortName, StringComparer.OrdinalIgnoreCase),
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
            vm.Update(n,
                marks: (n.IsSelf ? "★" : "") + (n.IsFavorite ? "⭐" : "") + (n.IsIgnored ? "🚫" : ""),
                name: n.Display,
                nameColor: n.IsSelf ? Accent : Fg,
                type: n.Role,
                hw: !string.IsNullOrEmpty(n.HwModel) ? n.HwModel : (n.IsSelf ? _main.OwnHardwareModel ?? "" : ""),
                heard: HeardCell(n),
                signal: _main.NodeSignalCell(n.Num) ?? "",
                env: _main.NodeEnvironmentCell(n.Num) ?? "",
                dm: dm, block: block);
            desired.Add(vm);
        }

        // Forget view-models for nodes that are gone (e.g. filtered out / removed) so the cache doesn't grow.
        if (_byNum.Count > desired.Count)
        {
            var keep = desired.Select(v => v.Num).ToHashSet();
            foreach (var gone in _byNum.Keys.Where(k => !keep.Contains(k)).ToList()) _byNum.Remove(gone);
        }

        Sync(desired);

        bool isFiltered = query.Length > 0 || typeFilter != AllTypes;
        _header.Text = isFiltered ? $"Nodes: {desired.Count} of {nodes.Count}" : $"Known nodes: {nodes.Count}";
        if (!_busy)
        {
            if (nodes.Count == 0)
                _status.Text = "No nodes loaded yet. Tap \"Update nodes\" to fetch them from the device.";
            else if (desired.Count == 0)
                _status.Text = query.Length > 0 ? $"No nodes match \"{query}\"." : $"No nodes of type {typeFilter}.";
        }
    }

    // Keeps the Type filter's choices in sync with the roles actually present, preserving the selection.
    void RefreshTypeFilter(List<MeshNode> all)
    {
        var wanted = new List<string> { AllTypes };
        wanted.AddRange(all.Select(n => n.Role).Where(r => !string.IsNullOrEmpty(r))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
        if (all.Any(n => string.IsNullOrEmpty(n.Role))) wanted.Add(NoType);
        if (wanted.SequenceEqual(_type.Items)) return;
        string selected = _type.SelectedItem as string ?? AllTypes;
        _rebuildingTypes = true;
        _type.Items.Clear();
        foreach (var w in wanted) _type.Items.Add(w);
        _type.SelectedIndex = Math.Max(0, wanted.IndexOf(selected));
        _rebuildingTypes = false;
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

    // Compact last-heard cell. Last-heard is stamped with the radio's own clock; if that clock is set
    // ahead of real time the stamp lands in the future — flag that with the actual date instead of a
    // confusing blank/future "ago".
    static string HeardCell(MeshNode n) =>
        n.LastHeard <= 0 ? "" :
        DateTimeOffset.FromUnixTimeSeconds(n.LastHeard) > DateTimeOffset.UtcNow.AddMinutes(1)
            ? $"{HeardAt(n.LastHeard)} (ahead)"
            : Ago(n.LastHeard);

    // The recycled row template: one cell per column, matching the header widths. Fields bind to the row's
    // NodeRowVM, so a cell scrolled into view (or rebound to a different node) just picks up that node's
    // current values.
    DataTemplate NodeTemplate() => new(() =>
    {
        var grid = new Grid { ColumnSpacing = 0, Padding = new Thickness(0, 6) };
        foreach (var c in Columns) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(c.W)));

        Label Cell(string textPath, int col, Color? color = null, bool bold = false)
        {
            var lbl = new Label
            {
                TextColor = color ?? Fg,
                FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                Padding = new Thickness(2, 0),
            };
            lbl.SetBinding(Label.TextProperty, textPath);
            lbl.SetBinding(Label.FontFamilyProperty, nameof(NodeRowVM.FontFamily));
            lbl.SetBinding(Label.FontSizeProperty, nameof(NodeRowVM.FontSize));
            grid.Add(lbl, col, 0);
            return lbl;
        }

        Cell(nameof(NodeRowVM.Marks), 0);
        var nameLbl = Cell(nameof(NodeRowVM.NameText), 1, bold: true);
        nameLbl.SetBinding(Label.TextColorProperty, nameof(NodeRowVM.NameColor));
        Cell(nameof(NodeRowVM.TypeText), 2, Dim);
        Cell(nameof(NodeRowVM.HwText), 3, Dim);
        Cell(nameof(NodeRowVM.HeardText), 4, Dim);
        Cell(nameof(NodeRowVM.SignalText), 5, Dim);
        Cell(nameof(NodeRowVM.EnvText), 6, Dim);

        var dmSwitch = new Switch { VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
        dmSwitch.SetBinding(Switch.IsToggledProperty, new Binding(nameof(NodeRowVM.Dm), BindingMode.TwoWay));
        dmSwitch.SetBinding(Switch.IsEnabledProperty, nameof(NodeRowVM.DmEnabled));
        dmSwitch.SetBinding(VisualElement.IsVisibleProperty, nameof(NodeRowVM.ShowToggles));   // own node has no toggles
        grid.Add(dmSwitch, 7, 0);

        var blkSwitch = new Switch { VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
        blkSwitch.SetBinding(Switch.IsToggledProperty, new Binding(nameof(NodeRowVM.Block), BindingMode.TwoWay));
        blkSwitch.SetBinding(VisualElement.IsVisibleProperty, nameof(NodeRowVM.ShowToggles));
        grid.Add(blkSwitch, 8, 0);

        // Long-press anywhere on the row (the switches keep their own touch handling) → the same menu as the
        // desktop right-click. The native Android LongClick reads the row's current BindingContext, so it works
        // correctly even as the cell is recycled to other nodes.
#if ANDROID
        void OnRowLongClick(object? s, Android.Views.View.LongClickEventArgs e)
        {
            e.Handled = true;
            if (grid.BindingContext is NodeRowVM vm)
                MainThread.BeginInvokeOnMainThread(() => _ = ShowNodeMenuAsync(vm.Node));
        }
        grid.HandlerChanged += (_, _) =>
        {
            if (grid.Handler?.PlatformView is Android.Views.View av)
            {
                av.LongClickable = true;
                av.LongClick -= OnRowLongClick;
                av.LongClick += OnRowLongClick;
            }
        };
#endif

        var cell = new VerticalStackLayout { Spacing = 0 };
        cell.Add(grid);
        cell.Add(new BoxView { HeightRequest = 1, Color = Rule });
        return cell;
    });

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

        string _marks = "";
        public string Marks { get => _marks; private set => Set(ref _marks, value); }

        string _nameText = "";
        public string NameText { get => _nameText; private set => Set(ref _nameText, value); }

        Color _nameColor = Fg;
        public Color NameColor { get => _nameColor; private set => Set(ref _nameColor, value); }

        string _typeText = "";
        public string TypeText { get => _typeText; private set => Set(ref _typeText, value); }

        string _hwText = "";
        public string HwText { get => _hwText; private set => Set(ref _hwText, value); }

        string _heardText = "";
        public string HeardText { get => _heardText; private set => Set(ref _heardText, value); }

        string _signalText = "";
        public string SignalText { get => _signalText; private set => Set(ref _signalText, value); }

        string _envText = "";
        public string EnvText { get => _envText; private set => Set(ref _envText, value); }

        string _fontFamily = "monospace";
        public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }

        double _fontSize = 13;
        public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }

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
        public void Update(MeshNode node, string marks, string name, Color nameColor, string type, string hw,
                           string heard, string signal, string env, bool dm, bool block)
        {
            Node = node;
            Marks = marks;
            NameText = name;
            NameColor = nameColor;
            TypeText = type;
            HwText = hw;
            HeardText = heard;
            SignalText = signal;
            EnvText = env;
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
