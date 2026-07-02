using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Gui;

/// <summary>
/// Modal channel manager: shows the device's channels (cached, with a Fetch to refresh from the radio),
/// creates/deletes real Meshtastic channels (name + PSK), sets an optional app-level AES key per channel,
/// and lets the user pick which channel chess uses and which channels chat listens to. The caller must
/// pause its /fromradio poll loop for the lifetime of this dialog — the admin/fetch ops drain the radio
/// queue themselves.
/// </summary>
internal sealed class ChannelsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE6, 0x9A, 0x4C));

    private readonly MeshtasticHttpClient _mesh;
    private readonly string _host;
    private readonly bool _lockChess;   // true while a game is in progress — don't allow moving the chess channel
    private IReadOnlyList<MeshChannel> _channels;

    private readonly ListBox _list = new() { Background = Panel, Foreground = Fg, BorderThickness = new Thickness(0), Height = 140 };
    private readonly TextBox _nameBox = new() { MinHeight = 24, MinWidth = 110 };
    private readonly TextBox _pskBox = new() { MinHeight = 24, MinWidth = 140 };
    private readonly PasswordBox _appKeyBox = new() { MinHeight = 24, MinWidth = 160, IsEnabled = false };   // masked — it's a secret
    private readonly TextBox _triggerBox = new() { MinHeight = 24, MinWidth = 230, IsEnabled = false };
    private readonly TextBox _pskShow = new() { MinHeight = 24, MinWidth = 250, IsReadOnly = true, Foreground = Dim, Background = Panel, BorderThickness = new Thickness(0) };
    private readonly ComboBox _chessCombo = new() { MinHeight = 24, MinWidth = 220 };
    private readonly ComboBox _utilityCombo = new() { MinHeight = 24, MinWidth = 220 };
    private readonly TextBlock _status = new() { Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    // Shown next to the Update button while no fetch has happened this session — explains why Update is disabled.
    private readonly TextBlock _updateHint = new() { Foreground = Warn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Text = "← Fetch from device to enable Update" };
    private readonly CheckBox _ackCheck = new() { Content = "Send chat acks", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), IsEnabled = false };
    private readonly CheckBox _ackSignalCheck = new() { Content = "…with RSSI/SNR/hops", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), IsEnabled = false };
    private readonly CheckBox _uplinkCheck = new() { Content = "Uplink", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, ToolTip = "Send this channel's messages to the MQTT server (uplink)." };
    private readonly CheckBox _downlinkCheck = new() { Content = "Downlink", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, ToolTip = "Receive this channel's messages from the MQTT server (downlink)." };
    private readonly CheckBox _positionCheck = new() { Content = "Share position", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, ToolTip = "Allow position to be shared on this channel. The current value can't be read back, so it's applied exactly as set when you Update/Create the channel." };
    private readonly Button _fetchBtn;
    private readonly Button _createBtn;
    private readonly Button _updateBtn;
    private readonly Button _deleteBtn;
    private readonly Button _setKeyBtn;
    private readonly Button _setTriggerBtn;
    private readonly Button _deleteChatBtn;
    private readonly Action<uint>? _onClearChat;   // notifies the main window to drop a channel's live chat rows
    private bool _busy;
    private bool _settingUtility;  // guards the utility-channel combo while it's repopulated, so selection-changed doesn't fire
    private bool _fetched;         // true once the user has fetched live channels this session — required before Update (so the PSK is correct)
    private uint _selectedIndex;   // remembered selected channel, so a refresh re-selects it (keeps the option checkboxes populated)

    /// <summary>Channel index chess should use (the primary, 0, by default).</summary>
    public uint ChessChannel { get; private set; }

    /// <summary>The device channel set as it stood when the dialog closed.</summary>
    public IReadOnlyList<MeshChannel> Channels => _channels;

    public ChannelsWindow(Window owner, MeshtasticHttpClient mesh, string host,
                          IReadOnlyList<MeshChannel> cachedChannels, uint chessChannel,
                          bool lockChessChannel = false, Action<uint>? onClearChat = null)
    {
        _mesh = mesh;
        _host = host;
        _lockChess = lockChessChannel;
        _onClearChat = onClearChat;
        _channels = cachedChannels ?? Array.Empty<MeshChannel>();
        ChessChannel = chessChannel;

        Title = "Channels";
        Owner = owner;
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        _fetchBtn = new Button { Content = "Fetch from device", MinHeight = 24, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Right };
        _createBtn = new Button { Content = "Create channel", MinWidth = 120, MinHeight = 24,
            ToolTip = "Create a NEW channel in the first free slot from the name, PSK and options below." };
        _updateBtn = new Button { Content = "Update channel", MinWidth = 120, MinHeight = 24, Margin = new Thickness(6, 0, 0, 0), IsEnabled = false,
            ToolTip = "Write the name, PSK and options to the selected channel. Requires a Fetch from device first, so the channel's PSK is read correctly before it's rewritten." };
        _deleteBtn = new Button { Content = "Delete selected channel", MinWidth = 160, MinHeight = 24,
            ToolTip = "Delete the channel selected in the list above (the primary channel 0 can't be deleted)." };
        _setKeyBtn = new Button { Content = "Set key", MinWidth = 70, MinHeight = 24, Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        _setTriggerBtn = new Button { Content = "Set", MinWidth = 50, MinHeight = 24, Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        _deleteChatBtn = new Button { Content = "Delete chat", MinWidth = 90, MinHeight = 24, Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        _fetchBtn.Click += Fetch_Click;
        _createBtn.Click += Create_Click;
        _updateBtn.Click += Update_Click;
        _deleteBtn.Click += Delete_Click;
        _setKeyBtn.Click += SetKey_Click;
        _setTriggerBtn.Click += SetTrigger_Click;
        _deleteChatBtn.Click += DeleteChat_Click;
        _ackCheck.Click += AckCheck_Click;
        _ackCheck.ToolTip = "Send acknowledgements for chat messages received on this channel (chess always acks).";
        _ackSignalCheck.Click += AckSignalCheck_Click;
        _ackSignalCheck.ToolTip = "When acking, also report the RSSI/SNR/hop count this device received the message at (only used when acks are on).";
        _list.SelectionChanged += (_, _) => OnSelectionChanged();

        Content = BuildLayout();
        // Load from cache on open (no device read) — name, role, uplink/downlink/position are all cached, so the
        // list and option checkboxes populate instantly and offline. PSK values aren't cached: keyed channels show
        // "🔒 set" until the user clicks "Fetch from device" to read live values (uplink/downlink and the keys).
        Loaded += (_, _) =>
        {
            RebuildLists();
            Status("Showing cached channels — click \"Fetch from device\" for live values (uplink/downlink and PSKs).", false);
        };
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(12) };

        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var hdr = Header("Device channels");
        hdr.Margin = new Thickness(0);
        DockPanel.SetDock(_fetchBtn, Dock.Right);
        headerRow.Children.Add(_fetchBtn);
        headerRow.Children.Add(hdr);
        root.Children.Add(headerRow);
        root.Children.Add(_list);

        // Delete acts on the channel selected in the list above — keep it next to the list, not the create fields.
        var deleteRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        deleteRow.Children.Add(_deleteBtn);
        root.Children.Add(deleteRow);

        // Device PSK of the selected channel (read-only, base64)
        var pskShowRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        pskShowRow.Children.Add(Label("PSK (selected):"));
        _pskShow.ToolTip = "The selected channel's radio PSK, base64-encoded (read-only). Share it with the other player so both radios match.";
        pskShowRow.Children.Add(_pskShow);
        root.Children.Add(pskShowRow);

        // App-level AES key for the selected channel
        var keyRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        keyRow.Children.Add(Label("App key (selected):"));
        _appKeyBox.ToolTip = "Optional app-level AES passphrase layered on top of the channel PSK. Saved on this PC per device/channel; both players must match.";
        keyRow.Children.Add(_appKeyBox);
        keyRow.Children.Add(_setKeyBtn);
        keyRow.Children.Add(_ackCheck);
        keyRow.Children.Add(_ackSignalCheck);
        root.Children.Add(keyRow);

        // Auto-ack keywords: a received message containing any of these (case-insensitive) gets an RSSI ack even
        // when "Send chat acks" is off for the channel — handy for range-test pings.
        var triggerRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        triggerRow.Children.Add(Label("Auto-ack keywords (RSSI):"));
        _triggerBox.ToolTip = "Comma-separated, case-insensitive. A received message gets an RSSI/SNR/hops ack even if 'Send chat acks' is off for this channel. A plain word (ping) matches anywhere in the message; wrap it in double quotes (\"ping\") to match only when the whole message is exactly that word. Leave empty to disable.";
        triggerRow.Children.Add(_triggerBox);
        triggerRow.Children.Add(_setTriggerBtn);
        root.Children.Add(triggerRow);
        root.Children.Add(new TextBlock { Text = "e.g. ping, test — matches any message containing the word. Quote it (\"ping\") to require an exact whole-message match.", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) });

        // Create / Update channel: writes name + PSK + uplink/downlink/position in one request — Create makes a
        // new channel, Update rewrites the selected one (which needs a Fetch first so its PSK is correct).
        root.Children.Add(Header("Create / Update channel"));
        root.Children.Add(new TextBlock
        {
            Text = "Create makes a new channel from the fields below. To Update the selected channel, click \"Fetch from device\" first so its PSK is read correctly before it's rewritten.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        var nameRow = new WrapPanel();
        nameRow.Children.Add(Label("Name:"));
        nameRow.Children.Add(_nameBox);
        nameRow.Children.Add(Label("PSK:"));
        _pskBox.ToolTip = "Channel PSK. A base64 key (e.g. from Random) is used as-is and matches 'PSK (selected)'; " +
            "any other text is treated as a passphrase and hashed to a 256-bit key. Leave empty for an open channel " +
            "(when updating a channel whose key couldn't be read back, an empty box keeps the existing key — type 'none' to clear it).";
        nameRow.Children.Add(_pskBox);
        var randomBtn = new Button { Content = "Random", MinWidth = 64, MinHeight = 24, Margin = new Thickness(6, 0, 0, 0) };
        randomBtn.Click += (_, _) => _pskBox.Text = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        nameRow.Children.Add(randomBtn);
        root.Children.Add(nameRow);

        var optsRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        optsRow.Children.Add(_uplinkCheck);
        optsRow.Children.Add(_downlinkCheck);
        optsRow.Children.Add(_positionCheck);
        root.Children.Add(optsRow);

        var actionRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        actionRow.Children.Add(_createBtn);
        actionRow.Children.Add(_updateBtn);
        actionRow.Children.Add(_updateHint);
        root.Children.Add(actionRow);

        // Cached chat row: delete the saved chat history for the selected channel.
        var chatRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        chatRow.Children.Add(Label("Cached chat (selected):"));
        _deleteChatBtn.ToolTip = "Delete the saved chat history for the selected channel on this PC.";
        chatRow.Children.Add(_deleteChatBtn);
        root.Children.Add(chatRow);

        root.Children.Add(Divider());

        // Chess channel
        root.Children.Add(Header("Chess channel"));
        root.Children.Add(new TextBlock
        {
            Text = _lockChess
                ? "Locked while a game is in progress — finish or leave the game to change the chess channel."
                : "Chess moves, game setup and acks use this one channel. The primary channel (0) can't be used — create a secondary channel. Both players must pick the same one.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        _chessCombo.IsEnabled = !_lockChess;
        root.Children.Add(_chessCombo);

        // Utility / info channel: which channel position/telemetry/node-info requests and the manual
        // node-info/position broadcasts go out on.
        root.Children.Add(Header("Info & position channel"));
        root.Children.Add(new TextBlock
        {
            Text = "The channel used for position requests, telemetry requests, and the manual \"Broadcast node info\" / " +
                   "\"Broadcast position\" actions. \"Automatic\" follows each node's own channel for requests and the " +
                   "primary channel (0) for broadcasts — the safest choice for reaching the most nodes.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        _utilityCombo.SelectionChanged += UtilityCombo_SelectionChanged;
        root.Children.Add(_utilityCombo);

        root.Children.Add(_status);

        var closeBtn = new Button { Content = "Done", MinWidth = 80, MinHeight = 26, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        closeBtn.Click += Done_Click;
        root.Children.Add(closeBtn);

        return root;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
    };

    private static Border Divider() => new()
    {
        Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)), Margin = new Thickness(0, 12, 0, 0),
    };

    private sealed record ChannelRow(uint Index, string Text);

    private async void Fetch_Click(object sender, RoutedEventArgs e) => await FetchFromDeviceAsync();

    private async Task FetchFromDeviceAsync()
    {
        if (_busy) return;
        _busy = true; SetButtons(false); Status("Reading channels from the device…", false);
        try
        {
            var fetched = await _mesh.GetDeviceChannelsAsync();
            // The device can be reachable yet not answer the config request — the read then "succeeds" with only
            // a synthetic primary channel. Treat that as a failure: keep the cached list and don't enable Update.
            if (!_mesh.LastChannelReadOk)
            {
                Status("Could not read channels from the device — it didn't respond to the config request. " +
                       "Showing cached values; check the connection and try Fetch again.", true);
                return;
            }
            _channels = fetched;
            _fetched = true;   // PSKs are now read from the device — Update is safe
            RebuildLists();
            DeviceCache.Save(_host, _channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);   // cache per device
            Status($"Fetched {_channels.Count(c => !c.IsDisabled)} channel(s) from the device.", false);
        }
        catch (Exception ex)
        {
            Status($"Could not read channels from the device — showing cached values. ({ex.Message})", true);
        }
        finally { _busy = false; SetButtons(true); }
    }

    private void RebuildLists()
    {
        var enabled = _channels.Where(c => !c.IsDisabled).OrderBy(c => c.Index).ToList();

        // Chess never runs on the primary channel (0) — chess traffic isn't allowed there — so the chess channel
        // must be a non-zero (secondary) channel. Drop an invalid/primary selection; default to the first secondary.
        var chessChannels = enabled.Where(c => c.Index != 0).ToList();
        if (chessChannels.Count > 0 && chessChannels.All(c => c.Index != ChessChannel)) ChessChannel = chessChannels[0].Index;

        var rows = enabled
            .Select(c => new ChannelRow(c.Index, $"[{c.Index}] {c.DisplayName} — {c.Role}{(c.HasKey ? "  🔒" : "")}"))
            .ToList();
        _list.ItemsSource = rows;
        _list.DisplayMemberPath = nameof(ChannelRow.Text);
        // Restore the previously selected channel (or default to the first) so the option checkboxes stay
        // populated after a refresh — setting ItemsSource otherwise clears the selection.
        _list.SelectedItem = rows.FirstOrDefault(r => r.Index == _selectedIndex) ?? rows.FirstOrDefault();
        OnSelectionChanged();

        _chessCombo.ItemsSource = chessChannels.Select(c => new ChannelRow(c.Index, $"[{c.Index}] {c.DisplayName}")).ToList();
        _chessCombo.DisplayMemberPath = nameof(ChannelRow.Text);
        _chessCombo.SelectedItem = (_chessCombo.ItemsSource as IEnumerable<ChannelRow>)?.FirstOrDefault(r => r.Index == ChessChannel);

        // Utility/info channel: "Automatic" (sentinel uint.MaxValue) plus every enabled channel.
        var utilRows = new List<ChannelRow> { new(uint.MaxValue, "Automatic (per-node / primary)") };
        utilRows.AddRange(enabled.Select(c => new ChannelRow(c.Index, $"[{c.Index}] {c.DisplayName}")));
        _settingUtility = true;
        _utilityCombo.ItemsSource = utilRows;
        _utilityCombo.DisplayMemberPath = nameof(ChannelRow.Text);
        uint savedUtil = DeviceCache.GetUtilityChannel(_host) ?? uint.MaxValue;
        var utilSel = utilRows.FirstOrDefault(r => r.Index == savedUtil) ?? utilRows[0];
        _utilityCombo.SelectedItem = utilSel;
        _settingUtility = false;
        _mesh.SetUtilityChannel(utilSel.Index == uint.MaxValue ? (uint?)null : utilSel.Index);   // apply the (possibly corrected) value live
    }

    // Load the selected channel's saved app-level key and show its device PSK.
    private void OnSelectionChanged()
    {
        bool has = _list.SelectedItem is ChannelRow;
        if (_list.SelectedItem is ChannelRow selected) _selectedIndex = selected.Index;   // remember across refreshes
        _appKeyBox.IsEnabled = has;
        _setKeyBtn.IsEnabled = has && !_busy;
        _ackCheck.IsEnabled = has && !_busy;
        _deleteBtn.IsEnabled = !_busy && _list.SelectedItem is ChannelRow { Index: > 0 };
        _appKeyBox.Password = _list.SelectedItem is ChannelRow keyRow ? DeviceCache.GetChannelKey(_host, keyRow.Index) : "";
        _triggerBox.IsEnabled = _setTriggerBtn.IsEnabled = has && !_busy;
        _triggerBox.Text = _list.SelectedItem is ChannelRow trigRow
            ? string.Join(", ", DeviceCache.GetAckTriggers(_host).GetValueOrDefault(trigRow.Index) ?? new List<string>())
            : "";
        _deleteChatBtn.IsEnabled = has && !_busy;
        // The Create/Update fields are always editable (you set them whether creating or updating) — only gated on busy.
        _nameBox.IsEnabled = _pskBox.IsEnabled = !_busy;
        _uplinkCheck.IsEnabled = _downlinkCheck.IsEnabled = _positionCheck.IsEnabled = !_busy;
        // Update requires a selected channel AND a fetch this session (so its PSK is read correctly before rewrite).
        _updateBtn.IsEnabled = has && _fetched && !_busy;
        // Tell the user why Update is unavailable: it stays disabled until they Fetch live channels this session.
        _updateHint.Visibility = _fetched ? Visibility.Collapsed : Visibility.Visible;
        if (_list.SelectedItem is ChannelRow optRow)
        {
            var ch = _channels.FirstOrDefault(c => c.Index == optRow.Index);
            var opts = DeviceCache.GetChannelOptions(_host).GetValueOrDefault(optRow.Index);
            // Populate the editable fields from the selected channel so it can be edited and re-written.
            _nameBox.Text = ch.Name;
            _pskBox.Text = ch.Psk;   // base64 when known; blank for an open channel or a key not yet fetched
            // Uplink/downlink are read straight from the device (Fetch to refresh them).
            _uplinkCheck.IsChecked = ch.UplinkEnabled;
            _downlinkCheck.IsChecked = ch.DownlinkEnabled;
            // Position is now read from the device too (parsed from module_settings) when it reports it
            // (PositionPrecision >= 0); otherwise fall back to the cached last-set value.
            _positionCheck.IsChecked = ch.PositionPrecision >= 0 ? ch.PositionPrecision > 0 : (opts?.Position ?? false);
        }
        else   // nothing selected → fields cleared for creating a new channel
        {
            _nameBox.Clear();
            _pskBox.Clear();
            _uplinkCheck.IsChecked = _downlinkCheck.IsChecked = _positionCheck.IsChecked = false;
        }
        _ackCheck.IsChecked = _list.SelectedItem is ChannelRow ackRow && DeviceCache.IsChatAckEnabled(_host, ackRow.Index);
        // Signal-in-ack only applies when acks are on for the channel.
        _ackSignalCheck.IsChecked = _list.SelectedItem is ChannelRow sigRow && DeviceCache.IsAckSignalEnabled(_host, sigRow.Index);
        _ackSignalCheck.IsEnabled = has && !_busy && _ackCheck.IsChecked == true;

        if (_list.SelectedItem is ChannelRow row)
        {
            var ch = _channels.FirstOrDefault(c => c.Index == row.Index);
            _pskShow.Text = ch.Psk.Length > 0 ? ch.Psk
                          : ch.HasKey ? "🔒 set — click Fetch to read it from the device"
                          : "(open channel — no PSK)";
        }
        else
            _pskShow.Text = "";
    }

    private void SetKey_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not ChannelRow row) return;
        DeviceCache.SetChannelKey(_host, row.Index, _appKeyBox.Password);   // persisted (DPAPI), "" clears it
        _mesh.SetChannelKey(row.Index, _appKeyBox.Password);                // apply immediately
        Status(_appKeyBox.Password.Length > 0
            ? $"App key set for channel [{row.Index}]."
            : $"App key cleared for channel [{row.Index}].", false);
    }

    // Save the selected channel's auto-ack keywords (takes effect on close — MainWindow reloads them).
    private void SetTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not ChannelRow row) return;
        var parts = _triggerBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DeviceCache.SetAckTriggers(_host, row.Index, parts);
        Status(parts.Length > 0
            ? $"Auto-ack keywords set for channel [{row.Index}] — messages containing them get an RSSI ack."
            : $"Auto-ack keywords cleared for channel [{row.Index}].", false);
    }

    private void AckCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not ChannelRow row) return;
        bool on = _ackCheck.IsChecked == true;
        DeviceCache.SetChatAck(_host, row.Index, on);   // takes effect on close (MainWindow reloads it)
        _ackSignalCheck.IsEnabled = on && !_busy;        // signal-in-ack only relevant when acks are on
        Status($"Chat acks {(on ? "on" : "off")} for channel [{row.Index}].", false);
    }

    private void AckSignalCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not ChannelRow row) return;
        bool on = _ackSignalCheck.IsChecked == true;
        DeviceCache.SetAckSignal(_host, row.Index, on);   // takes effect on close (MainWindow reloads it)
        Status($"Ack signal report {(on ? "on" : "off")} for channel [{row.Index}].", false);
    }

    // Pick the utility/info channel — persisted per device and applied to the live client immediately.
    private void UtilityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingUtility || _utilityCombo.SelectedItem is not ChannelRow row) return;
        uint? ch = row.Index == uint.MaxValue ? (uint?)null : row.Index;
        DeviceCache.SetUtilityChannel(_host, ch);
        _mesh.SetUtilityChannel(ch);
        Status(ch == null
            ? "Info & position requests/broadcasts: automatic (per-node channel, primary for broadcasts)."
            : $"Info & position requests/broadcasts will use channel [{ch}].", false);
    }

    // Create a NEW channel (first free slot) from the current fields.
    private async void Create_Click(object sender, RoutedEventArgs e) => await WriteChannelAsync(null);

    // Update the SELECTED channel from the current fields. Guarded so it only runs after a Fetch (so the PSK is
    // read correctly before the channel is rewritten) — the button is also disabled until then.
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not ChannelRow row) { Status("Select a channel to update.", true); return; }
        if (!_fetched) { Status("Click \"Fetch from device\" first so the channel's PSK is read correctly before updating.", true); return; }
        await WriteChannelAsync(row.Index);
    }

    // Writes name + PSK + uplink/downlink/position in one request: to the given channel (update), or to a new
    // free slot when <paramref name="target"/> is null (create).
    private async Task WriteChannelAsync(uint? target)
    {
        if (_busy) return;
        string name = _nameBox.Text.Trim();
        if (name.Length == 0) { Status("Enter a channel name first.", true); return; }

        bool isUpdate = target.HasValue;
        bool uplink = _uplinkCheck.IsChecked == true;
        bool downlink = _downlinkCheck.IsChecked == true;
        bool position = _positionCheck.IsChecked == true;

        // When updating a channel whose key couldn't be read back (HasKey but no base64), an empty PSK box means
        // "keep the existing key" (null), not "make it open". Type "none" to actually clear it.
        var current = target.HasValue ? _channels.FirstOrDefault(c => c.Index == target.Value) : default;
        string? psk = isUpdate && _pskBox.Text.Trim().Length == 0 && current.HasKey && current.Psk.Length == 0
            ? null : _pskBox.Text;

        _busy = true; SetButtons(false);
        Status($"{(isUpdate ? "Updating" : "Creating")} channel '{name}'…", false);
        try
        {
            var result = await _mesh.SetChannelAsync(target, name, psk, uplink, downlink, position);
            if (!result.Ok) { Status(result.Error ?? "Save failed.", true); return; }
            uint idx = result.Index;

            // Cache the options we set (position especially, which can't be read back).
            DeviceCache.SaveChannelOption(_host, idx, new DeviceCache.ChannelOptions { Uplink = uplink, Downlink = downlink, Position = position });

            // Reflect the change locally rather than re-reading the device: a config re-read right after the
            // commit often comes back incomplete (only channel 0) until the radio settles, which would make the
            // other channels vanish until a manual Fetch. We know exactly what we wrote, so apply it.
            static bool LooksLikeRawKey(string s)
            {
                try { return Convert.FromBase64String(s.Trim()).Length is 16 or 32; } catch { return false; }
            }
            bool keyCleared = _pskBox.Text.Trim().Length == 0 || _pskBox.Text.Trim() == "none";
            bool keepingKey = psk == null;   // updating a channel whose key we left untouched
            bool hasKey = keepingKey ? current.HasKey : !keyCleared;
            string pskShown = keepingKey ? current.Psk : (!keyCleared && LooksLikeRawKey(_pskBox.Text) ? _pskBox.Text.Trim() : "");
            string role = isUpdate
                ? (string.IsNullOrEmpty(current.Role) ? "Secondary" : current.Role)
                : "Secondary";
            var patched = new MeshChannel(idx, name, role, hasKey, pskShown, uplink, downlink, position ? 32 : 0);
            _channels = _channels.Where(c => c.Index != idx).Append(patched).OrderBy(c => c.Index).ToList();

            _selectedIndex = idx;   // keep focus on the channel we just wrote
            RebuildLists();
            DeviceCache.Save(_host, _channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
            Status(isUpdate ? $"Updated channel [{idx}]." : $"Created channel '{name}' (index {idx}).", false);
        }
        catch (Exception ex) { Status($"Save failed: {ex.Message}", true); }
        finally { _busy = false; SetButtons(true); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _list.SelectedItem is not ChannelRow row) return;
        if (row.Index == 0) { Status("The primary channel (0) cannot be deleted.", true); return; }
        if (!ThemedDialog.Confirm(this, $"Disable channel [{row.Index}] on the device?", "Delete channel", defaultYes: true))
            return;

        _busy = true; SetButtons(false); Status($"Disabling channel [{row.Index}]…", false);
        try
        {
            var result = await _mesh.DisableChannelAsync(row.Index);
            if (!result.Ok) { Status(result.Error ?? "Delete failed.", true); return; }
            var fetched = await _mesh.GetDeviceChannelsAsync();
            // If the post-disable re-read didn't respond, don't clobber the list with the synthetic result —
            // reflect the disable locally so the channel still drops out of the list.
            _channels = _mesh.LastChannelReadOk ? fetched : _channels.Where(c => c.Index != row.Index).ToList();
            RebuildLists();
            DeviceCache.Save(_host, _channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
            Status($"Channel [{row.Index}] disabled.", false);
        }
        catch (Exception ex) { Status($"Delete failed: {ex.Message}", true); }
        finally { _busy = false; SetButtons(true); }
    }

    private void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not ChannelRow row) return;
        int count = DeviceCache.GetChat(_host).TryGetValue(row.Index, out var list) ? list.Count : 0;
        // Confirm even when nothing is cached — the live chat window may still be showing messages for this
        // channel (received this session) that the user wants cleared too.
        string prompt = count > 0
            ? $"Delete {count} cached chat message(s) for channel [{row.Index}] and clear it from the chat window?"
            : $"Clear channel [{row.Index}]'s chat from the chat window?";
        if (!ThemedDialog.Confirm(this, prompt, "Delete chat", defaultYes: true))
            return;
        DeviceCache.ClearChat(_host, row.Index);   // wipe the cached history
        _onClearChat?.Invoke(row.Index);           // and drop the channel's rows from the live chat view
        Status($"Deleted chat for channel [{row.Index}].", false);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_chessCombo.SelectedItem is ChannelRow row) ChessChannel = row.Index;
        DialogResult = true;
    }

    private void SetButtons(bool enabled)
    {
        _fetchBtn.IsEnabled = enabled;
        _createBtn.IsEnabled = enabled;
        OnSelectionChanged();   // also (re)computes the Update button: needs a selection + a fetch this session
    }

    private void Status(string text, bool warn)
    {
        _status.Text = text;
        _status.Foreground = warn ? Warn : Dim;
    }
}
