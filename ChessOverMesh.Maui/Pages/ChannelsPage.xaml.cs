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
    public sealed record Result(uint ChessChannel, IReadOnlyList<MeshChannel> Channels);

    sealed record ChannelRow(uint Index, string Text);

    readonly MeshtasticHttpClient _mesh;
    readonly string _host;
    readonly bool _lockChess;
    IReadOnlyList<MeshChannel> _channels;
    uint _chessChannel;
    bool _busy;
    bool _fetched;          // true once channels were read live this session — required before Update (so the PSK is correct)
    bool _settingUtility;   // guards the utility-channel picker while it's repopulated
    readonly TaskCompletionSource<Result> _tcs = new();

    public Task<Result> Completion => _tcs.Task;

    public ChannelsPage(MeshtasticHttpClient mesh, string host, IReadOnlyList<MeshChannel> cachedChannels,
                        uint chessChannel, bool lockChessChannel)
    {
        InitializeComponent();
        _mesh = mesh;
        _host = host;
        _lockChess = lockChessChannel;
        _channels = cachedChannels ?? Array.Empty<MeshChannel>();
        _chessChannel = chessChannel;

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
    }

    async void OnFetch(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true; SetBusy(true); SetStatus("Reading channels from the device…", false);
        try
        {
            _channels = await _mesh.GetDeviceChannelsAsync();
            _fetched = true;   // PSKs are now read from the device — Update is safe
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

        // Update rewrites the selected channel's name/PSK/options — it needs a live Fetch first so the PSK is
        // read correctly before it's rewritten. The hint explains why it's disabled until then.
        UpdateBtn.IsEnabled = has && _fetched && !_busy;
        UpdateHint.IsVisible = !_fetched;

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
            // Populate the editable Name/PSK from the selected channel so Update can rewrite it. PSK is base64 when
            // known (fetched), blank for an open channel or a key not yet read back.
            NameBox.Text = ch.Name;
            PskBox.Text = ch.Psk;
            // Uplink/downlink are read straight from the device (Fetch to refresh); position too when the device
            // reports it (PositionPrecision >= 0), else off.
            UplinkSwitch.IsToggled = ch.UplinkEnabled;
            DownlinkSwitch.IsToggled = ch.DownlinkEnabled;
            PositionSwitch.IsToggled = ch.PositionPrecision > 0;
        }
        else
        {
            AppKeyBox.Text = ""; TriggerBox.Text = ""; PskShow.Text = ""; AckSignalSwitch.IsToggled = false; AckSignalSwitch.IsEnabled = false;
            NameBox.Text = ""; PskBox.Text = "";
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

    // Rewrite the SELECTED channel's name + PSK + uplink/downlink/position in one request. Guarded so it only runs
    // after a Fetch (so the PSK is read correctly before the channel is rewritten) — the button is also disabled
    // until then. Mirrors the desktop ChannelsWindow's "Update channel".
    async void OnUpdate(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (ChannelList.SelectedItem is not ChannelRow row) { SetStatus("Select a channel to update.", true); return; }
        if (!_fetched) { SetStatus("Tap \"Fetch from device\" first so the channel's PSK is read correctly before updating.", true); return; }
        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) { SetStatus("Enter a channel name first.", true); return; }

        var current = _channels.FirstOrDefault(c => c.Index == row.Index);
        bool up = UplinkSwitch.IsToggled, down = DownlinkSwitch.IsToggled, pos = PositionSwitch.IsToggled;
        // An empty PSK box on a channel whose key couldn't be read back keeps the existing key (null); type "none"
        // to actually clear it. Otherwise the box text is applied (base64 → raw key, anything else → passphrase).
        string pskText = (PskBox.Text ?? "").Trim();
        string? psk = pskText.Length == 0 && current.HasKey && current.Psk.Length == 0 ? null : PskBox.Text;

        _busy = true; SetBusy(true); SetStatus($"Updating channel [{row.Index}]…", false);
        try
        {
            var result = await _mesh.SetChannelAsync(row.Index, name, psk, up, down, pos);
            if (!result.Ok) { SetStatus(result.Error ?? "Update failed.", true); return; }
            // Reflect locally rather than re-reading: a config re-read right after the commit can come back
            // incomplete until the radio settles (the desktop app hit this), so apply the values we just set.
            static bool LooksLikeRawKey(string s)
            {
                try { return Convert.FromBase64String(s.Trim()).Length is 16 or 32; } catch { return false; }
            }
            bool keyCleared = pskText.Length == 0 || pskText == "none";
            bool keepingKey = psk == null;   // updating a channel whose key we left untouched
            bool hasKey = keepingKey ? current.HasKey : !keyCleared;
            string pskShown = keepingKey ? current.Psk : (!keyCleared && LooksLikeRawKey(pskText) ? pskText : "");
            _channels = _channels
                .Select(c => c.Index == row.Index
                    ? c with { Name = name, HasKey = hasKey, Psk = pskShown, UplinkEnabled = up, DownlinkEnabled = down, PositionPrecision = pos ? 32 : 0 }
                    : c)
                .ToList();
            RebuildLists();
            ChannelList.SelectedItem = (ChannelList.ItemsSource as IEnumerable<ChannelRow>)?.FirstOrDefault(r => r.Index == row.Index);
            DeviceCache.Save(_host, _channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
            SetStatus($"Updated channel [{row.Index}].", false);
        }
        catch (Exception ex) { SetStatus($"Update failed: {ex.Message}", true); }
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
        if (!await ThemedDialogs.Alert(this, "Delete channel", $"Disable channel [{row.Index}] on the device?", "Yes", "No")) return;
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
        _tcs.TrySetResult(new Result(_chessChannel, _channels));
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        // Hardware/back nav also commits the current selections.
        if (ChessPicker.SelectedItem is ChannelRow row) _chessChannel = row.Index;
        _tcs.TrySetResult(new Result(_chessChannel, _channels));
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
