using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChessOverMesh.Mesh;
using Meshtastic.Protobufs;
using static Meshtastic.Protobufs.Config.Types;
using static Meshtastic.Protobufs.ModuleConfig.Types;
using Role = Meshtastic.Protobufs.Config.Types.DeviceConfig.Types.Role;
using RebroadcastMode = Meshtastic.Protobufs.Config.Types.DeviceConfig.Types.RebroadcastMode;

namespace ChessOverMesh.Gui;

/// <summary>
/// Reads the device's current configuration and lets the user change a focused set of settings (Device,
/// LoRa, Position, Telemetry) plus device actions (reboot/shutdown/resets), writing each via admin messages.
/// The caller must pause its /fromradio poll loop for the dialog's lifetime — the fetch and admin writes
/// drain the radio queue themselves.
/// </summary>
internal sealed class DeviceSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE6, 0x9A, 0x4C));

    private readonly MeshtasticHttpClient _mesh;
    private readonly TextBlock _status = new() { Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TabControl _tabs = new() { Margin = new Thickness(0, 4, 0, 0), MinHeight = 300, Background = Bg };
    private bool _busy;

    // Loaded config clones (edited in place, then written). Null until the initial fetch completes.
    private DeviceConfig? _device;
    private LoRaConfig? _lora;
    private PositionConfig? _position;
    private TelemetryConfig? _telemetry;
    private DisplayConfig? _display;
    private MQTTConfig? _mqtt;
    private NeighborInfoConfig? _neighbor;
    private RangeTestConfig? _rangeTest;
    private StoreForwardConfig? _storeForward;
    private User? _owner;

    // Device tab controls
    private readonly TextBox _longName = Text(180), _shortName = Text(60), _nodeInfoSecs = Text(90), _screenOnSecs = Text(90);
    private readonly CheckBox _ledDisabled = Check("LED heartbeat disabled (turn the status LED off)");
    private readonly CheckBox _licensed = Check("Licensed (ham) operator");
    private readonly ComboBox _role = Combo(180), _rebroadcast = Combo(180);
    // LoRa tab controls
    private readonly ComboBox _region = Combo(160), _preset = Combo(160);
    private readonly CheckBox _usePreset = Check("Use preset"), _txEnabled = Check("TX (transmission) enabled");
    private readonly TextBox _hopLimit = Text(90), _txPower = Text(90), _channelNum = Text(90);
    private readonly CheckBox _overrideDuty = Check("Override duty cycle (ignore region airtime limit)"),
                              _boostedGain = Check("Boosted RX gain (SX126x)"),
                              _ignoreMqtt = Check("Ignore MQTT (drop MQTT-sourced packets)"),
                              _okToMqtt = Check("OK to MQTT (allow uplinking this node to MQTT)"),
                              _paFanDisabled = Check("PA fan disabled");
    // Position tab controls
    private readonly TextBox _posBroadcast = Text(90), _gpsInterval = Text(90), _latitude = Text(140), _longitude = Text(140), _altitude = Text(90);
    private readonly CheckBox _posSmart = Check("Smart broadcast"), _fixedPos = Check("Fixed position");
    // Telemetry tab controls
    private readonly TextBox _devUpdate = Text(90), _envUpdate = Text(90);
    private readonly CheckBox _envEnabled = Check("Environment measurement enabled"),
                              _envScreen = Check("Show environment on screen"),
                              _fahrenheit = Check("Display °F");
    // Modules tab controls
    private readonly CheckBox _mqttEnabled = Check("MQTT enabled"), _mqttEncryption = Check("Encryption enabled"),
                              _mqttJson = Check("JSON enabled"), _mqttTls = Check("TLS enabled"), _mqttProxy = Check("Proxy to client (phone uplinks MQTT)");
    private readonly TextBox _mqttAddress = Text(220), _mqttUsername = Text(180), _mqttPassword = Text(180), _mqttRoot = Text(180);
    private readonly CheckBox _niEnabled = Check("Neighbor info enabled");
    private readonly TextBox _niInterval = Text(90);
    private readonly CheckBox _rtEnabled = Check("Range test enabled"), _rtSave = Check("Save results to file");
    private readonly TextBox _rtSender = Text(90);
    private readonly CheckBox _sfEnabled = Check("Store & forward enabled"), _sfHeartbeat = Check("Heartbeat");
    private readonly TextBox _sfRecords = Text(90), _sfHistoryMax = Text(90), _sfHistoryWindow = Text(90);

    public DeviceSettingsWindow(Window owner, MeshtasticHttpClient mesh)
    {
        _mesh = mesh;
        Title = "Device settings";
        Owner = owner;
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = Bg;

        BuildRoleItems();
        FillEnum(_rebroadcast, Enum.GetValues(typeof(RebroadcastMode)));
        FillEnum(_region, Enum.GetValues(typeof(LoRaConfig.Types.RegionCode)));
        FillEnum(_preset, Enum.GetValues(typeof(LoRaConfig.Types.ModemPreset)));

        Content = BuildLayout();
        SetEnabled(false);
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock { Text = "Device configuration", Foreground = Fg, FontWeight = FontWeights.Bold });

        _tabs.Items.Add(DeviceTab());
        _tabs.Items.Add(LoRaTab());
        _tabs.Items.Add(PositionTab());
        _tabs.Items.Add(TelemetryTab());
        _tabs.Items.Add(ModulesTab());
        _tabs.Items.Add(ActionsTab());
        root.Children.Add(_tabs);

        root.Children.Add(_status);

        var closeBtn = new Button { Content = "Close", MinWidth = 80, MinHeight = 26, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        closeBtn.Click += (_, _) => Close();
        root.Children.Add(closeBtn);
        return root;
    }

    private TabItem DeviceTab()
    {
        var p = Section();
        p.Children.Add(Row("Owner name:", _longName));
        p.Children.Add(Row("Short name:", _shortName));
        p.Children.Add(Row("Role:", _role));
        p.Children.Add(Row("Rebroadcast mode:", _rebroadcast));
        p.Children.Add(Row("NodeInfo interval (s):", _nodeInfoSecs));
        p.Children.Add(Row("Screen on time (s):", _screenOnSecs));
        p.Children.Add(new TextBlock { Text = "Seconds the device screen stays on after activity (0 = always on, as the native app's display timeout).", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
        p.Children.Add(Row("", _ledDisabled));
        p.Children.Add(Row("", _licensed));
        p.Children.Add(new TextBlock { Text = "Marks this node as a licensed amateur radio operator (sets the User.is_licensed flag).", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
        p.Children.Add(SaveButton("Save device", async () =>
        {
            if (_device == null) return null;
            _device.Role = (Role)((RoleItem)_role.SelectedItem).Value;
            _device.RebroadcastMode = (RebroadcastMode)_rebroadcast.SelectedItem;
            _device.NodeInfoBroadcastSecs = ParseU(_nodeInfoSecs, _device.NodeInfoBroadcastSecs);
            if (_display != null) _display.ScreenOnSecs = ParseU(_screenOnSecs, _display.ScreenOnSecs);   // screen-on lives in DisplayConfig
            // Only write the owner if it actually changed.
            User? ownerToWrite = null;
            if (_owner != null && (_owner.LongName != _longName.Text.Trim() || _owner.ShortName != _shortName.Text.Trim()
                                   || _owner.IsLicensed != (_licensed.IsChecked == true)))
            {
                _owner.LongName = _longName.Text.Trim();
                _owner.ShortName = _shortName.Text.Trim();
                _owner.IsLicensed = _licensed.IsChecked == true;
                ownerToWrite = _owner;
            }
            // One transaction → one reboot, so the device + display (screen-on) changes both persist. (Writing them
            // separately reboots after the first and drops the second — that's why screen-on reverted to 600.)
            return await _mesh.WriteDeviceSettingsAsync(ownerToWrite, _device, _ledDisabled.IsChecked == true, _display);
        }));
        return Tab("Device", p);
    }

    private TabItem LoRaTab()
    {
        var p = Section();
        p.Children.Add(new TextBlock { Text = "Region and preset must match every node to stay connected.", Foreground = Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(Row("Region:", _region));
        p.Children.Add(Row("", _usePreset));
        p.Children.Add(Row("Modem preset:", _preset));
        p.Children.Add(Row("Hop limit (0-7):", _hopLimit));
        p.Children.Add(Row("", _txEnabled));
        p.Children.Add(Row("TX power (dBm):", _txPower));
        p.Children.Add(Row("Channel number:", _channelNum));
        p.Children.Add(Row("", _overrideDuty));
        p.Children.Add(Row("", _boostedGain));
        p.Children.Add(Row("", _paFanDisabled));
        p.Children.Add(Row("", _ignoreMqtt));
        p.Children.Add(Row("", _okToMqtt));
        p.Children.Add(SaveButton("Save LoRa", async () =>
        {
            if (_lora == null) return null;
            _lora.Region = (LoRaConfig.Types.RegionCode)_region.SelectedItem;
            _lora.UsePreset = _usePreset.IsChecked == true;
            _lora.ModemPreset = (LoRaConfig.Types.ModemPreset)_preset.SelectedItem;
            _lora.HopLimit = Math.Min(7, ParseU(_hopLimit, _lora.HopLimit));
            _lora.TxEnabled = _txEnabled.IsChecked == true;
            _lora.TxPower = ParseI(_txPower, _lora.TxPower);
            _lora.ChannelNum = ParseU(_channelNum, _lora.ChannelNum);
            _lora.OverrideDutyCycle = _overrideDuty.IsChecked == true;
            _lora.Sx126XRxBoostedGain = _boostedGain.IsChecked == true;
            // ignore_mqtt / config_ok_to_mqtt / pa_fan_disabled aren't in the bundled protobuf — write via the helper.
            return await _mesh.WriteLoRaConfigAsync(_lora, _ignoreMqtt.IsChecked == true, _okToMqtt.IsChecked == true, _paFanDisabled.IsChecked == true);
        }));
        return Tab("LoRa", p);
    }

    private TabItem PositionTab()
    {
        var p = Section();
        p.Children.Add(Row("Broadcast interval (s):", _posBroadcast));
        p.Children.Add(Row("", _posSmart));
        p.Children.Add(Row("", _fixedPos));
        p.Children.Add(new TextBlock { Text = "Fixed position uses these manual coordinates (decimal degrees), applied when Fixed position is checked.", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        p.Children.Add(Row("Latitude:", _latitude));
        p.Children.Add(Row("Longitude:", _longitude));
        p.Children.Add(Row("Altitude (m):", _altitude));
        p.Children.Add(Row("GPS update interval (s):", _gpsInterval));
        p.Children.Add(SaveButton("Save position", async () =>
        {
            if (_position == null) return null;
            _position.PositionBroadcastSecs = ParseU(_posBroadcast, _position.PositionBroadcastSecs);
            _position.PositionBroadcastSmartEnabled = _posSmart.IsChecked == true;
            bool fixedOn = _fixedPos.IsChecked == true;
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
        }));
        return Tab("Position", p);
    }

    private static bool TryParseCoord(string s, out double value) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private TabItem TelemetryTab()
    {
        var p = Section();
        p.Children.Add(Row("Device interval (s):", _devUpdate));
        p.Children.Add(Row("Environment interval (s):", _envUpdate));
        p.Children.Add(Row("", _envEnabled));
        p.Children.Add(Row("", _envScreen));
        p.Children.Add(Row("", _fahrenheit));
        p.Children.Add(SaveButton("Save telemetry", async () =>
        {
            if (_telemetry == null) return null;
            _telemetry.DeviceUpdateInterval = ParseU(_devUpdate, _telemetry.DeviceUpdateInterval);
            _telemetry.EnvironmentUpdateInterval = ParseU(_envUpdate, _telemetry.EnvironmentUpdateInterval);
            _telemetry.EnvironmentMeasurementEnabled = _envEnabled.IsChecked == true;
            _telemetry.EnvironmentScreenEnabled = _envScreen.IsChecked == true;
            _telemetry.EnvironmentDisplayFahrenheit = _fahrenheit.IsChecked == true;
            return await _mesh.WriteModuleConfigAsync(_telemetry);
        }));
        return Tab("Telemetry", p);
    }

    private TabItem ModulesTab()
    {
        var p = new StackPanel { Margin = new Thickness(10) };

        // ---- MQTT ----
        p.Children.Add(ModuleHeader("MQTT"));
        p.Children.Add(new TextBlock { Text = "Bridges the mesh to an MQTT server (e.g. mqtt.meshtastic.org). Choose which channels uplink/downlink under Channels → channel options.", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(Row("", _mqttEnabled));
        p.Children.Add(Row("Server address:", _mqttAddress));
        p.Children.Add(Row("Username:", _mqttUsername));
        p.Children.Add(Row("Password:", _mqttPassword));
        p.Children.Add(Row("Root topic:", _mqttRoot));
        p.Children.Add(Row("", _mqttEncryption));
        p.Children.Add(Row("", _mqttJson));
        p.Children.Add(Row("", _mqttTls));
        p.Children.Add(Row("", _mqttProxy));
        p.Children.Add(SaveButton("Save MQTT", async () =>
        {
            if (_mqtt == null) return null;
            _mqtt.Enabled = _mqttEnabled.IsChecked == true;
            _mqtt.Address = _mqttAddress.Text.Trim();
            _mqtt.Username = _mqttUsername.Text.Trim();
            _mqtt.Password = _mqttPassword.Text;
            _mqtt.Root = _mqttRoot.Text.Trim();
            _mqtt.EncryptionEnabled = _mqttEncryption.IsChecked == true;
            _mqtt.JsonEnabled = _mqttJson.IsChecked == true;
            _mqtt.TlsEnabled = _mqttTls.IsChecked == true;
            _mqtt.ProxyToClientEnabled = _mqttProxy.IsChecked == true;
            return await _mesh.WriteModuleConfigAsync(_mqtt);
        }));

        // ---- Neighbor Info ----
        p.Children.Add(ModuleHeader("Neighbor info"));
        p.Children.Add(new TextBlock { Text = "Periodically broadcasts the list of directly-heard neighbors. Increases airtime — use a long interval.", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(Row("", _niEnabled));
        p.Children.Add(Row("Update interval (s):", _niInterval));
        p.Children.Add(SaveButton("Save neighbor info", async () =>
        {
            if (_neighbor == null) return null;
            _neighbor.Enabled = _niEnabled.IsChecked == true;
            _neighbor.UpdateInterval = ParseU(_niInterval, _neighbor.UpdateInterval);
            return await _mesh.WriteModuleConfigAsync(_neighbor);
        }));

        // ---- Range Test ----
        p.Children.Add(ModuleHeader("Range test"));
        p.Children.Add(new TextBlock { Text = "Sender transmits sequenced test packets every N seconds; receivers can log them. 0 sender = receive only.", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(Row("", _rtEnabled));
        p.Children.Add(Row("Sender interval (s):", _rtSender));
        p.Children.Add(Row("", _rtSave));
        p.Children.Add(SaveButton("Save range test", async () =>
        {
            if (_rangeTest == null) return null;
            _rangeTest.Enabled = _rtEnabled.IsChecked == true;
            _rangeTest.Sender = ParseU(_rtSender, _rangeTest.Sender);
            _rangeTest.Save = _rtSave.IsChecked == true;
            return await _mesh.WriteModuleConfigAsync(_rangeTest);
        }));

        // ---- Store & Forward ----
        p.Children.Add(ModuleHeader("Store & forward"));
        p.Children.Add(new TextBlock { Text = "Routers can store messages and replay missed ones to clients that come back online. Needs PSRAM.", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(Row("", _sfEnabled));
        p.Children.Add(Row("", _sfHeartbeat));
        p.Children.Add(Row("Records:", _sfRecords));
        p.Children.Add(Row("History return max:", _sfHistoryMax));
        p.Children.Add(Row("History window (s):", _sfHistoryWindow));
        p.Children.Add(SaveButton("Save store & forward", async () =>
        {
            if (_storeForward == null) return null;
            _storeForward.Enabled = _sfEnabled.IsChecked == true;
            _storeForward.Heartbeat = _sfHeartbeat.IsChecked == true;
            _storeForward.Records = ParseU(_sfRecords, _storeForward.Records);
            _storeForward.HistoryReturnMax = ParseU(_sfHistoryMax, _storeForward.HistoryReturnMax);
            _storeForward.HistoryReturnWindow = ParseU(_sfHistoryWindow, _storeForward.HistoryReturnWindow);
            return await _mesh.WriteModuleConfigAsync(_storeForward);
        }));

        var scroll = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 };
        return Tab("Modules", scroll);
    }

    private static TextBlock ModuleHeader(string text) =>
        new() { Text = text, Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 2) };

    private TabItem ActionsTab()
    {
        var p = Section();
        p.Children.Add(new TextBlock { Text = "Announce this node on the mesh now (the same broadcasts the firmware sends on a timer):", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(ActionButton("Broadcast node info", "Broadcast your node info (name, hardware, role) to the whole mesh now?", () => _mesh.BroadcastOwnNodeInfoAsync()));
        p.Children.Add(ActionButton("Broadcast position", "Broadcast your position to the whole mesh now?", () => _mesh.BroadcastOwnPositionAsync()));

        p.Children.Add(new TextBlock { Text = "Reboot/Shutdown will drop the connection. Resets are destructive.", Foreground = Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) });

        p.Children.Add(ActionButton("Reboot", "Reboot the device now?", () => _mesh.RebootAsync(5)));
        p.Children.Add(ActionButton("Shutdown", "Shut the device down now?", () => _mesh.ShutdownAsync(5)));
        p.Children.Add(ActionButton("NodeDB reset", "Clear the device's node database? It will forget all known nodes.", () => _mesh.NodeDbResetAsync()));
        p.Children.Add(ActionButton("Factory reset", "FACTORY RESET the device? This erases ALL settings and channels and cannot be undone.", () => _mesh.FactoryResetAsync(), doubleConfirm: true));
        return Tab("Actions", p);
    }

    private async Task LoadAsync()
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
            // Module configs are secondary — if the device didn't report one, fall back to defaults so the tab
            // stays usable (rather than blocking the whole dialog on it).
            _mqtt = _mesh.GetMqttConfig() ?? new MQTTConfig();
            _neighbor = _mesh.GetNeighborInfoConfig() ?? new NeighborInfoConfig();
            _rangeTest = _mesh.GetRangeTestConfig() ?? new RangeTestConfig();
            _storeForward = _mesh.GetStoreForwardConfig() ?? new StoreForwardConfig();
            _owner = _mesh.GetOwner() ?? new User();
            // Only allow editing if we actually read the real config — otherwise a Save would overwrite the
            // device with defaults. Keep the tabs disabled and ask the user to retry.
            if (_device == null || _lora == null || _position == null || _telemetry == null)
            {
                _status.Text = "Could not read the full configuration from the device — editing is disabled. " +
                               "Close and try again (make sure the device is reachable).";
                return;
            }
            Populate();
            SetEnabled(true);
            _status.Text = "Settings loaded. Change a value and click its Save button.";
        }
        catch (Exception ex) { _status.Text = $"Could not read settings: {ex.Message}"; }
    }

    private void Populate()
    {
        _longName.Text = _owner!.LongName;
        _shortName.Text = _owner.ShortName;
        SelectRole(_device!.Role);
        _rebroadcast.SelectedItem = _device.RebroadcastMode;
        _nodeInfoSecs.Text = _device.NodeInfoBroadcastSecs.ToString();
        _screenOnSecs.Text = (_display?.ScreenOnSecs ?? 0).ToString();
        _ledDisabled.IsChecked = _mesh.GetLedHeartbeatDisabled();
        _licensed.IsChecked = _owner!.IsLicensed;

        _region.SelectedItem = _lora!.Region;
        _usePreset.IsChecked = _lora.UsePreset;
        _preset.SelectedItem = _lora.ModemPreset;
        _hopLimit.Text = _lora.HopLimit.ToString();
        _txEnabled.IsChecked = _lora.TxEnabled;
        _txPower.Text = _lora.TxPower.ToString();
        _channelNum.Text = _lora.ChannelNum.ToString();
        _overrideDuty.IsChecked = _lora.OverrideDutyCycle;
        _boostedGain.IsChecked = _lora.Sx126XRxBoostedGain;
        var extras = _mesh.GetLoRaExtras();   // ignore_mqtt / config_ok_to_mqtt / pa_fan_disabled (not in the bundled protobuf)
        _ignoreMqtt.IsChecked = extras.IgnoreMqtt;
        _okToMqtt.IsChecked = extras.OkToMqtt;
        _paFanDisabled.IsChecked = extras.PaFanDisabled;

        _posBroadcast.Text = _position!.PositionBroadcastSecs.ToString();
        _posSmart.IsChecked = _position.PositionBroadcastSmartEnabled;
        _fixedPos.IsChecked = _position.FixedPosition;
        _gpsInterval.Text = _position.GpsUpdateInterval.ToString();
        var pos = _mesh.GetOwnPosition();
        _latitude.Text = pos != null && pos.LatitudeI != 0 ? (pos.LatitudeI / 1e7).ToString("0.#######", CultureInfo.InvariantCulture) : "";
        _longitude.Text = pos != null && pos.LongitudeI != 0 ? (pos.LongitudeI / 1e7).ToString("0.#######", CultureInfo.InvariantCulture) : "";
        _altitude.Text = pos != null && pos.Altitude != 0 ? pos.Altitude.ToString() : "";

        _devUpdate.Text = _telemetry!.DeviceUpdateInterval.ToString();
        _envUpdate.Text = _telemetry.EnvironmentUpdateInterval.ToString();
        _envEnabled.IsChecked = _telemetry.EnvironmentMeasurementEnabled;
        _envScreen.IsChecked = _telemetry.EnvironmentScreenEnabled;
        _fahrenheit.IsChecked = _telemetry.EnvironmentDisplayFahrenheit;

        _mqttEnabled.IsChecked = _mqtt!.Enabled;
        _mqttAddress.Text = _mqtt.Address;
        _mqttUsername.Text = _mqtt.Username;
        _mqttPassword.Text = _mqtt.Password;
        _mqttRoot.Text = _mqtt.Root;
        _mqttEncryption.IsChecked = _mqtt.EncryptionEnabled;
        _mqttJson.IsChecked = _mqtt.JsonEnabled;
        _mqttTls.IsChecked = _mqtt.TlsEnabled;
        _mqttProxy.IsChecked = _mqtt.ProxyToClientEnabled;

        _niEnabled.IsChecked = _neighbor!.Enabled;
        _niInterval.Text = _neighbor.UpdateInterval.ToString();

        _rtEnabled.IsChecked = _rangeTest!.Enabled;
        _rtSender.Text = _rangeTest.Sender.ToString();
        _rtSave.IsChecked = _rangeTest.Save;

        _sfEnabled.IsChecked = _storeForward!.Enabled;
        _sfHeartbeat.IsChecked = _storeForward.Heartbeat;
        _sfRecords.Text = _storeForward.Records.ToString();
        _sfHistoryMax.Text = _storeForward.HistoryReturnMax.ToString();
        _sfHistoryWindow.Text = _storeForward.HistoryReturnWindow.ToString();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private sealed record RoleItem(int Value, string Label);

    private void BuildRoleItems()
    {
        var roles = new (int, string)[]
        {
            (0,"Client"),(1,"Client Mute"),(2,"Router"),(3,"Router Client"),(4,"Repeater"),(5,"Tracker"),
            (6,"Sensor"),(7,"TAK"),(8,"Client Hidden"),(9,"Lost and Found"),(10,"TAK Tracker"),(11,"Router Late"),(12,"Client Base"),
        };
        foreach (var (v, l) in roles) _role.Items.Add(new RoleItem(v, l));
        _role.DisplayMemberPath = nameof(RoleItem.Label);
    }

    private void SelectRole(Role role)
    {
        int v = (int)role;
        _role.SelectedItem = _role.Items.Cast<RoleItem>().FirstOrDefault(r => r.Value == v) ?? _role.Items[0];
    }

    private static void FillEnum(ComboBox cb, Array values) { foreach (var v in values) cb.Items.Add(v); }

    private UIElement SaveButton(string text, Func<Task<string?>> write)
    {
        var btn = new Button { Content = text, MinHeight = 26, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        btn.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; SetEnabled(false); _status.Text = "Writing…";
            try
            {
                string? err = await write();
                _status.Text = err == null ? "Saved to the device." : $"The radio rejected the change: {err}";
            }
            catch (Exception ex) { _status.Text = $"Write failed: {ex.Message}"; }
            finally { _busy = false; SetEnabled(true); }
        };
        return btn;
    }

    private UIElement ActionButton(string text, string confirm, Func<Task> action, bool doubleConfirm = false)
    {
        var btn = new Button { Content = text, MinHeight = 26, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        btn.Click += async (_, _) =>
        {
            if (_busy) return;
            if (!ThemedDialog.Confirm(this, confirm, text, defaultYes: true)) return;
            if (doubleConfirm && !ThemedDialog.Confirm(this, "Are you absolutely sure? This cannot be undone.", text, defaultYes: true)) return;
            _busy = true; SetEnabled(false); _status.Text = $"Sending {text}…";
            try { await action(); _status.Text = $"{text} sent."; }
            catch (Exception ex) { _status.Text = $"{text} failed: {ex.Message}"; }
            finally { _busy = false; SetEnabled(true); }
        };
        return btn;
    }

    private void SetEnabled(bool on) { _tabs.IsEnabled = on && !_busy; }

    private static StackPanel Section() => new() { Margin = new Thickness(10) };

    private static TabItem Tab(string header, UIElement content) =>
        new() { Header = header, Content = content, Foreground = Brushes.Black };   // black header text on the default light tab strip

    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var lbl = new TextBlock { Text = label, Foreground = Fg, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(lbl, Dock.Left);
        dp.Children.Add(lbl);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        dp.Children.Add(control);
        return dp;
    }

    private static TextBox Text(int width) => new() { MinWidth = width, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Left };
    private static ComboBox Combo(int width) => new() { MinWidth = width, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Left };
    private static CheckBox Check(string content) => new() { Content = content, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };

    private static uint ParseU(TextBox t, uint fallback) => uint.TryParse(t.Text.Trim(), out var v) ? v : fallback;
    private static int ParseI(TextBox t, int fallback) => int.TryParse(t.Text.Trim(), out var v) ? v : fallback;
}
