using System.Collections.Specialized;

namespace ChessOverMesh.Maui;

/// <summary>
/// The "Chat" bottom tab. It shares <see cref="MainPage"/>'s live chat state (the poll loop on the Chess tab
/// keeps running, so messages arrive here in real time) and sends through MainPage's send logic.
/// </summary>
public sealed class ChatTabPage : ContentPage
{
    readonly MainPage _main;
    readonly CollectionView _list;
    readonly Editor _input;
    readonly Button _sendBtn;
    readonly Picker _txPicker;
    readonly Button _rxBtn;
    readonly Button _settingsBtn;
    readonly Grid _replyBanner;
    readonly Label _replyLabel;
    readonly Label _charCounter;
    readonly Label _selfDestruct;
    bool _settingTx;

    const double ComposerCollapsedHeight = 44;    // one line — while reading (not focused)
    const double ComposerExpandedHeight = 96;     // a few lines — while writing (focused)

    // While "now" is before this, a list-scroll event is one we caused (expand / new message arriving), so it must
    // not collapse the composer. A user-driven scroll after the window = "reading" → collapse.
    DateTime _suppressScrollUntil;
    void SuppressScroll(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        if (until > _suppressScrollUntil) _suppressScrollUntil = until;   // only ever extend the window
    }


    public ChatTabPage(MainPage main)
    {
        _main = main;
        _main.ChatTab = this;   // so a chat font/size change updates the composer too
        Title = "Chat";
        BackgroundColor = Color.FromArgb("#1E1E1E");

        _list = new CollectionView
        {
            ItemsSource = _main.ChatLog,
            SelectionMode = SelectionMode.Single,   // used only to detect a tap on a message (see below); never kept selected
            ItemTemplate = new DataTemplate(() =>
            {
                var msg = new Label { FontAttributes = FontAttributes.Bold };
                msg.SetBinding(Label.TextProperty, nameof(LogEntry.Text));
                msg.SetBinding(Label.TextColorProperty, nameof(LogEntry.TextColor));
                msg.SetBinding(Label.FontFamilyProperty, nameof(LogEntry.FontFamily));
                msg.SetBinding(Label.FontSizeProperty, nameof(LogEntry.FontSize));
                // FontSize tracks the message (DetailFontSize = message size × a <1 factor) so it scales with the chat text setting.
                var detail = new Label { TextColor = Color.FromArgb("#8A8A8A") };
                detail.SetBinding(Label.TextProperty, nameof(LogEntry.Detail));
                detail.SetBinding(Label.FontSizeProperty, nameof(LogEntry.DetailFontSize));
                // Sender self-destruct countdown ("🕓 deletes in …"), dim; hidden when the message has no expiry.
                var expiry = new Label { TextColor = Color.FromArgb("#8A8A8A") };
                expiry.SetBinding(Label.TextProperty, nameof(LogEntry.Expiry));
                expiry.SetBinding(Label.FontSizeProperty, nameof(LogEntry.DetailFontSize));
                expiry.SetBinding(Label.IsVisibleProperty, new Binding(nameof(LogEntry.Expiry), converter: NotEmpty));
                // Emoji reactions (tapbacks) on this message, hidden when there are none.
                var reactions = new Label { FontSize = 16 };
                reactions.SetBinding(Label.TextProperty, nameof(LogEntry.Reactions));
                reactions.SetBinding(Label.IsVisibleProperty, new Binding(nameof(LogEntry.Reactions), converter: NotEmpty));
                var cell = new VerticalStackLayout { Padding = new Thickness(6, 4), Spacing = 1, Children = { msg, detail, expiry, reactions } };
                cell.SetBinding(IsVisibleProperty, nameof(LogEntry.Visible));   // the RX filter hides a channel/DM's rows
                cell.SetBinding(BackgroundColorProperty, nameof(LogEntry.RowBackground));   // unread received rows get a subtle yellow wash

                // Long-press a message → the same options as the desktop right-click menu. MAUI has no built-in
                // long-press gesture and PointerGestureRecognizer doesn't fire for finger touches on Android, so
                // hook the native view's LongClick directly. The handler reads the row's *current* binding context
                // at click time, which stays correct even as the CollectionView recycles cells.
#if ANDROID
                void OnLongClick(object? s, Android.Views.View.LongClickEventArgs e)
                {
                    e.Handled = true;
                    if (cell.BindingContext is LogEntry le)
                        MainThread.BeginInvokeOnMainThread(() => _ = ShowMessageMenuAsync(le));
                }
                // A short tap on the message counts as "reading the chat": clear the yellow unread wash (every row)
                // and collapse the composer. We handle the native Click because the LongClickable view above
                // intercepts touches, so a tap never reaches the CollectionView's selection (SelectionChanged) here.
                void OnClick(object? s, System.EventArgs e)
                    => MainThread.BeginInvokeOnMainThread(() =>
                    {
                        foreach (var le in _main.ChatLog) le.IsUnread = false;
                        CollapseComposer();
                    });
                cell.HandlerChanged += (_, _) =>
                {
                    if (cell.Handler?.PlatformView is Android.Views.View av)
                    {
                        av.LongClickable = true;
                        av.LongClick -= OnLongClick;   // avoid double-subscribing if the handler is re-created
                        av.LongClick += OnLongClick;
                        av.Clickable = true;
                        av.Click -= OnClick;           // ditto — tap clears the unread wash even when selection won't fire
                        av.Click += OnClick;
                    }
                };
#endif
                return cell;
            }),
        };
        // Tapping a message selects it — we use that purely as a reliable "tap on the RX window" signal to collapse
        // the composer (then immediately clear the selection so no row stays highlighted).
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem == null) return;
            _list.SelectedItem = null;
            foreach (var e in _main.ChatLog) e.IsUnread = false;   // pressing in the chat list marks every message read (clears the yellow wash)
            CollapseComposer();
        };
        // Fallback: scrolling the message list = "I'm reading" → collapse the composer. We ignore scrolls we caused
        // ourselves (expanding the box, or a new message auto-scrolling) via the suppression window.
        _list.Scrolled += (_, _) =>
        {
            if (DateTime.UtcNow < _suppressScrollUntil) return;
            if (_input.IsFocused || _input.Height > ComposerCollapsedHeight + 1) CollapseComposer();
        };

        _txPicker = new Picker
        {
            TextColor = Color.FromArgb("#E0E0E0"),
            BackgroundColor = Color.FromArgb("#1E1E1E"),
            FontSize = 13,
            Title = "Channel",
            ItemDisplayBinding = new Binding(nameof(MainPage.ChatTxTarget.Label)),
        };
        _txPicker.SelectedIndexChanged += (_, _) => { if (!_settingTx && _txPicker.SelectedItem is MainPage.ChatTxTarget t) _main.SetChatTx(t); };

        // Composer toggles between two fixed heights: one line while reading (so it doesn't eat the chat list) and
        // a few lines while writing. Longer text scrolls inside the box. (Two fixed heights are far more reliable
        // than runtime AutoSize toggling, which didn't actually shrink the Editor back down.)
        _input = new Editor
        {
            Placeholder = "Message…",
            TextColor = Color.FromArgb("#E0E0E0"),
            BackgroundColor = Color.FromArgb("#1E1E1E"),
            AutoSize = EditorAutoSizeOption.Disabled,
            HeightRequest = ComposerCollapsedHeight,          // one line until focused
            VerticalOptions = LayoutOptions.End,
        };
        _input.TextChanged += (_, _) => UpdateCharCounter();
        // Tapping the box = "writing" (expand + scroll once); losing focus = "reading" (collapse to one line).
        _input.Focused += (_, _) => ExpandComposer();
        _input.Unfocused += (_, _) => CollapseComposer();
        ApplyComposerFont(AppSettings.ChatFont ?? "", AppSettings.ChatSize);   // match the chat text setting

        // Live "<used> / <max> · <left> left" counter under the composer (matches the desktop GUI). Turns red over the limit.
        _charCounter = new Label { TextColor = Color.FromArgb("#B0B0B0"), FontSize = 11, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(0, 0, 4, 0) };
        // Self-destruct indicator: whether the current TX channel stamps outgoing messages with a self-destruct
        // lifetime, and how long. Sits left of the counter; hidden when the channel has no send-TTL.
        _selfDestruct = new Label { TextColor = Color.FromArgb("#E0A030"), FontSize = 11, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(4, 0, 0, 0), IsVisible = false };
        _sendBtn = new Button { Text = "Send", MinimumHeightRequest = 44, Padding = new Thickness(14, 0), VerticalOptions = LayoutOptions.End };
        _sendBtn.Clicked += async (_, _) => await SendAsync();

        // RX view filter: choose which channels/DMs are shown; the button shows total unread on hidden ones.
        _rxBtn = new Button { Text = "RX ▾", Padding = new Thickness(10, 0), MinimumHeightRequest = 36 };
        _rxBtn.Clicked += async (_, _) => { await Navigation.PushModalAsync(new RxFilterPage(_main)); };

        // Quick jump to Chat settings — where the per-channel self-destruct (and message retention) is set — so you
        // can check or change it without leaving the chat. Reuses MainPage's existing modal navigation.
        _settingsBtn = new Button { Text = "⚙", Padding = new Thickness(10, 0), MinimumHeightRequest = 36 };
        _settingsBtn.Clicked += (_, _) => _main.OpenChatSettings();

        var txRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 6 };
        txRow.Add(new Label { Text = "TX", TextColor = Color.FromArgb("#B0B0B0"), VerticalOptions = LayoutOptions.Center }, 0, 0);
        txRow.Add(_txPicker, 1, 0);
        txRow.Add(_rxBtn, 2, 0);
        txRow.Add(_settingsBtn, 3, 0);

        var composer = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 6 };
        composer.Add(_input, 0, 0);
        composer.Add(_sendBtn, 1, 0);

        // Reply banner: shows what the next send replies to, with an ✕ to cancel. Hidden unless replying.
        _replyLabel = new Label { TextColor = Color.FromArgb("#B0B0B0"), FontSize = 12, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        var cancelReply = new Button { Text = "✕", Padding = new Thickness(8, 0), MinimumHeightRequest = 32, BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#E0E0E0") };
        cancelReply.Clicked += (_, _) => _main.CancelReply();
        _replyBanner = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, BackgroundColor = Color.FromArgb("#252526"), Padding = new Thickness(6, 2), IsVisible = false };
        _replyBanner.Add(_replyLabel, 0, 0);
        _replyBanner.Add(cancelReply, 1, 0);

        var border = new Border { BackgroundColor = Color.FromArgb("#1A1A1A"), StrokeThickness = 0, Padding = 2, Content = _list };

        var root = new Grid
        {
            Padding = 8,
            RowSpacing = 6,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
        };
        // Counter row: self-destruct indicator on the left, character counter on the right.
        var counterRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 6 };
        counterRow.Add(_selfDestruct, 0, 0);
        counterRow.Add(_charCounter, 1, 0);

        root.Add(border, 0, 0);
        root.Add(txRow, 0, 1);
        root.Add(_replyBanner, 0, 2);
        root.Add(composer, 0, 3);
        root.Add(counterRow, 0, 4);
        Content = root;
        UpdateCharCounter();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        (Shell.Current as AppShell)?.OnChatTabShown();   // stop the tab flashing / clear the unread marker
        // Note: the per-message yellow wash is NOT cleared just by opening the tab — it persists until the user
        // presses in the chat list (SelectionChanged above) or replies on the channel, so unread stays visible.
        BackgroundConnection.ClearMessageNotifications();   // reading the chat dismisses any pending message alerts
        RebuildTx();   // channels may have changed while another tab was showing
        _main.ChatLog.CollectionChanged += OnChatChanged;
        _main.StateChanged += OnMainStateChanged;
        UpdateSendState();
        UpdateReplyBanner();
        UpdateRxButton();
        Dispatcher.Dispatch(ScrollToEnd);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        (Shell.Current as AppShell)?.OnChatTabHidden();
        _main.ChatLog.CollectionChanged -= OnChatChanged;
        _main.StateChanged -= OnMainStateChanged;
    }

    void OnMainStateChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        RebuildTx();        // DM targets can appear/disappear (e.g. a DM arrived, or a node was un-DM'd)
        UpdateSendState();
        UpdateReplyBanner();
        UpdateRxButton();
    });

    void UpdateRxButton()
    {
        int n = _main.TotalUnread;
        _rxBtn.Text = n > 0 ? $"RX ▾  ●{n}" : "RX ▾";
    }

    void UpdateReplyBanner()
    {
        var snip = _main.ReplyingTo;
        _replyBanner.IsVisible = snip != null;
        _replyLabel.Text = snip != null ? $"↳ Replying to: {snip}" : "";
    }

    // Live character counter: chars used / 200, plus how many remain. Counts the wire length actually transmitted
    // (AES-base64 ciphertext when the TX channel has an app key), matching the limit SendChatAsync enforces.
    void UpdateCharCounter()
    {
        int max = MainPage.MaxChatLength;
        int used = _main.ChatWireLength(_input.Text);
        int left = max - used;
        var split = _main.ChatSplitInfo(_input.Text);   // Parts>0 = will split; -1 = too long even split
        if (used > max && split.Parts > 0)
        {
            // Not an error — it'll be split. Headers mode: total on-air bytes (payload + text + each part's header)
            // out of the budget (MaxChatChunks × 200). Headers off: independent messages, so just show the count.
            _charCounter.Text = split.Headers
                ? $"{split.OnAirBytes} / {MainPage.MaxChatSplitBytes}  ·  {split.Parts} parts"
                : $"{used} chars · {split.Parts} messages";
            _charCounter.TextColor = Color.FromArgb("#B0B0B0");
        }
        else if (used > max && split.Parts < 0)
        {
            _charCounter.Text = "too long even split";
            _charCounter.TextColor = Color.FromArgb("#FF6B6B");
        }
        else
        {
            _charCounter.Text = $"{used} / {max}  ·  {left} left";
            _charCounter.TextColor = used > max ? Color.FromArgb("#FF6B6B") : Color.FromArgb("#B0B0B0");
        }
        UpdateSelfDestruct();
    }

    // Shows whether messages you send on the current TX channel self-destruct, and after how long, so it's clear
    // before hitting Send. Hidden when the channel has no send-TTL. The lifetime is per channel, so it tracks the TX
    // picker; changes made in Chat settings show on return (OnAppearing → RebuildTx → here).
    void UpdateSelfDestruct()
    {
        int ttl = _main.ChatSendTtlMinutes();
        _selfDestruct.IsVisible = ttl > 0;
        if (ttl > 0) _selfDestruct.Text = $"🔥 Self-destruct: {FormatTtl(ttl)}";
    }

    // A friendly duration for a self-destruct lifetime given in minutes: "5 min", "2 hours", "1 day", "1 h 30 min".
    static string FormatTtl(int minutes)
    {
        if (minutes <= 0) return "off";
        if (minutes % 1440 == 0) { int d = minutes / 1440; return $"{d} day{(d == 1 ? "" : "s")}"; }
        if (minutes % 60 == 0) { int h = minutes / 60; return $"{h} hour{(h == 1 ? "" : "s")}"; }
        if (minutes < 60) return $"{minutes} min";
        return $"{minutes / 60} h {minutes % 60} min";
    }

    // Sending is allowed only once the post-connect mesh sync has completed (so we don't send mid-sync), and not
    // while the last message is still awaiting confirmation. When blocked, the button greys out and shows a live
    // countdown of the seconds left (waiting for an ack/relay/reply, or the brief post-receive ack hold); a
    // confirmation cancels it early and re-enables Send.
    void UpdateSendState()
    {
        bool ready = _main.IsConnected && _main.IsSynced;
        int countdown = _main.ChatSendCountdown;
        bool blocked = ready && countdown > 0;
        _sendBtn.IsEnabled = ready && !blocked;
        _sendBtn.Text = blocked ? $"{countdown}s" : "Send";
        _input.Placeholder = ready ? "Message…"
            : _main.IsConnected ? "Syncing with the mesh…" : "Not connected";
    }

    void RebuildTx()
    {
        var items = _main.ChatTxTargets().ToList();
        var current = _main.CurrentChatTxTarget();
        _settingTx = true;
        _txPicker.ItemsSource = items;
        _txPicker.SelectedItem = items.FirstOrDefault(it => it.IsDm == current.IsDm && it.Id == current.Id) ?? items.FirstOrDefault();
        _settingTx = false;
        UpdateCharCounter();   // a different TX channel may have a different key, changing the wire length
    }

    void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollToEnd();
    }

    // "Writing" mode: grow the composer to a few lines and bring the newest message into view. Triggered only by
    // focusing (tapping) the text box.
    async void ExpandComposer()
    {
        SuppressScroll(1200);   // the box growing + keyboard opening will jiggle the list; don't treat that as "reading"
        _input.HeightRequest = ComposerExpandedHeight;
        await Task.Delay(250);   // let the keyboard + taller box settle before scrolling
        ScrollToEnd();
    }

    // "Reading" mode: force the composer back to a single line and dismiss the keyboard. Triggered by tapping a
    // message in the list, or by the box otherwise losing focus.
    void CollapseComposer()
    {
        if (_input.IsFocused) _input.Unfocus();               // drop focus + keyboard
        _input.HeightRequest = ComposerCollapsedHeight;       // always one line while reading
    }

    /// <summary>Applies the chat text font/size (from the "Chat text" setting) to the TX composer, so what you
    /// type matches what you'll see in the conversation. Called at construction and live from MainPage.ApplyChatFont.</summary>
    public void ApplyComposerFont(string family, double size)
    {
        _input.FontSize = size;
        _input.FontFamily = string.IsNullOrEmpty(family) ? null : family;
    }

    void ScrollToEnd()
    {
        var c = _main.ChatLog;
        if (c.Count == 0) return;
        SuppressScroll(400);   // this is our own scroll — don't let it collapse the composer
        _list.ScrollTo(c.Count - 1, position: ScrollToPosition.End, animate: false);
    }

    async Task SendAsync()
    {
        var reason = await _main.SendChatAsync(_input.Text ?? "");
        if (string.IsNullOrEmpty(reason))
        {
            _input.Text = "";       // accepted — clear the box
            CollapseComposer();     // and shrink it back to a single line (drops focus/keyboard, like reading mode)
        }
        else await ThemedDialogs.Alert(this, "Message not sent", reason, "OK");   // blocked — tell the user why
    }

    // Long-press menu on a chat message — mirrors the desktop right-click options.
    async Task ShowMessageMenuAsync(LogEntry le)
    {
        uint sender = le.SenderNode;   // sender node for node-addressed actions (set on live AND reloaded rows); 0 = none
        bool hasSender = sender != 0;
        bool canReply = (le.Rx?.PacketId ?? le.PacketId) != 0;
        var opts = new List<string>();
        if (canReply) opts.Add("Reply");
        if (canReply) opts.Add("React");
        if (hasSender) { opts.Add("DM"); opts.Add("Node info"); opts.Add("Open location in Google Maps"); }
        opts.Add("Request node info");   // always offered; if the row has no known sender we explain why on tap
        opts.Add("Message details");   // received: signal/relay; sent: who acked + RSSI/SNR/hops both ways
        opts.Add("Copy message");
        opts.Add("Remove message");

        string choice = await ThemedDialogs.ActionSheet(this, "Message", "Cancel", null, opts.ToArray());
        switch (choice)
        {
            case "Reply":
                _main.StartReply(le);   // links the next send + switches TX to the message's source
                _input.Focus();
                break;
            case "React":
                // Quick picks, plus "More…" which opens the phone's keyboard so any emoji can be chosen.
                var reactOpts = MainPage.ReactionEmojis.Append("More…").ToArray();
                string emoji = await ThemedDialogs.ActionSheet(this, "React", "Cancel", null, reactOpts);
                if (emoji == "More…")
                    emoji = await ThemedDialogs.Prompt(this, "React", "Type or pick any emoji from your keyboard:",
                                                     accept: "Send", cancel: "Cancel", placeholder: "🙂", maxLength: 16);
                if (!string.IsNullOrWhiteSpace(emoji) && emoji != "Cancel" && emoji != "More…")
                    await _main.ReactToAsync(le, emoji.Trim());
                break;
            case "DM" when sender != 0:
                await ThemedDialogs.Alert(this, "Direct message", _main.StartDmWith(sender), "OK");
                break;
            case "Request node info":
                if (sender != 0)
                    await ThemedDialogs.Alert(this, "Node info", await _main.RequestNodeInfoForAsync(sender), "OK");
                else
                {
                    _main.NoteNoSenderForNodeInfo();   // log a visible system message so it never fails silently
                    await ThemedDialogs.Alert(this, "Node info", MainPage.NoSenderForNodeInfoText, "OK");
                }
                break;
            case "Node info" when sender != 0:
                // Same full "all info" view as the Nodes list's "Show all info" button.
                var node = _main.GetNodes().FirstOrDefault(n => n.Num == sender);
                if (node == null)
                    await ThemedDialogs.Alert(this, "Node info", $"No node entry yet for !{sender:x8} — use \"Request node info\" first, then try again.", "OK");
                else
                    await Navigation.PushModalAsync(new TelemetryPage(_main, node));
                break;
            case "Open location in Google Maps" when sender != 0:
                var url = _main.NodeMapsUrl(sender);
                if (url == null)
                    await ThemedDialogs.Alert(this, "No location",
                        "No position data found for this node.\n\nIt hasn't shared its location yet. Use \"Request position\" to ask for it, then try again.", "OK");
                else
                    await Launcher.Default.OpenAsync(url);
                break;
            case "Message details":
                await ThemedDialogs.Alert(this, "Message details",
                    _main.MessageDetailsFor(le) ?? "No additional details for this message yet. Only messages received " +
                    "over the mesh, or sent messages that have been acknowledged, carry signal information.", "OK");
                break;
            case "Copy message":
                await Clipboard.SetTextAsync(MessageBody(le.Text ?? ""));
                break;
            case "Remove message":
                _main.RemoveCachedChat(le);   // also drop it from the per-channel cache so it won't return
                _main.ChatLog.Remove(le);
                break;
        }
    }

    // The body of a chat row's message line — everything after the "<sender>: " prefix
    // ("You → Bob: hi" → "hi"). Returns the whole string if there's no such prefix.
    static string MessageBody(string text)
    {
        int i = text.IndexOf(": ", StringComparison.Ordinal);
        return i >= 0 ? text[(i + 2)..] : text;
    }

    // Shows a bound element only when the string it's bound to is non-empty (used to hide the reactions line).
    static readonly NotEmptyConverter NotEmpty = new();
    sealed class NotEmptyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => !string.IsNullOrEmpty(value as string);
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
