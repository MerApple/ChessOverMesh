using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// The "Device" bottom tab (leftmost). It owns the connection UI — connect over WiFi (HTTP) or, experimentally,
/// over Bluetooth LE — while the connection itself lives on <see cref="MainPage"/> (driven via
/// <c>ConnectToAsync</c> / <c>ConnectViaTransportAsync</c> / <c>DisconnectDevice</c>). After connecting it shows
/// the device's firmware version and battery voltage, like the native Meshtastic app, refreshing whenever
/// MainPage's connection state changes.
/// </summary>
public sealed class DeviceTabPage : ContentPage
{
    readonly MainPage _main;
    readonly Entry _host;
    readonly Button _wifiBtn, _findBtn, _disconnectBtn, _scanBtn, _bleBtn, _deviceSettingsBtn;
    readonly Picker _blePicker;
    readonly Label _status, _sync, _name, _hardware, _firmware, _battery;
    IReadOnlyList<BleDeviceInfo> _bleDevices = Array.Empty<BleDeviceInfo>();
    bool _busy;

    static readonly Color Dim = Color.FromArgb("#B0B0B0");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Rule = Color.FromArgb("#3F3F46");
    static readonly Color SyncProgress = Color.FromArgb("#7FB2E5");   // syncing (blue)
    static readonly Color SyncDone = Color.FromArgb("#5BBA5B");       // sync complete (green)
    static readonly Color SyncError = Color.FromArgb("#E57373");      // sync failed (red)

    public DeviceTabPage(MainPage main)
    {
        _main = main;
        Title = "Device";
        BackgroundColor = Color.FromArgb("#1E1E1E");

        _status = new Label { Text = "Not connected.", TextColor = Dim, FontSize = 12 };
        _disconnectBtn = new Button { Text = "Disconnect", HeightRequest = 44, Padding = new Thickness(14, 0), IsVisible = false };
        _disconnectBtn.Clicked += OnDisconnect;

        // ---- Connected-device info ----
        _name = InfoValue(); _hardware = InfoValue(); _firmware = InfoValue(); _battery = InfoValue();
        var info = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
            },
            ColumnSpacing = 10, RowSpacing = 6,
        };
        AddInfoRow(info, 0, "Device", _name);
        AddInfoRow(info, 1, "Hardware", _hardware);
        AddInfoRow(info, 2, "Firmware", _firmware);
        AddInfoRow(info, 3, "Battery", _battery);

        // Mesh-sync progress (moved here from the Chess status line).
        _sync = new Label { TextColor = Color.FromArgb("#7FB2E5"), FontSize = 12, IsVisible = false };

        // Opens the device configuration editor (owner/role, LoRa, position, telemetry, reboot/resets) — the
        // MAUI equivalent of the desktop "Device settings…" window. Enabled once connected and synced.
        _deviceSettingsBtn = new Button { Text = "Device settings…", HeightRequest = 44, Padding = new Thickness(14, 0), IsVisible = false };
        _deviceSettingsBtn.Clicked += (_, _) => _main.OpenDeviceSettings();

        // ---- WiFi (HTTP) ----
        _host = new Entry
        {
            Text = string.IsNullOrWhiteSpace(AppSettings.LastHost) ? "http://192.168.2.183" : AppSettings.LastHost,
            TextColor = Fg, Keyboard = Keyboard.Url, Placeholder = "http://192.168.x.x",
        };
        _findBtn = new Button { Text = "Find", HeightRequest = 40, Padding = new Thickness(12, 0) };
        _findBtn.Clicked += OnFind;
        var hostRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 6,
        };
        hostRow.Add(new Label { Text = "Host", TextColor = Dim, VerticalOptions = LayoutOptions.Center }, 0, 0);
        hostRow.Add(_host, 1, 0);
        hostRow.Add(_findBtn, 2, 0);
        _wifiBtn = new Button { Text = "Connect over WiFi", HeightRequest = 44, Padding = new Thickness(14, 0) };
        _wifiBtn.Clicked += OnWifiConnect;

        // ---- Bluetooth LE (experimental) ----
        _scanBtn = new Button { Text = "Scan", HeightRequest = 40, Padding = new Thickness(12, 0) };
        _scanBtn.Clicked += OnScan;
        _blePicker = new Picker
        {
            Title = "Discovered radios", TextColor = Fg, BackgroundColor = Color.FromArgb("#1E1E1E"),
            ItemDisplayBinding = new Binding(nameof(BleDeviceInfo.Name)),
        };
        _bleBtn = new Button { Text = "Connect over Bluetooth", HeightRequest = 44, Padding = new Thickness(14, 0) };
        _bleBtn.Clicked += OnBleConnect;

        var stack = new VerticalStackLayout { Padding = 16, Spacing = 12 };
        stack.Add(new Label { Text = "Device", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        stack.Add(_status);
        stack.Add(_disconnectBtn);
        stack.Add(new Label { Text = "Connected device", TextColor = Fg, FontAttributes = FontAttributes.Bold });
        stack.Add(info);
        stack.Add(_sync);
        stack.Add(_deviceSettingsBtn);
        stack.Add(new BoxView { HeightRequest = 1, Color = Rule, Margin = new Thickness(0, 6) });
        stack.Add(new Label { Text = "WiFi (HTTP)", TextColor = Fg, FontAttributes = FontAttributes.Bold });
        stack.Add(hostRow);
        stack.Add(_wifiBtn);
        stack.Add(new BoxView { HeightRequest = 1, Color = Rule, Margin = new Thickness(0, 6) });
        stack.Add(new Label { Text = "Bluetooth", TextColor = Fg, FontAttributes = FontAttributes.Bold });
        stack.Add(new Label { Text = "Scan for nearby radios, pick one, then connect. May prompt to pair (PIN).", TextColor = Dim, FontSize = 12 });
        stack.Add(_scanBtn);
        stack.Add(_blePicker);
        stack.Add(_bleBtn);
        stack.Add(new BoxView { HeightRequest = 1, Color = Rule, Margin = new Thickness(0, 6) });

        // Opt-in auto-reconnect: if the device drops (e.g. the phone slept), retry once a minute until it returns.
        var arSwitch = new Switch { IsToggled = AppSettings.AutoReconnect, VerticalOptions = LayoutOptions.Center };
        arSwitch.Toggled += (_, e) => { AppSettings.AutoReconnect = e.Value; if (!e.Value) _main.CancelAutoReconnect(); };
        var arRow = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        arRow.Add(arSwitch);
        arRow.Add(new Label { Text = "Auto-reconnect", TextColor = Fg, VerticalOptions = LayoutOptions.Center });
        stack.Add(arRow);
        stack.Add(new Label { Text = "If the connection drops, retry once a minute until it reconnects (or you tap " +
            "Stop reconnecting).", TextColor = Dim, FontSize = 12 });

        // Opt-in background poll: while the phone sleeps, check the device for new messages every ~15 minutes.
        var bgSwitch = new Switch { IsToggled = AppSettings.BackgroundPoll, VerticalOptions = LayoutOptions.Center };
        bgSwitch.Toggled += (_, e) => { AppSettings.BackgroundPoll = e.Value; BackgroundPoll.Apply(); };
        var bgRow = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        bgRow.Add(bgSwitch);
        bgRow.Add(new Label { Text = "Background message check", TextColor = Fg, VerticalOptions = LayoutOptions.Center });
        stack.Add(bgRow);
        stack.Add(new Label { Text = "While the phone is asleep, check the device for new messages about every 15 " +
            "minutes and notify you. Not real-time — Android limits how often this can run.", TextColor = Dim, FontSize = 12 });

        Content = new ScrollView { Content = stack };

        // MainPage raises StateChanged from ApplyConnectionState (connect, disconnect, and after each sync),
        // so the readout below stays current as telemetry arrives.
        _main.StateChanged += OnStateChanged;
    }

    static Label InfoValue() => new() { Text = "—", TextColor = Fg, VerticalOptions = LayoutOptions.Center };

    static void AddInfoRow(Grid grid, int row, string label, Label value)
    {
        grid.Add(new Label { Text = label, TextColor = Dim, VerticalOptions = LayoutOptions.Center }, 0, row);
        grid.Add(value, 1, row);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    void OnStateChanged() => MainThread.BeginInvokeOnMainThread(Refresh);

    // Find a device on the local network by resolving the Meshtastic mDNS hostname (meshtastic.local) — the same
    // approach as the desktop app. Android 12+ resolves ".local" names via the system mDNS resolver.
    async void OnFind(object? sender, EventArgs e)
    {
        if (_busy || _main.IsConnected || _main.IsConnecting) return;
        _busy = true; _status.Text = "Searching for a device (meshtastic.local)…"; Refresh();
        try
        {
            var ip = await ResolveMeshtasticAsync();
            if (ip == null)
            {
                _status.Text = "No device found — enter the IP manually.";
                await DisplayAlert("No device found",
                    "No Meshtastic device answered at meshtastic.local.\n\nCheck the device has WiFi enabled and is " +
                    "on the same network, then try Find again — or type its IP in the Host box.", "OK");
            }
            else
            {
                _host.Text = $"http://{ip}";
                _status.Text = $"Found a device at {ip} — tap Connect over WiFi.";
            }
        }
        catch (Exception ex) { _status.Text = $"Search failed: {ex.Message}"; }
        finally { _busy = false; Refresh(); }
    }

    static async Task<string?> ResolveMeshtasticAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var addrs = await System.Net.Dns.GetHostAddressesAsync("meshtastic.local", cts.Token);
            foreach (var a in addrs)
                if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return a.ToString();
        }
        catch { /* not found / mDNS unavailable */ }
        return null;
    }

    async void OnWifiConnect(object? sender, EventArgs e)
    {
        if (_busy || _main.IsConnected || _main.IsConnecting) return;
        var host = (_host.Text ?? "").Trim();
        if (host.Length == 0) { await DisplayAlert("Host required", "Enter the device's address, e.g. http://192.168.1.50", "OK"); return; }
        _busy = true; _status.Text = "Connecting over WiFi…"; Refresh();
        try
        {
            // This is an async void handler — an uncaught exception here would crash the app, so catch it and
            // show the reason instead.
            var msg = await _main.ConnectToAsync(host);
            _status.Text = msg;
            if (!_main.IsConnected) await DisplayAlert("Not connected", msg, "OK");
        }
        catch (Exception ex)
        {
            _status.Text = "Connect error.";
            await DisplayAlert("Connect error", ex.ToString(), "OK");
        }
        finally { _busy = false; Refresh(); }
    }

    async void OnScan(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (!BleScanner.IsAvailable) { await DisplayAlert("Bluetooth", "This device has no Bluetooth LE adapter.", "OK"); return; }
        if (!await BleScanner.EnsurePermissionsAsync()) { await DisplayAlert("Permission needed", "Bluetooth permission was denied.", "OK"); return; }
        if (!BleScanner.IsOn) { await DisplayAlert("Bluetooth off", "Turn Bluetooth on, then scan again.", "OK"); return; }

        _busy = true; _status.Text = "Scanning for Meshtastic radios…"; Refresh();
        try
        {
            _bleDevices = await BleScanner.ScanAsync(TimeSpan.FromSeconds(8));
            _blePicker.ItemsSource = _bleDevices.ToList();
            if (_bleDevices.Count > 0) { _blePicker.SelectedIndex = 0; _status.Text = $"Found {_bleDevices.Count} radio(s)."; }
            else _status.Text = "No Meshtastic radios found. Make sure the radio is on and advertising.";
        }
        catch (Exception ex) { _status.Text = "Scan failed."; await DisplayAlert("Scan failed", ex.Message, "OK"); }
        finally { _busy = false; Refresh(); }
    }

    async void OnBleConnect(object? sender, EventArgs e)
    {
        if (_busy || _main.IsConnected || _main.IsConnecting) return;
        if (_blePicker.SelectedItem is not BleDeviceInfo pick) { await DisplayAlert("Pick a radio", "Scan, then choose a radio to connect to.", "OK"); return; }

        _busy = true; _status.Text = $"Connecting to {pick.Name} over Bluetooth…"; Refresh();
        IMeshTransport? transport = null;
        try
        {
            transport = await BleMeshTransport.ConnectAsync(pick.Device);
            // Pass a rebuild delegate so auto-reconnect can re-establish the BLE link if it drops.
            var msg = await _main.ConnectViaTransportAsync(transport, pick.Name, "ble:" + pick.Device.Id,
                rebuild: async () => (IMeshTransport)await BleMeshTransport.ConnectAsync(pick.Device));
            _status.Text = msg;
            if (!_main.IsConnected) await DisplayAlert("Not connected", msg, "OK");
        }
        catch (Exception ex)
        {
            transport?.Dispose();
            _status.Text = "Bluetooth connect failed.";
            await DisplayAlert("Bluetooth connect failed", ex.Message, "OK");
        }
        finally { _busy = false; Refresh(); }
    }

    void OnDisconnect(object? sender, EventArgs e)
    {
        if (_main.IsAutoReconnecting) { _main.CancelAutoReconnect(); _status.Text = "Auto-reconnect cancelled."; Refresh(); return; }
        if (!_main.IsConnected) return;
        _main.DisconnectDevice();
        _status.Text = "Disconnected.";
        Refresh();
    }

    void Refresh()
    {
        bool connected = _main.IsConnected;
        bool reconnecting = _main.IsAutoReconnecting;
        bool idle = !connected && !reconnecting && !_main.IsConnecting && !_busy;

        // While auto-reconnecting, show the button as a cancel for the retry loop.
        _disconnectBtn.IsVisible = connected || reconnecting;
        _disconnectBtn.IsEnabled = (connected || reconnecting) && !_busy;
        _disconnectBtn.Text = reconnecting
            ? (_main.ReconnectCountdown > 0 ? $"Stop reconnecting ({_main.ReconnectCountdown}s)" : "Stop reconnecting")
            : "Disconnect";
        _host.IsEnabled = idle;
        _wifiBtn.IsEnabled = idle;
        _findBtn.IsEnabled = idle;
        _scanBtn.IsEnabled = idle;
        _blePicker.IsEnabled = idle && _bleDevices.Count > 0;
        _bleBtn.IsEnabled = idle && _bleDevices.Count > 0;

        // Device settings need a live, fully-synced connection (the editor fetches the real config on open).
        _deviceSettingsBtn.IsVisible = connected;
        _deviceSettingsBtn.IsEnabled = connected && _main.IsSynced && !_busy;

        if (!connected)
        {
            _name.Text = _hardware.Text = _firmware.Text = _battery.Text = "—";
            _sync.IsVisible = false;
            return;
        }
        _name.Text = _main.DeviceName ?? "—";
        _hardware.Text = _main.HardwareModel ?? "—";
        _firmware.Text = _main.FirmwareVersion ?? "(reported after sync)";
        _battery.Text = FormatBattery(_main.DeviceBattery);
        _sync.Text = _main.SyncStatus;
        _sync.IsVisible = !string.IsNullOrEmpty(_main.SyncStatus);
        _sync.TextColor = _main.SyncStatus.StartsWith("Synced", StringComparison.Ordinal) ? SyncDone
                        : _main.SyncStatus.StartsWith("Sync error", StringComparison.Ordinal) ? SyncError
                        : SyncProgress;   // still syncing
    }

    static string FormatBattery((int Percent, float Voltage)? b)
    {
        if (b is not { } v) return "(reported after sync)";
        string pct = v.Percent > 100 ? "powered (USB)" : $"{v.Percent}%";
        return v.Voltage > 0 ? $"{pct} · {v.Voltage:0.00} V" : pct;
    }
}
