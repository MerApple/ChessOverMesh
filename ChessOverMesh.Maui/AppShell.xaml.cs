namespace ChessOverMesh.Maui;

public partial class AppShell : Shell
{
	const string ChatTitle = "Chat";
	const string ChatAlert = "Chat ●";   // unread marker shown on the bottom tab
	const string ChessTitle = "Chess";
	const string SystemTabTitle = "System messages";   // shown on the chess tab when the board is hidden
	const string ChatRoute = "chat";     // the Chat tab's shell route, used by RequestChatTab's GoToAsync

	readonly ShellContent _chessTab;
	readonly ShellContent _chatTab;
	IDispatcherTimer? _flashTimer;
	int _flashTicks;
	bool _onChatTab;

	// The live Shell instance, so a notification tap (handled in MainActivity) can route to the Chat tab. Set in the
	// constructor; there's only ever one AppShell. A tap that arrives before the Shell is built sets _pendingChatNav,
	// applied on the first navigation.
	static AppShell? _instance;
	static bool _pendingChatNav;

	/// <summary>Whether the Chat tab is the one currently showing (used to decide if an incoming message is "unread").</summary>
	public bool ChatTabVisible => _onChatTab;

	public AppShell()
	{
		InitializeComponent();

		// MainPage owns the device connection, poll loop and game state. The Chat tab shares that live state via
		// a reference to the same MainPage instance, so both tabs stay in sync without a separate session service.
		var main = new MainPage();

		var tabs = new TabBar();
		tabs.Items.Add(new ShellContent { Title = "Device", Content = new DeviceTabPage(main) });
		_chessTab = new ShellContent { Title = AppSettings.ShowChessboard ? ChessTitle : SystemTabTitle, Content = main };
		tabs.Items.Add(_chessTab);
		_chatTab = new ShellContent { Title = ChatTitle, Content = new ChatTabPage(main), Route = ChatRoute };
		tabs.Items.Add(_chatTab);
		tabs.Items.Add(new ShellContent { Title = "Settings", Content = new SettingsTabPage(main) });
		Items.Add(tabs);

		_instance = this;
	}

	/// <summary>Show the Chat tab. Called (via MainActivity) when the user taps a message notification, so they land
	/// on the message instead of the last-shown / disconnected Device tab. If the Shell isn't built yet (a cold start
	/// from the notification), the request is deferred and applied on the first navigation.</summary>
	public static void RequestChatTab()
	{
		var inst = _instance;
		if (inst == null) { _pendingChatNav = true; return; }
		inst.Dispatcher.Dispatch(async () => await inst.GoToChatTabAsync());
	}

	async Task GoToChatTabAsync()
	{
		try { await GoToAsync("//" + ChatRoute); }
		catch { /* best effort — worst case we stay on the current tab */ }
	}

	protected override void OnNavigated(ShellNavigatedEventArgs args)
	{
		base.OnNavigated(args);
		// Apply a nav requested before the Shell was ready (cold start from a notification tap).
		if (_pendingChatNav)
		{
			_pendingChatNav = false;
			Dispatcher.Dispatch(async () => await GoToChatTabAsync());
		}
	}

	/// <summary>Updates the chess tab's title from the "Show chessboard" setting — "Chess" when the board is
	/// shown, "System messages" when it's hidden. Called live from the System settings switch.</summary>
	public void RefreshChessTabTitle() =>
		_chessTab.Title = AppSettings.ShowChessboard ? ChessTitle : SystemTabTitle;

	/// <summary>Flashes the Chat tab label (then leaves a "●" marker) to draw attention to a new message — unless
	/// the Chat tab is already the one being viewed.</summary>
	public void FlashChatTab()
	{
		if (_onChatTab) return;
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_flashTimer ??= MakeFlashTimer();
			_flashTicks = 0;
			_flashTimer.Stop();
			_flashTimer.Start();
		});
	}

	IDispatcherTimer MakeFlashTimer()
	{
		var t = Dispatcher.CreateTimer();
		t.Interval = TimeSpan.FromMilliseconds(450);
		t.Tick += (_, _) =>
		{
			_flashTicks++;
			_chatTab.Title = (_flashTicks % 2 == 1) ? ChatAlert : ChatTitle;
			if (_flashTicks >= 7) { _chatTab.Title = ChatAlert; t.Stop(); }   // settle on a persistent unread badge
		};
		return t;
	}

	/// <summary>Called by the Chat tab when it becomes visible: stop flashing and clear the unread marker.</summary>
	public void OnChatTabShown()
	{
		_onChatTab = true;
		_flashTimer?.Stop();
		_chatTab.Title = ChatTitle;
	}

	/// <summary>Called by the Chat tab when it's hidden.</summary>
	public void OnChatTabHidden() => _onChatTab = false;
}
