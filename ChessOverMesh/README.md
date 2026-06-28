# Chess over Meshtastic

A two-player console chess game in C# that sends and receives moves over a
Meshtastic mesh network using the device's **HTTP API**.

Each player runs the program against their own Meshtastic node (connected to WiFi).
Moves are exchanged as ordinary text messages on a shared channel, so you can even
watch them scroll by in the Meshtastic phone app.

## How it works

```
 Player (White)                 Mesh radio                  Player (Black)
 ┌──────────────┐   HTTP PUT    ┌──────────┐    LoRa     ┌──────────┐   HTTP GET   ┌──────────────┐
 │ ChessOverMesh │ ───toradio──▶ │  node A  │ ─ ─ ─ ─ ─ ▶ │  node B  │ ◀─fromradio─ │ ChessOverMesh │
 └──────────────┘               └──────────┘             └──────────┘              └──────────────┘
```

- **Transport** (`Mesh/MeshtasticHttpClient.cs`) — wraps the device REST endpoints
  `PUT /api/v1/toradio` and `GET /api/v1/fromradio`, serializing the same protobufs
  the device uses over BLE/Serial/TCP. It reuses the protobuf classes and message
  factories from the official Meshtastic C# library already in this repo
  (`../c-sharp-master/Meshtastic`).
- **Protocol** (`Game/Protocol.cs`) — moves are encoded as text:
  `CHX|<gameId>|MOVE|<ply>|<uci>` (e.g. `CHX|7f3a|MOVE|1|e2e4`) plus
  `CHX|<gameId>|RESIGN`. The `CHX` prefix and game id let the game ignore ordinary
  chatter and only react to its own moves; the ply counter dedupes the repeats and
  reordering that a mesh can produce.
- **Engine** (`Chess/`) — a full-rules move generator (castling, en passant,
  promotion, check / checkmate / stalemate). Validated with perft (matches the known
  node counts for the starting position through depth 4) and a Scholar's-mate test.

## Build

```powershell
dotnet build ChessOverMesh.csproj
```

Verify the chess engine:

```powershell
dotnet run --project ChessOverMesh.csproj -- --selftest
```

## GUI (WPF)

A Windows GUI front-end lives in the sibling `ChessOverMesh.Gui` project. It reuses
the same engine, mesh transport and protocol — it just replaces the console UI with a
clickable board.

```powershell
dotnet run --project ..\ChessOverMesh.Gui\ChessOverMesh.Gui.csproj
```

In the window:
1. Enter the device address (e.g. `http://192.168.1.50`) and click **Connect**. The
   attempt times out after 20s so you can retry; once connected the button becomes
   **Disconnect**.
2. Pick a **channel** (populated from the device — use the **⟳** button to re-fetch
   the channel list if any are missing) and a **role**.
3. Either **Create** a game or **Join…** a pending one (see below), then click a piece
   and a highlighted square to move. Promotions pop up a piece picker. The move list and
   a status line track the game; **Resign** ends it.

### Creating & joining games (lobby)

- **Role** can be **Auto** (default), **White**, or **Black**.
- **Create** announces a new game on the channel. With **Auto** you're assigned a random
  colour; with **White**/**Black** you get that colour. Everyone on the channel sees the
  announcement (e.g. *"meshtastic500 started game '8792' (they're White). Click Join… to
  play as Black."*) and **acknowledges** it — your create line shows **who acked it** (like
  chat). If **no one** acknowledges after the retries, creation **fails** and you're returned
  to the lobby to try again.
- **Join…** lists the open games announced on the channel; pick one and you're
  **automatically given the opposite colour** of its creator. Joining is a handshake: your
  JOIN is sent and the host must acknowledge it before you enter the game. If the host
  doesn't acknowledge (after retries), you get a **"no acknowledgement"** error and can try
  joining again.
- **Cancel** abandons a game (after a confirm) and returns you to the lobby, telling the channel so
  the other side leaves too. It's only available in the two moments a game can be safely called off:
  **after you create a game and are waiting for an opponent to join**, and **after you press Resign
  while waiting for the opponent to acknowledge**. Once the opponent has joined and play is underway,
  use **Resign** instead. Cancelling is courtesy-notified, not acknowledged — it always succeeds locally.

The board highlights the selected piece, legal destinations (dots), the last move, and a
king in check. When a **move or chat message arrives** while the app isn't focused, its
**taskbar button flashes** to get your attention (it stops when you click back to the app).

### Saving & loading games

- **Save…** (during a game) prompts for a filename and asks your opponent to save too. They
  get a prompt to accept; if they accept, both copies are written (to
  `%LOCALAPPDATA%\ChessOverMesh\saves\<name>.json`) and **the game ends**. The opponent
  **acknowledges** whether they saved — if they decline (or there's no ack), the save is
  cancelled and the game continues.
- **Loading** is part of starting a game. Pressing **Create** asks whether to **Create new game**
  or **Load existing game**; choosing *Load existing game* opens a file picker and **hosts the saved
  position** directly. The game announcement carries the **save's filename**, so the open game shows
  up in the lobby as *"…[resumes 'name']"* — and it can **only be joined by loading the same
  filename**. To join, open **Join…** and pick the game; if it's a resume game you're **automatically
  asked to open the matching file** before joining (a fresh game never asks). Loading the wrong file
  shows a "wrong saved game" prompt so you can retry. The save records the move list, **whose turn is
  next**, and **your colour**, so play **resumes from where it left off** (you keep your colours and
  the correct side to move).

### System messages & channel chat

The right panel has three stacked sections — **Moves**, **System messages**, and **Channel chat**.
Drag the divider between the board and the right panel to make it wider or narrower. You can
**select messages/moves and copy them** — right-click for **Copy / Copy all**, or select
(Ctrl/Shift for multiple) and press **Ctrl+C**.

Every move and message is prefixed with a **`[MM-dd HH:mm:ss]` receive timestamp**. For **incoming**
moves/messages it's the **radio's own receive time** (`rx_time` from the Meshtastic packet, converted to
local time) — the moment the device actually heard it, which is more accurate than when the app drained
it (notably during the post-connect backlog sync). If the device clock isn't set (`rx_time` is 0) it
falls back to this machine's local time, which is also used for things **you** send. The timestamp is
**never transmitted** — it's added locally on display — so each side shows when *it* received a message.

- **System messages** collects all game/lobby events — game created/joined/cancelled/over, who
  acknowledged your created game, lobby announcements, and save/resign notices — so they don't
  clutter the conversation.
Each list has an always-visible scrollbar and scrolls with the **mouse wheel** or by **dragging the
bar**. New entries only auto-scroll to the bottom when you're **already at the bottom** — if you scroll
up to read back, incoming moves/messages won't yank you down.

- **Channel chat** is reserved for actual chat. Anything you type in its box is sent on the selected
  channel as an ordinary Meshtastic text message (so it also shows up in the Meshtastic phone app),
  and incoming messages that aren't chess protocol appear here with the sender's name. Only traffic
  on the **currently selected channel** is shown — messages the device hears on other channels are
  ignored (matched on the packet's channel index). Chess
  traffic uses a `CHX|...` prefix, so it drives the board / system list instead of the chat.
  To avoid flooding a slow mesh, **only one chat message can be in flight at a time** — the **Send**
  button is disabled after you send and re-enables once the message is **acknowledged by any node**
  or it **times out** after the retries. You can keep composing your next message while you wait.
  Messages are also capped at **200 characters on the wire**; if a message (or its **AES base64
  ciphertext**, when a key is set) exceeds that, it's **not sent** and a **red warning** appears in
  the System messages list so you can shorten it.

### Encryption (optional)

The **Key** field is an optional AES-256 passphrase **for the selected channel**. When set,
every text payload (moves, chat, acks, game announcements) is **AES-256-CBC encrypted and
base64-encoded** before being sent, and decoded/decrypted on receipt — so it's gibberish on
the channel and in the Meshtastic app. The 256-bit key is derived from your passphrase
(SHA-256) with a fresh random IV per message. **Both players must use the same key.** Leave it
empty for plaintext. Messages that don't decrypt with your key (foreign/plain traffic) are
passed through unchanged. This is an app-layer layer on top of Meshtastic's own channel
encryption.

Keys are **remembered per device and per channel** — selecting a channel loads its saved key,
and editing the field saves it for that channel. Cached keys are **encrypted at rest with
Windows DPAPI** (scoped to your user account), so they're unreadable by other users or if the
cache file is copied to another machine. The app also remembers the **last host** you
connected to and pre-fills it on the next launch.

Cache files live in `%LOCALAPPDATA%\ChessOverMesh\` — `devices.json` (per-device channels,
node list and DPAPI-protected channel keys) and `settings.json` (last host).

### Delivery acknowledgement & retry

**Moves** use an explicit, application-level acknowledgement: when your opponent applies your
move, it broadcasts an `ACK` message back, and your move shows a ✓ once that ack arrives
("Opponent acknowledged move …"). This is more reliable than the mesh's own routing ack.

- If no ack arrives within **20 seconds**, the move is **resent** automatically.
- If it's still unacknowledged after the retry limit, you're **asked whether to resend it**. A move
  that's been made is **never reverted** — you either resend, or stop resending and leave it on the
  board (it can still be confirmed later).
- **Receiving the opponent's next move counts as an ack** (they could only reply if your move reached
  them): it marks your move ✓, and a separate `ACK` arriving afterwards is simply ignored.
- After you receive a move, sending your own move is held for **5 seconds** so the acknowledgement you
  send back finishes transmitting first (a short status note shows the remaining time if you try sooner).

**Resigning** is also acknowledged: clicking **Resign** sends the resignation and waits for the
opponent to acknowledge before ending the game. If it isn't acknowledged after two attempts you're
asked whether to **cancel the game anyway** — **Yes** abandons it (returning both sides to the
lobby), **No** keeps it on so you can resign or **Cancel** again.

**Chat** messages are also acknowledged at the application level: every recipient that
receives your chat replies with a `CHATACK`, and your message shows **who acknowledged it** by
name — e.g. *"You: hello ✓ acked by: Meshtastic ba30, KDRG"*. Node names come from the device's
node list, which is **cached per device** (populated on the first sync and refreshable via
**Nodes… → Update nodes**); if an acker isn't in the cache yet it shows as a `!hex` id until
the next node update. After you **receive** a chat message, sending your own is held for **5 seconds**
(the **Send** button greys out) so the `CHATACK` you send back transmits first.
A chat with no acknowledgement after two attempts is flagged ✗ (a later
ack still updates it).

The **first** time you connect to a device, the app fetches its channels (which triggers the
device's config dump) and **syncs** in the background — draining the queued packets so acks
stay timely — showing a live counter, e.g. *"Syncing with the mesh… 180 packets drained"*.
You can chat, start a game and make moves right away during this sync; the ack timer is
paused, so nothing false-fails — your moves just won't show their ✓ (and an opponent's moves
won't appear) until the drain catches up at **Ready**. This drain scales with how many nodes
the device has heard of, so on a busy mesh it can take a minute or two.

### Per-device cache (fast reconnect)

The channel list and node number are **remembered per device** (in
`%LOCALAPPDATA%\ChessOverMesh\devices.json`). On every **reconnect** the app uses that cache
and **skips the config dump entirely**, so the radio stays in live-packet mode and reception
works immediately — no sync wait. The one-time sync only happens the first time you connect
to a given radio (or when you refresh, below).

### Nodes (config dialog)

The **Nodes…** button opens a dialog listing every node the device knows (your own node is
starred). Because a cached reconnect skips the config dump, the node list starts empty —
click **Update nodes** to fetch it from the device (this re-runs the config dump, with a live
*"Updating… N nodes"* counter, and also refreshes the cached channels). Use the channel **⟳**
button if you only need to re-pull the channel list.

## Play (console)

On each player's machine:

```powershell
# White player (picks/owns the game id and shares it):
dotnet run --project ChessOverMesh.csproj -- --host http://192.168.1.50 --color white

# Black player (uses the same game id, opposite color):
dotnet run --project ChessOverMesh.csproj -- --host http://192.168.1.77 --color black --game 7f3a
```

Run with no arguments to be prompted for the host, color, and game id.

### Options

| Flag | Meaning |
|------|---------|
| `--host <url>`   | Base URL of the Meshtastic device HTTP API (e.g. `http://192.168.1.50`). `https://` self-signed certs are accepted. |
| `--color <c>`    | `white` or `black`. |
| `--game <id>`    | Shared game id. White picks one; both players must use the same id. |
| `--channel <n>`  | Meshtastic channel index to use. If omitted, the app lists the channels configured on the device after connecting and lets you pick one. Both players must use the same channel. |
| `--selftest`     | Run the engine self-test and exit. |

### In-game commands

```
e2e4        make a move (coordinate notation; add q/r/b/n to promote, e.g. e7e8q)
board       redraw the board
moves       list all legal moves
resign      resign the game
help        show help
quit        leave
```

## Requirements

- .NET 8 SDK
- A Meshtastic device with the HTTP/network API enabled and reachable over WiFi.
  Both players must be on the **same channel** with the **same game id** and
  **opposite colors**.

## Notes / limitations

- The mesh is broadcast and best-effort: moves are deduped by ply, but if a device
  is out of range you'll see "out-of-order" or "out of sync" warnings. Default LoRa
  channels are slow — expect a few seconds of latency per move.
- There's no draw-offer / threefold / 50-move handling yet (only resign and
  checkmate/stalemate end the game).
- Messages are not encrypted by this app beyond Meshtastic's own channel encryption.
