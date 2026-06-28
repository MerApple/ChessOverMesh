using System.Security.Cryptography;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// Channel manager (MAUI port of the desktop ChannelsWindow): shows the device's channels, creates/deletes
/// real Meshtastic channels (name + PSK), sets an optional app-level AES key per channel, toggles chat acks,
/// and picks which channel chess uses and which channels chat listens to. The caller pauses its poll loop
/// for the page's lifetime. Await <see cref="Completion"/> to get the chosen selections back.
/// </summary>
public partial class ChannelsPage : ContentPage
{
    public sealed record Result(uint ChessChannel, HashSet<uint> ChatListen, IReadOnlyList<MeshChannel> Channels);

    sealed record ChannelRow(uint Index, string Text);

    readonly MeshtasticHttpClient _mesh;
    readonly string _host;
    readonly bool _lockChess;
    IReadOnlyList<MeshChannel> _channels;
    uint _chessChannel;
    readonly HashSet<uint> _chatListen;
    bool _busy;
    bool _settingUtility;   // guards the utility-channel picker while it's repopulated
    readonly TaskCompletionSource<Result> _tcs = new();

    public Task<Result> Completion => _tcs.Task;

    public ChannelsPage(MeshtasticHttpClient mesh, string host, IReadOnlyList<MeshChannel> cachedChannels,
                        uint chessChannel, IEnumerable<uint> chatListen, bool lockChessChannel)
    {
        InitializeComponent();
        _mesh = mesh;
        _host = host;
        _lockChess = lockChessChannel;
        _channels = cachedChannels ?? Array.Empty<MeshChannel>();
        _chessChannel = chessChannel;
        _chatListen = new HashSet<uint>(chatListen);

        ChessPicker.IsEnabled = !_lockChess;
        ChessHint.Text = _lockChess
            ? "Locked while a game is in progress — finish or leave the game to change it."
            : "Chess moves, setup and acks use this one channel. The primary channel (0) can't be used — create a secondary channel. Both players must pick the same one.";

        RebuildLists();
        SetStatus("Showing cached channels — tap Fetch to refresh from the device.", false);
    }

    void RebuildLists()
    {
        var enabled = _channels.Where(c => !c.IsDisabled).OrderBy(c => c.Index).ToList();
        // Chess never runs on the primary channel (0) — chess traffic isn't allowed there — so the chess channel
        // must be a non-zero (secondary) channel. Drop an invalid/primary selection; default to the first secondary.
        var chessChannels = enabled.Where(c => c.Index != 0).ToList();
        if (chessChannels.Count > 0 && chessChannels.All(c => c.Index != _chessChannel)) _chessChannel = chessChannels[0].Index;
        _chatListen.RemoveWhere(i => enabled.All(c => c.Index != i));
        if (_chatListen.Count == 0 && enabled.Count > 0)
            _chatListen.Add(enabled.Any(c => c.Index == _chessChannel) ? _chessChannel : enabled[0].Index);

        ChannelList.ItemsSource = enabled
            .Select(c => new ChannelRow(c.Index, $"[{c.Index}] {c.DisplayName} — {c.Role}{(c.HasKey ? "  🔒" : "")}"))
            .ToList();

        var chessRows = chessChannels.Select(c => new ChannelRow(c.Index, $"[{c.Index}] {c.DisplayName}")).ToList();
        ChessPicker.ItemsSource = chessRows;
        ChessPicker.ItemDisplayBinding = new Binding(nameof(ChannelRow.Text));
        ChessPicker.SelectedItem = chessRows.FirstOrDefault(r => r.Index == _chessChannel);

        // Utility/info channel: "Automatic" (sentinel uint.MaxValue) plus every enabled channel.
        var utilRows = new List<ChannelRow> { new(uint.MaxValue, "Automatic (per-node / primary)") };
        utilRows.AddRange(enabled.Select(c => new ChannelRow(c.Index, $"[{c.Index}] {c.DisplayName}")));
        _settingUtility = true;
        UtilityPicker.ItemsSource = utilRows;
        UtilityPicker.ItemDisplayBinding = new Binding(nameof(ChannelRow.Text));
        uint savedUtil = DeviceCache.GetUtilityChannel(_host) ?? uint.MaxValue;
        var utilSel = utilRows.FirstOrDefault(r => r.Index == savedUtil) ?? utilRows[0];
        UtilityPicker.SelectedItem = utilSel;
        _settingUtility = false;
        _mesh.SetUtilityChannel(utilSel.Index == uint.MaxValue ? (uint?)null : utilSel.Index);   // apply the (possibly corrected) value live

        ChatListenPanel.Children.Clear();
        foreach (var c in enabled)
        {
            uint idx = c.Index;
            var sw = new Switch { IsToggled = _chatListen.Contains(idx), VerticalOptions = LayoutOptions.Center };
            sw.Toggled += (_, e) => { if (e.Value) _chatListen.Add(idx); else _chatListen.Remove(idx); };
            var row = new HorizontalStackLayout { Spacing = 8 };
            row.Add(sw);
            row.Add(new Label { Text = $"[{idx}] {c.DisplayName}", TextColor = Color.FromArgb("#E0E0E0"), VerticalOptions = LayoutOptions.Center });
            ChatListenPanel.Add(row);
        }
    }

    async void OnFetch(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true; SetBusy(true); SetStatus("Reading channels from the device…", false);
        try
        {
            _channels = await _mesh.GetDeviceChannelsAsync();
            RebuildLists();
            DeviceCache.Save(_host, _channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
            SetStatus($"Fetched {_channels.Count(c => !c.IsDisabled)} channel(s) from the device (cached).", false);
        }
        catch (Exception ex) { SetStatus($"Could not read channels: {ex.Message}", true); }
        finally { _busy = false; SetBusy(false); }
    }

    void OnChannelSelected(object? sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    void UpdateSelectionState()
    {
        bool has = ChannelList.SelectedItem is ChannelRow;
        AppKeyBox.IsEnabled = has;
        SetKeyBtn.IsEnabled = has && !_busy;
        AckSwitch.IsEnabled = has && !_busy;
        TriggerBox.IsEnabled = SetTriggerBtn.IsEnabled = has && !_busy;
        DeleteBtn.IsEnabled = !_busy && ChannelList.SelectedItem is ChannelRow { Index: > 0 };

        UplinkSwitch.IsEnabled = DownlinkSwitch.IsEnabled = PositionSwitch.IsEnabled = has && !_busy;
        SaveOptionsBtn.IsEnabled = has && !_busy;

        if (ChannelList.SelectedItem is ChannelRow row)
        {
            AppKeyBox.Text = DeviceCache.GetChannelKey(_host, row.Index);
            TriggerBox.Text = string.Join(", ", DeviceCache.GetAckTriggers(_host).GetValueOrDefault(row.Index) ?? new List<string>());
            AckSwitch.IsToggled = DeviceCache.IsChatAckEnabled(_host, row.Index);
            AckSignalSwitch.IsToggled = DeviceCache.IsAckSignalEnabled(_host, row.Index);
            AckSignalSwitch.IsEnabled = has && !_busy && AckSwitch.IsToggled;   // only relevant when acks are on
            var ch = _channels.FirstOrDefault(c => c.Index == row.Index);
            PskShow.Text = ch.Psk.Length > 0 ? ch.Psk
                         : ch.HasKey ? "🔒 set — tap Fetch to read it from the device"
                         : "(open channel — no PSK)";
            // Uplink/downlink are read straight from the device (Fetch to refresh); position too when the device
            // reports it (PositionPrecision >= 0), else off.
            UplinkSwitch.IsToggled = ch.UplinkEnabled;
            DownlinkSwitch.IsToggled = ch.DownlinkEnabled;
            PositionSwitch.IsToggled = ch.PositionPrecision > 0;
        }
        else
        {
            AppKeyBox.Text = ""; TriggerBox.Text = ""; PskShow.Text = ""; AckSignalSwitch.IsToggled = false; AckSignalSwitch.IsEnabled = false;
            UplinkSwitch.IsToggled = DownlinkSwitch.IsToggled = PositionSwitch.IsToggled = false;
        }
    }

    void OnSetTrigger(object? sender, EventArgs e)
    {
        if (ChannelList.SelectedItem is not ChannelRow row) return;
        var parts = (TriggerBox.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DeviceCache.SetAckTriggers(_host, row.Index, parts);
        SetStatus(parts.Length > 0
            ? $"Auto-ack keywords set for channel [{row.Index}] — messages containing them get an RSSI ack."
            : $"Auto-ack keywords cleared for channel [{row.Index}].", false);
    }

    void OnSetKey(object? sender, EventArgs e)
    {
        if (ChannelList.SelectedItem is not ChannelRow row) return;
        DeviceCache.SetChannelKey(_host, row.Index, AppKeyBox.Text ?? "");
        _mesh.SetChannelKey(row.Index, AppKeyBox.Text ?? "");
        SetStatus((AppKeyBox.Text ?? "").Length > 0 ? $"App key set for channel [{row.Index}]." : $"App key cleared for channel [{row.Index}].", false);
    }

    void OnAckToggled(object? sender, ToggledEventArgs e)
    {
        if (ChannelList.SelectedItem is not ChannelRow row) return;
        DeviceCache.SetChatAck(_host, row.Index, e.Value);
        AckSignalSwitch.IsEnabled = e.Value && !_busy;   // signal-in-ack only relevant when acks are on
        SetStatus($"Chat acks {(e.Value ? "on" : "off")} for channel [{row.Index}].", false);
    }

    void OnAckSignalToggled(object? sender, ToggledEventArgs e)
    {
        if (ChannelList.SelectedItem is not ChannelRow row) return;
        DeviceCache.SetAckSignal(_host, row.Index, e.Value);
        SetStatus($"Ack signal report {(e.Value ? "on" : "off")} for channel [{row.Index}].", false);
    }

    void OnUtilityChanged(object? sender, EventArgs e)
    {
        if (_settingUtility || UtilityPicker.SelectedItem is not ChannelRow row) return;
        uint? ch = row.Index == uint.MaxValue ? (uint?)null : row.Index;
        DeviceCache.SetUtilityChannel(_host, ch);
        _mesh.SetUtilityChannel(ch);
        SetStatus(ch == null
            ? "Info & position requests/broadcasts: automatic (per-node channel, primary for broadcasts)."
            : $"Info & position requests/broadcasts will use channel [{ch}].", false);
    }

    void OnRandomPsk(object? sender, EventArgs e) => PskBox.Text = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    async void OnCreate(object? sender, EventArgs e)
    {
        if (_busy) return;
        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) { SetStatus("Enter a channel name first.", true); return; }
        _busy = true; SetBusy(true); SetStatus($"Creating channel '{name}'…", false);
        try
        {
            var result = await _mesh.AddOrUpdateChannelAsync(null, name, PskBox.Text ?? "");
            if (!result.Ok) { SetStatus(result.Error ?? "Create failed.", true); return; }
            NameBox.Text = ""; PskBox.Text = "";
            _channels = await _mesh.GetDeviceChannelsAsync();
            RebuildLists();
            DeviceCache.Save(_host, _channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
            SetStatus($"Created channel '{name}' (index {result.Index}).", false);
        }
        catch (Exception ex) { SetStatus($"Create failed: {ex.Message}", true); }
        finally { _busy = false; SetBusy(false); }
    }

    async void OnSaveOptions(object? sender, EventArgs e)
    {
        if (_busy || ChannelList.SelectedItem is not ChannelRow row) return;
        var ch = _channels.FirstOrDefault(c => c.Index == row.Index);
        bool up = UplinkSwitch.IsToggled, down = DownlinkSwitch.IsToggled, pos = PositionSwitch.IsToggled;
        _busy = true; SetBusy(true); SetStatus($"Saving options for channel [{row.Index}]…", false);
        try
        {
            // null PSK = keep the device's existing key (SetChannelAsync loads it); name preserved.
            var result = await _mesh.SetChannelAsync(row.Index, ch.Name ?? "", null, up, down, pos);
            if (!result.Ok) { SetStatus(result.Error ?? "Save failed.", true); return; }
            // Reflect locally rather than re-reading: a config re-read right after the commit can come back
            // incomplete until the radio settles (the desktop app hit this), so apply the values we just set.
            _channels = _channels
                .Select(c => c.Index == row.Index ? c with { UplinkEnabled = up, DownlinkEnabled = down, PositionPrecision = pos ? 32 : 0 } : c)
                .ToList();
            RebuildLists();
            ChannelList.SelectedItem = (ChannelList.ItemsSource as IEnumerable<ChannelRow>)?.FirstOrDefault(r => r.Index == row.Index);
            DeviceCache.Save(_host, _channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
            SetStatus($"Saved options for channel [{row.Index}].", false);
        }
        catch (Exception ex) { SetStatus($"Save failed: {ex.Message}", true); }
        finally { _busy = false; SetBusy(false); }
    }

    async void OnDelete(object? sender, EventArgs e)
    {
        if (_busy || ChannelList.SelectedItem is not ChannelRow row) return;
        if (row.Index == 0) { SetStatus("The primary channel (0) cannot be deleted.", true); return; }
        if (!await DisplayAlert("Delete channel", $"Disable channel [{row.Index}] on the device?", "Yes", "No")) return;
        _busy = true; SetBusy(true); SetStatus($"Disabling channel [{row.Index}]…", false);
        try
        {
            var result = await _mesh.DisableChannelAsync(row.Index);
            if (!result.Ok) { SetStatus(result.Error ?? "Delete failed.", true); return; }
            _channels = await _mesh.GetDeviceChannelsAsync();
            RebuildLists();
            DeviceCache.Save(_host, _channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
            SetStatus($"Channel [{row.Index}] disabled.", false);
        }
        catch (Exception ex) { SetStatus($"Delete failed: {ex.Message}", true); }
        finally { _busy = false; SetBusy(false); }
    }

    async void OnDone(object? sender, EventArgs e)
    {
        if (ChessPicker.SelectedItem is ChannelRow row) _chessChannel = row.Index;
        if (_chatListen.Count == 0) _chatListen.Add(_chessChannel);
        _tcs.TrySetResult(new Result(_chessChannel, _chatListen, _channels));
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        // Hardware/back nav also commits the current selections.
        if (ChessPicker.SelectedItem is ChannelRow row) _chessChannel = row.Index;
        if (_chatListen.Count == 0) _chatListen.Add(_chessChannel);
        _tcs.TrySetResult(new Result(_chessChannel, _chatListen, _channels));
        return base.OnBackButtonPressed();
    }

    void SetBusy(bool busy)
    {
        FetchBtn.IsEnabled = !busy;
        CreateBtn.IsEnabled = !busy;
        UpdateSelectionState();
    }

    void SetStatus(string text, bool warn)
    {
        StatusLabel.Text = text;
        StatusLabel.TextColor = warn ? Color.FromArgb("#E69A4C") : Color.FromArgb("#B0B0B0");
    }
}
