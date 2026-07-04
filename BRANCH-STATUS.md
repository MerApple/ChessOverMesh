# Worktree / branch merge status

**Purpose.** This file is the source of truth for which worktree branches are already
integrated into `main`, so we never have to re-investigate "is this branch merged?" again.
When a branch is confirmed merged, **mark it here** (and, ideally, remove its stale worktree).

- Last verified: **2026-07-04** (re-verified for the 1.4.7 release)
- `main` at time of verification: **`01d0024`** (`Release 1.4.6` + connection-lost-reason + MAUI node-info button auto-sizing, about to be tagged `1.4.7`)

Re-verified against the moved `main`: every branch below is still merged/no-op except the
two obsolete ones. New since 1.4.6 and now on `main`: the connection-lost message shows *why*
the link dropped, and the MAUI "Node info" action buttons auto-size to their text.

## How status is determined

Two checks, run against `main`:

1. **In history** — `git merge-base --is-ancestor <branch> main` succeeds → every commit on the
   branch is already in `main`.
2. **Content-equivalent** — a dry-run merge produces an empty diff
   (`git merge --no-ff --no-commit <branch>` → `git diff --cached` empty → `git merge --abort`).
   The branch's *commits* aren't in `main` verbatim (it was based on an older `main` and the
   feature was re-implemented/squashed), but its *content* already is. Safe to treat as merged.

A branch is **NOT merged** only if a dry-run merge introduces real changes or conflicts.

## ✅ Confirmed merged into main (32) — safe to ignore / delete worktree

### In history (ancestor of main) — 29
| Branch |
|---|
| worktree-nodeinfo-dump-suppress *(merged 2026-07-04)* |
| worktree-resizable-windows |
| worktree-dm-message-color |
| worktree-dm-split-headers |
| worktree-map-cursor-coords |
| worktree-map-settings-resizable |
| worktree-noise-settings |
| worktree-offline-map-cache |
| worktree-positions-per-node-setting |
| worktree-remove-sysmsg-setting |
| done/menu-uitextsize |
| done/worktree-auto-delete-messages |
| done/worktree-chat-detail-scale |
| done/worktree-chunked-messages |
| done/worktree-clear-pw-on-cache-clear |
| done/worktree-drop-hopsaway-cache-signal |
| done/worktree-gui-chat-lock |
| done/worktree-mask-app-key |
| done/worktree-maui-update-channel |
| done/worktree-move-show-chessboard |
| done/worktree-node-position-history |
| done/worktree-node-track-button |
| done/worktree-proxy-multiuser-auth |
| done/worktree-require-current-pw-on-remove |
| done/worktree-selfdestruct-indicator |
| done/worktree-tx-channel-lock |
| done/worktree-unread-highlight |
| done/worktree-window-size-memory |
| done/wt-pos-15m |

### Content already in main (no-op merge) — 3
These branches show commits "ahead" of `main`, but their content is already present (feature
re-implemented in `main`). A merge changes nothing.
| Branch | Note |
|---|---|
| worktree-telemetry-system-messages | own-broadcast / hop_start gating — now lives in `MeshtasticHttpClient.cs` |
| worktree-map-node-track | superseded by `CaptureNodeDbPositionsToHistory()` in `MeshtasticHttpClient.cs` |
| worktree-map-provider-text-contrast | tile-provider ComboBox contrast already applied |

## ❌ NOT merged — superseded, decide & delete (2)

These are old branches whose feature already exists in `main` via a *different*
implementation, so a merge **conflicts**. Do not merge as-is; they are almost certainly
obsolete and should be deleted after a glance.
| Branch | Conflicts | Why it's obsolete |
|---|---|---|
| worktree-outgoing-system-messages | 3 files | `OwnBroadcast` outgoing-broadcast logging already in `main` |
| worktree-noise-calibration-display | 1 file | raw-alongside-calibrated noise already in `main` (`noise + NoiseCalibrationFor(...)`) |

## TODO / practice

- [ ] **Mark every branch confirmed merged** in this file (done above) and keep it current.
- [ ] Prune the 32 merged worktrees/branches when convenient:
      `git worktree remove <path>` then `git branch -D <branch>`.
- [ ] Resolve the 2 superseded branches: confirm obsolete, then delete (same commands).
- [ ] Re-run the verification below whenever `main` moves or new worktrees appear, and update
      this file.

### Re-verify command
```bash
# Branches with commits not yet in main (candidates to check):
for b in $(git for-each-ref --format='%(refname:short)' refs/heads/ | grep -v '^main$'); do
  ahead=$(git rev-list --count main..$b)
  if git merge-base --is-ancestor "$b" main; then echo "MERGED     $b"
  elif [ "$ahead" -gt 0 ]; then echo "CHECK($ahead)   $b"; fi
done
# For each CHECK branch, dry-run merge on a clean tree:
#   git merge --no-ff --no-commit <b>; git diff --cached --stat; git merge --abort
# empty diff => content already in main (merged-equivalent); conflict/real diff => NOT merged.
```
