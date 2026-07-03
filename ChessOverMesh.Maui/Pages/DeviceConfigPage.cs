using System.Globalization;
using ChessOverMesh.Mesh;
using Meshtastic.Protobufs;
using static Meshtastic.Protobufs.Config.Types;
using static Meshtastic.Protobufs.ModuleConfig.Types;
using Role = Meshtastic.Protobufs.Config.Types.DeviceConfig.Types.Role;
using RebroadcastMode = Meshtastic.Protobufs.Config.Types.DeviceConfig.Types.RebroadcastMode;

namespace ChessOverMesh.Maui;

/// <summary>
/// Device configuration editor — the MAUI port of the desktop <c>DeviceSettingsWindow</c>. Reads the device's
/// current config and lets the user change a focused set of settings (Device, LoRa, Position, Telemetry) plus
/// device actions (reboot/shutdown/resets), writing each section via admin messages. All of the protocol work
/// lives in the shared <see cref="MeshtasticHttpClient"/>, so this is purely the on-phone UI.
///
/// The caller pauses its poll loop for the page's lifetime (the fetch and admin writes drain the radio queue
/// themselves). Await <see cref="Completion"/> to know when the user has closed it.
/// </summary>
public sealed class DeviceConfigPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");
    static readonly Color Warn = Color.FromArgb("#E69A4C");
    static readonly Color Rule = Color.FromArgb("#3F3F46");

    readonly MeshtasticHttpClient _mesh;
    readonly TaskCompletionSource<bool> _tcs = new();
    public Task Completion => _tcs.Task;

    bool _busy;
    bool _loaded;

    // Loaded config clones (edited in place, then written). Null until the initial fetch completes.
    DeviceConfig? _device;
    LoRaConfig? _lora;
    PositionConfig? _position;
    TelemetryConfig? _telemetry;
    DisplayConfig? _display;
    MQTTConfig? _mqtt;
    NeighborInfoConfig? _neighbor;
    RangeTestConfig? _rangeTest;
    StoreForwardConfig? _storeForward;
    User? _owner;

    // Enum option arrays (index ↔ value) backing the pickers.
    static readonly (int Value, string Label)[] Roles =
    {
        (0, "Client"), (1, "Client Mute"), (2, "Router"), (3, "Router Client"), (4, "Repeater"), (5, "Tracker"),
        (6, "Sensor"), (7, "TAK"), (8, "Client Hidden"), (9, "Lost and Found"), (10, "TAK Tracker"),
        (11, "Router Late"), (12, "Client Base"),
    };
    static readonly RebroadcastMode[] Rebroadcasts = (RebroadcastMode[])Enum.GetValues(typeof(RebroadcastMode));
    static readonly LoRaConfig.Types.RegionCode[] Regions = (LoRaConfig.Types.RegionCode[])Enum.GetValues(typeof(LoRaConfig.Types.RegionCode));
    static readonly LoRaConfig.Types.ModemPreset[] Presets = (LoRaConfig.Types.ModemPreset[])Enum.GetValues(typeof(LoRaConfig.Types.ModemPreset));

    // ---- Controls ----
    // Device
    readonly Entry _longName = TextEntry(), _shortName = TextEntry(), _nodeInfoSecs = NumEntry(), _screenOnSecs = NumEntry();
    readonly Picker _role = MakePicker(), _rebroadcast = MakePicker();
    readonly Switch _ledDisabled = new();
    // LoRa
    readonly Picker _region = MakePicker(), _preset = MakePicker();
    readonly Switch _usePreset = new(), _txEnabled = new();
    readonly Entry _hopLimit = NumEntry(), _txPower = NumEntry(), _channelNum = NumEntry();
    readonly Switch _overrideDuty = new(), _boostedGain = new(), _ignoreMqtt = new(), _okToMqtt = new(), _paFanDisabled = new();
    // Position
    readonly Entry _posBroadcast = NumEntry(), _gpsInterval = NumEntry(), _altitude = NumEntry();
    readonly Entry _latitude = TextEntry(), _longitude = TextEntry();
    readonly Switch _posSmart = new(), _fixedPos = new();
    // Telemetry
    readonly Entry _devUpdate = NumEntry(), _envUpdate = NumEntry();
    readonly Switch _envEnabled = new(), _envScreen = new(), _fahrenheit = new();
    // Modules — MQTT
    readonly Switch _mqttEnabled = new(), _mqttEncryption = new(), _mqttJson = new(), _mqttTls = new(), _mqttProxy = new();
    readonly Entry _mqttAddress = TextEntry(), _mqttUsername = TextEntry(), _mqttPassword = TextEntry(), _mqttRoot = TextEntry();
    // Modules — Neighbor info / Range test / Store & forward
    readonly Switch _niEnabled = new();
    readonly Entry _niInterval = NumEntry();
    readonly Switch _rtEnabled = new(), _rtSave = new();
    readonly Entry _rtSender = NumEntry();
    readonly Switch _sfEnabled = new(), _sfHeartbeat = new();
    readonly Entry _sfRecords = NumEntry(), _sfHistoryMax = NumEntry(), _sfHistoryWindow = NumEntry();

    readonly Label _status = new() { TextColor = Dim, FontSize = 12 };
    readonly VerticalStackLayout _body = new() { Spacing = 6, Opacity = 0.5 };

    public DeviceConfigPage(MeshtasticHttpClient mesh)
    {
        _mesh = mesh;
        Title = "Device settings";
        BackgroundColor = Bg;

        foreach (var r in Roles) _role.Items.Add(r.Label);
        foreach (var v in Rebroadcasts) _rebroadcast.Items.Add(v.ToString());
        foreach (var v in Regions) _region.Items.Add(v.ToString());
        foreach (var v in Presets) _preset.Items.Add(v.ToString());

        _body.Add(SectionHeader("Device"));
        _body.Add(Field("Owner name", _longName));
        _body.Add(Field("Short name", _shortName));
        _body.Add(Field("Role", _role));
        _body.Add(Field("Rebroadcast mode", _rebroadcast));
        _body.Add(Field("NodeInfo interval (s)", _nodeInfoSecs));
        _body.Add(Field("Screen on time (s)", _screenOnSecs));
        _body.Add(new Label { Text = "Seconds the screen stays on after activity (0 = always on).", TextColor = Dim, FontSize = 12 });
        _body.Add(SwitchRow("LED heartbeat disabled (turn the status LED off)", _ledDisabled));
        _body.Add(SaveButton("Save device", SaveDeviceAsync));

        _body.Add(Divider());
        _body.Add(SectionHeader("LoRa"));
        _body.Add(new Label { Text = "Region and preset must match every node to stay connected.", TextColor = Warn, FontSize = 12 });
        _body.Add(Field("Region", _region));
        _body.Add(SwitchRow("Use preset", _usePreset));
        _body.Add(Field("Modem preset", _preset));
        _body.Add(Field("Hop limit (0-7)", _hopLimit));
        _body.Add(SwitchRow("TX (transmission) enabled", _txEnabled));
        _body.Add(Field("TX power (dBm)", _txPower));
        _body.Add(Field("Channel number", _channelNum));
        _body.Add(SwitchRow("Override duty cycle (ignore region airtime limit)", _overrideDuty));
        _body.Add(SwitchRow("Boosted RX gain (SX126x)", _boostedGain));
        _body.Add(SwitchRow("PA fan disabled", _paFanDisabled));
        _body.Add(SwitchRow("Ignore MQTT (drop MQTT-sourced packets)", _ignoreMqtt));
        _body.Add(SwitchRow("OK to MQTT (allow uplinking this node to MQTT)", _okToMqtt));
        _body.Add(SaveButton("Save LoRa", SaveLoRaAsync));

        _body.Add(Divider());
        _body.Add(SectionHeader("Position"));
        _body.Add(Field("Broadcast interval (s)", _posBroadcast));
        _body.Add(SwitchRow("Smart broadcast", _posSmart));
        _body.Add(SwitchRow("Fixed position", _fixedPos));
        _body.Add(new Label { Text = "Fixed position uses these manual coordinates (decimal degrees), applied when Fixed position is on.", TextColor = Dim, FontSize = 12 });
        _body.Add(Field("Latitude", _latitude));
        _body.Add(Field("Longitude", _longitude));
        _body.Add(Field("Altitude (m)", _altitude));
        _body.Add(Field("GPS update interval (s)", _gpsInterval));
        _body.Add(SaveButton("Save position", SavePositionAsync));

        _body.Add(Divider());
        _body.Add(SectionHeader("Telemetry"));
        _body.Add(Field("Device interval (s)", _devUpdate));
        _body.Add(Field("Environment interval (s)", _envUpdate));
        _body.Add(SwitchRow("Environment measurement enabled", _envEnabled));
        _body.Add(SwitchRow("Show environment on screen", _envScreen));
        _body.Add(SwitchRow("Display °F", _fahrenheit));
        _body.Add(SaveButton("Save telemetry", SaveTelemetryAsync));

        _body.Add(Divider());
        _body.Add(SectionHeader("Modules"));

        _body.Add(new Label { Text = "MQTT", TextColor = Fg, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 4, 0, 0) });
        _body.Add(new Label { Text = "Bridges the mesh to an MQTT server (e.g. mqtt.meshtastic.org). Pick which channels uplink/downlink under Channels → channel options.", TextColor = Dim, FontSize = 12 });
        _body.Add(SwitchRow("MQTT enabled", _mqttEnabled));
        _body.Add(Field("Server address", _mqttAddress));
        _body.Add(Field("Username", _mqttUsername));
        _body.Add(Field("Password", _mqttPassword));
        _body.Add(Field("Root topic", _mqttRoot));
        _body.Add(SwitchRow("Encryption enabled", _mqttEncryption));
        _body.Add(SwitchRow("JSON enabled", _mqttJson));
        _body.Add(SwitchRow("TLS enabled", _mqttTls));
        _body.Add(SwitchRow("Proxy to client (phone uplinks MQTT)", _mqttProxy));
        _body.Add(SaveButton("Save MQTT", SaveMqttAsync));

        _body.Add(new Label { Text = "Neighbor info", TextColor = Fg, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) });
        _body.Add(new Label { Text = "Periodically broadcasts directly-heard neighbors. Adds airtime — use a long interval.", TextColor = Dim, FontSize = 12 });
        _body.Add(SwitchRow("Neighbor info enabled", _niEnabled));
        _body.Add(Field("Update interval (s)", _niInterval));
        _body.Add(SaveButton("Save neighbor info", SaveNeighborAsync));

        _body.Add(new Label { Text = "Range test", TextColor = Fg, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) });
        _body.Add(new Label { Text = "Sender transmits sequenced test packets every N seconds; receivers can log them. 0 sender = receive only.", TextColor = Dim, FontSize = 12 });
        _body.Add(SwitchRow("Range test enabled", _rtEnabled));
        _body.Add(Field("Sender interval (s)", _rtSender));
        _body.Add(SwitchRow("Save results to file", _rtSave));
        _body.Add(SaveButton("Save range test", SaveRangeTestAsync));

        _body.Add(new Label { Text = "Store & forward", TextColor = Fg, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) });
        _body.Add(new Label { Text = "Routers can store messages and replay missed ones to clients that come back online. Needs PSRAM.", TextColor = Dim, FontSize = 12 });
        _body.Add(SwitchRow("Store & forward enabled", _sfEnabled));
        _body.Add(SwitchRow("Heartbeat", _sfHeartbeat));
        _body.Add(Field("Records", _sfRecords));
        _body.Add(Field("History return max", _sfHistoryMax));
        _body.Add(Field("History window (s)", _sfHistoryWindow));
        _body.Add(SaveButton("Save store & forward", SaveStoreForwardAsync));

        _body.Add(Divider());
        _body.Add(SectionHeader("Actions"));
        _body.Add(new Label { Text = "Announce this node on the mesh now (the same broadcasts the firmware sends on a timer):", TextColor = Dim, FontSize = 12 });
        _body.Add(ActionButton("Broadcast node info", "Broadcast your node info (name, hardware, role) to the whole mesh now?", () => _mesh.BroadcastOwnNodeInfoAsync()));
        _body.Add(ActionButton("Broadcast position", "Broadcast your position to the whole mesh now?", () => _mesh.BroadcastOwnPositionAsync()));
        _body.Add(new Label { Text = "Reboot/Shutdown will drop the connection. Resets are destructive.", TextColor = Warn, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        _body.Add(ActionButton("Reboot", "Reboot the device now?", () => _mesh.RebootAsync(5)));
        _body.Add(ActionButton("Shutdown", "Shut the device down now?", () => _mesh.ShutdownAsync(5)));
        _body.Add(ActionButton("NodeDB reset", "Clear the device's node database? It will forget all known nodes.", () => _mesh.NodeDbResetAsync()));
        _body.Add(ActionButton("Factory reset", "FACTORY RESET the device? This erases ALL settings and channels and cannot be undone.", () => _mesh.FactoryResetAsync(), doubleConfirm: true));

        var closeBtn = new Button { Text = "Close", MinimumHeightRequest = 44, Margin = new Thickness(0, 12, 0, 0) };
        closeBtn.Clicked += async (_, _) => await CloseAsync();

        var root = new VerticalStackLayout { Padding = 16, Spacing = 8 };
        root.Add(new Label { Text = "Device configuration", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        root.Add(_status);
        root.Add(_body);
        root.Add(closeBtn);
        Content = new ScrollView { Content = root };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded) await LoadAsync();
    }

    async Task LoadAsync()
    {
        _status.Text = "Reading settings from the device…";
        try
        {
            await _mesh.FetchDeviceConfigAsync(TimeSpan.FromSeconds(15));
            _device = _mesh.GetDeviceConfig();
            _lora = _mesh.GetLoRaConfig();
            _position = _mesh.GetPositionConfig();
            _telemetry = _mesh.GetTelemetryConfig();
            _display = _mesh.GetDisplayConfig();
            // Module configs are secondary — fall back to defaults if the device didn't report one so the section
            // stays usable rather than blocking the whole page.
            _mqtt = _mesh.GetMqttConfig() ?? new MQTTConfig();
            _neighbor = _mesh.GetNeighborInfoConfig() ?? new NeighborInfoConfig();
            _rangeTest = _mesh.GetRangeTestConfig() ?? new RangeTestConfig();
            _storeForward = _mesh.GetStoreForwardConfig() ?? new StoreForwardConfig();
            _owner = _mesh.GetOwner() ?? new User();
            // Only allow editing if we actually read the real config — otherwise a Save would overwrite the
            // device with defaults. Keep editing disabled and ask the user to retry.
            if (_device == null || _lora == null || _position == null || _telemetry == null)
            {
                _status.Text = "Could not read the full configuration from the device — editing is disabled. " +
                               "Close and try again (make sure the device is reachable).";
                return;
            }
            Populate();
            _loaded = true;
            _body.Opacity = 1;
            _status.Text = "Settings loaded. Change a value and tap its Save button.";
        }
        catch (Exception ex) { _status.Text = $"Could not read settings: {ex.Message}"; }
    }

    void Populate()
    {
        _longName.Text = _owner!.LongName;
        _shortName.Text = _owner.ShortName;
        _role.SelectedIndex = Math.Max(0, Array.FindIndex(Roles, r => r.Value == (int)_device!.Role));
        _rebroadcast.SelectedIndex = Math.Max(0, Array.IndexOf(Rebroadcasts, _device!.RebroadcastMode));
        _nodeInfoSecs.Text = _device.NodeInfoBroadcastSecs.ToString();
        _screenOnSecs.Text = (_display?.ScreenOnSecs ?? 0).ToString();
        _ledDisabled.IsToggled = _mesh.GetLedHeartbeatDisabled();

        _region.SelectedIndex = Math.Max(0, Array.IndexOf(Regions, _lora!.Region));
        _usePreset.IsToggled = _lora.UsePreset;
        _preset.SelectedIndex = Math.Max(0, Array.IndexOf(Presets, _lora.ModemPreset));
        _hopLimit.Text = _lora.HopLimit.ToString();
        _txEnabled.IsToggled = _lora.TxEnabled;
        _txPower.Text = _lora.TxPower.ToString();
        _channelNum.Text = _lora.ChannelNum.ToString();
        _overrideDuty.IsToggled = _lora.OverrideDutyCycle;
        _boostedGain.IsToggled = _lora.Sx126XRxBoostedGain;
        var extras = _mesh.GetLoRaExtras();   // ignore_mqtt / config_ok_to_mqtt / pa_fan_disabled (not in the bundled protobuf)
        _ignoreMqtt.IsToggled = extras.IgnoreMqtt;
        _okToMqtt.IsToggled = extras.OkToMqtt;
        _paFanDisabled.IsToggled = extras.PaFanDisabled;

        _posBroadcast.Text = _position!.PositionBroadcastSecs.ToString();
        _posSmart.IsToggled = _position.PositionBroadcastSmartEnabled;
        _fixedPos.IsToggled = _position.FixedPosition;
        _gpsInterval.Text = _position.GpsUpdateInterval.ToString();
        var pos = _mesh.GetOwnPosition();
        _latitude.Text = pos != null && pos.LatitudeI != 0 ? (pos.LatitudeI / 1e7).ToString("0.#######", CultureInfo.InvariantCulture) : "";
        _longitude.Text = pos != null && pos.LongitudeI != 0 ? (pos.LongitudeI / 1e7).ToString("0.#######", CultureInfo.InvariantCulture) : "";
        _altitude.Text = pos != null && pos.Altitude != 0 ? pos.Altitude.ToString() : "";

        _devUpdate.Text = _telemetry!.DeviceUpdateInterval.ToString();
        _envUpdate.Text = _telemetry.EnvironmentUpdateInterval.ToString();
        _envEnabled.IsToggled = _telemetry.EnvironmentMeasurementEnabled;
        _envScreen.IsToggled = _telemetry.EnvironmentScreenEnabled;
        _fahrenheit.IsToggled = _telemetry.EnvironmentDisplayFahrenheit;

        _mqttEnabled.IsToggled = _mqtt!.Enabled;
        _mqttAddress.Text = _mqtt.Address;
        _mqttUsername.Text = _mqtt.Username;
        _mqttPassword.Text = _mqtt.Password;
        _mqttRoot.Text = _mqtt.Root;
        _mqttEncryption.IsToggled = _mqtt.EncryptionEnabled;
        _mqttJson.IsToggled = _mqtt.JsonEnabled;
        _mqttTls.IsToggled = _mqtt.TlsEnabled;
        _mqttProxy.IsToggled = _mqtt.ProxyToClientEnabled;

        _niEnabled.IsToggled = _neighbor!.Enabled;
        _niInterval.Text = _neighbor.UpdateInterval.ToString();

        _rtEnabled.IsToggled = _rangeTest!.Enabled;
        _rtSender.Text = _rangeTest.Sender.ToString();
        _rtSave.IsToggled = _rangeTest.Save;

        _sfEnabled.IsToggled = _storeForward!.Enabled;
        _sfHeartbeat.IsToggled = _storeForward.Heartbeat;
        _sfRecords.Text = _storeForward.Records.ToString();
        _sfHistoryMax.Text = _storeForward.HistoryReturnMax.ToString();
        _sfHistoryWindow.Text = _storeForward.HistoryReturnWindow.ToString();
    }

    // ---- Per-section saves (mirror the desktop editor) -------------------------------------------

    async Task<string?> SaveDeviceAsync()
    {
        if (_device == null) return null;
        _device.Role = (Role)Roles[Math.Max(0, _role.SelectedIndex)].Value;
        _device.RebroadcastMode = Rebroadcasts[Math.Max(0, _rebroadcast.SelectedIndex)];
        _device.NodeInfoBroadcastSecs = ParseU(_nodeInfoSecs, _device.NodeInfoBroadcastSecs);
        if (_display != null) _display.ScreenOnSecs = ParseU(_screenOnSecs, _display.ScreenOnSecs);   // screen-on lives in DisplayConfig
        // Only write the owner if it actually changed.
        User? ownerToWrite = null;
        if (_owner != null && (_owner.LongName != _longName.Text?.Trim() || _owner.ShortName != _shortName.Text?.Trim()))
        {
            _owner.LongName = _longName.Text?.Trim() ?? "";
            _owner.ShortName = _shortName.Text?.Trim() ?? "";
            ownerToWrite = _owner;
        }
        // One transaction → one reboot, so the device + display (screen-on) changes both persist. (Writing them
        // separately reboots after the first and drops the second — that's why screen-on reverted to 600.)
        return await _mesh.WriteDeviceSettingsAsync(ownerToWrite, _device, _ledDisabled.IsToggled, _display);
    }

    async Task<string?> SaveLoRaAsync()
    {
        if (_lora == null) return null;
        _lora.Region = Regions[Math.Max(0, _region.SelectedIndex)];
        _lora.UsePreset = _usePreset.IsToggled;
        _lora.ModemPreset = Presets[Math.Max(0, _preset.SelectedIndex)];
        _lora.HopLimit = Math.Min(7, ParseU(_hopLimit, _lora.HopLimit));
        _lora.TxEnabled = _txEnabled.IsToggled;
        _lora.TxPower = ParseI(_txPower, _lora.TxPower);
        _lora.ChannelNum = ParseU(_channelNum, _lora.ChannelNum);
        _lora.OverrideDutyCycle = _overrideDuty.IsToggled;
        _lora.Sx126XRxBoostedGain = _boostedGain.IsToggled;
        // ignore_mqtt / config_ok_to_mqtt / pa_fan_disabled aren't in the bundled protobuf — write via the helper.
        return await _mesh.WriteLoRaConfigAsync(_lora, _ignoreMqtt.IsToggled, _okToMqtt.IsToggled, _paFanDisabled.IsToggled);
    }

    async Task<string?> SavePositionAsync()
    {
        if (_position == null) return null;
        _position.PositionBroadcastSecs = ParseU(_posBroadcast, _position.PositionBroadcastSecs);
        _position.PositionBroadcastSmartEnabled = _posSmart.IsToggled;
        bool fixedOn = _fixedPos.IsToggled;
        if (fixedOn && (!TryParseCoord(_latitude.Text, out _) || !TryParseCoord(_longitude.Text, out _)))
            return "Enter a valid latitude and longitude for fixed position.";
        _position.FixedPosition = fixedOn;
        _position.GpsUpdateInterval = ParseU(_gpsInterval, _position.GpsUpdateInterval);
        var err = await _mesh.WriteConfigAsync(_position);
        if (err != null) return err;
        // Push (or clear) the manual coordinates to match the Fixed-position toggle.
        if (fixedOn)
        {
            TryParseCoord(_latitude.Text, out double lat);
            TryParseCoord(_longitude.Text, out double lon);
            return await _mesh.SetFixedPositionAsync(lat, lon, ParseI(_altitude, 0));
        }
        return await _mesh.RemoveFixedPositionAsync();
    }

    async Task<string?> SaveTelemetryAsync()
    {
        if (_telemetry == null) return null;
        _telemetry.DeviceUpdateInterval = ParseU(_devUpdate, _telemetry.DeviceUpdateInterval);
        _telemetry.EnvironmentUpdateInterval = ParseU(_envUpdate, _telemetry.EnvironmentUpdateInterval);
        _telemetry.EnvironmentMeasurementEnabled = _envEnabled.IsToggled;
        _telemetry.EnvironmentScreenEnabled = _envScreen.IsToggled;
        _telemetry.EnvironmentDisplayFahrenheit = _fahrenheit.IsToggled;
        return await _mesh.WriteModuleConfigAsync(_telemetry);
    }

    async Task<string?> SaveMqttAsync()
    {
        if (_mqtt == null) return null;
        _mqtt.Enabled = _mqttEnabled.IsToggled;
        _mqtt.Address = _mqttAddress.Text?.Trim() ?? "";
        _mqtt.Username = _mqttUsername.Text?.Trim() ?? "";
        _mqtt.Password = _mqttPassword.Text ?? "";
        _mqtt.Root = _mqttRoot.Text?.Trim() ?? "";
        _mqtt.EncryptionEnabled = _mqttEncryption.IsToggled;
        _mqtt.JsonEnabled = _mqttJson.IsToggled;
        _mqtt.TlsEnabled = _mqttTls.IsToggled;
        _mqtt.ProxyToClientEnabled = _mqttProxy.IsToggled;
        return await _mesh.WriteModuleConfigAsync(_mqtt);
    }

    async Task<string?> SaveNeighborAsync()
    {
        if (_neighbor == null) return null;
        _neighbor.Enabled = _niEnabled.IsToggled;
        _neighbor.UpdateInterval = ParseU(_niInterval, _neighbor.UpdateInterval);
        return await _mesh.WriteModuleConfigAsync(_neighbor);
    }

    async Task<string?> SaveRangeTestAsync()
    {
        if (_rangeTest == null) return null;
        _rangeTest.Enabled = _rtEnabled.IsToggled;
        _rangeTest.Sender = ParseU(_rtSender, _rangeTest.Sender);
        _rangeTest.Save = _rtSave.IsToggled;
        return await _mesh.WriteModuleConfigAsync(_rangeTest);
    }

    async Task<string?> SaveStoreForwardAsync()
    {
        if (_storeForward == null) return null;
        _storeForward.Enabled = _sfEnabled.IsToggled;
        _storeForward.Heartbeat = _sfHeartbeat.IsToggled;
        _storeForward.Records = ParseU(_sfRecords, _storeForward.Records);
        _storeForward.HistoryReturnMax = ParseU(_sfHistoryMax, _storeForward.HistoryReturnMax);
        _storeForward.HistoryReturnWindow = ParseU(_sfHistoryWindow, _storeForward.HistoryReturnWindow);
        return await _mesh.WriteModuleConfigAsync(_storeForward);
    }

    // ---- UI helpers -----------------------------------------------------------------------------

    View SaveButton(string text, Func<Task<string?>> write)
    {
        var btn = new Button { Text = text, MinimumHeightRequest = 40, HorizontalOptions = LayoutOptions.End, Margin = new Thickness(0, 6, 0, 0) };
        btn.Clicked += async (_, _) =>
        {
            if (_busy || !_loaded) return;
            _busy = true; SetBusy(true); _status.Text = "Writing…";
            try
            {
                string? err = await write();
                _status.Text = err == null ? "Saved to the device." : $"The radio rejected the change: {err}";
            }
            catch (Exception ex) { _status.Text = $"Write failed: {ex.Message}"; }
            finally { _busy = false; SetBusy(false); }
        };
        return btn;
    }

    View ActionButton(string text, string confirm, Func<Task> action, bool doubleConfirm = false)
    {
        var btn = new Button { Text = text, MinimumHeightRequest = 40, HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(0, 4, 0, 0) };
        btn.Clicked += async (_, _) =>
        {
            if (_busy || !_loaded) return;
            if (!await ThemedDialogs.Alert(this, text, confirm, "Yes", "No")) return;
            if (doubleConfirm && !await ThemedDialogs.Alert(this, text, "Are you absolutely sure? This cannot be undone.", "Yes", "No")) return;
            _busy = true; SetBusy(true); _status.Text = $"Sending {text}…";
            try { await action(); _status.Text = $"{text} sent."; }
            catch (Exception ex) { _status.Text = $"{text} failed: {ex.Message}"; }
            finally { _busy = false; SetBusy(false); }
        };
        return btn;
    }

    void SetBusy(bool busy) => _body.IsEnabled = !busy;

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

    static Label SectionHeader(string text) =>
        new() { Text = text, TextColor = Fg, FontAttributes = FontAttributes.Bold, FontSize = 16, Margin = new Thickness(0, 6, 0, 0) };

    static BoxView Divider() => new() { HeightRequest = 1, Color = Rule, Margin = new Thickness(0, 8) };

    static View Field(string label, View control)
    {
        control.HorizontalOptions = LayoutOptions.Fill;
        return new VerticalStackLayout { Spacing = 2, Margin = new Thickness(0, 2), Children =
        {
            new Label { Text = label, TextColor = Dim, FontSize = 12 }, control,
        } };
    }

    static View SwitchRow(string label, Switch sw)
    {
        sw.VerticalOptions = LayoutOptions.Center;
        var row = new HorizontalStackLayout { Spacing = 10, Margin = new Thickness(0, 2) };
        row.Add(sw);
        row.Add(new Label { Text = label, TextColor = Fg, VerticalOptions = LayoutOptions.Center });
        return row;
    }

    static Entry TextEntry() => new() { TextColor = Fg, BackgroundColor = Bg };
    static Entry NumEntry() => new() { TextColor = Fg, BackgroundColor = Bg, Keyboard = Keyboard.Numeric };
    static Picker MakePicker() => new() { TextColor = Fg, BackgroundColor = Bg };

    static bool TryParseCoord(string? s, out double value) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    static uint ParseU(Entry t, uint fallback) => uint.TryParse((t.Text ?? "").Trim(), out var v) ? v : fallback;
    static int ParseI(Entry t, int fallback) => int.TryParse((t.Text ?? "").Trim(), out var v) ? v : fallback;
}
