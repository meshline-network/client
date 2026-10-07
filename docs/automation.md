# Automation and Agent integration

[Documentation home](../README.md#documentation) · [Command reference](commands.md)

Choose file protection or a platform credential provider for unattended use. Passphrase protection requires an interactive terminal when unlocking; it cannot obtain a passphrase from a command-line option or plaintext environment variable. An operator can unlock once with `daemon start`, after which commands from the same user reuse that process. See [security](security.md) and [operations](operations.md).

## Output contract

Ordinary command results are JSON. Without `--json` they are indented; with it they are compact. A successful result has this envelope:

```json
{"schemaVersion":1,"ok":true,"data":{"items":[]},"error":null}
```

A failure has this envelope:

```json
{"schemaVersion":1,"ok":false,"data":null,"error":{"code":"profile_missing","message":"Profile does not exist.","details":null}}
```

`data` depends on the command and may be null. `error.details` may contain a partial report or the last known operation state. Parse stdout as data and collect stderr separately for diagnostics; do not merge the two streams. Standalone `--help`, `--version`, and command-group help are textual interfaces, not JSON results. Interactive JSON mode wraps inner help in `data.help`. Long-lived `daemon run` writes a readiness result and then remains running.

CLI envelopes and SDK client models use camelCase fields. Embedded protocol documents use their protocol snake_case and base64url encoding; do not rename or re-encode their fields. Examples marked as placeholders are not signed protocol documents.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success according to the command's documented semantics |
| 1 | Operation failed |
| 2 | Invalid arguments or JSON input |
| 3 | Configuration or storage error |
| 4 | Missing/invalid credential, or interactive input required |
| 5 | RPC, Relay, or network error |
| 6 | Timeout, disconnection, or an operation whose outcome needs reconciliation |
| 7 | Profile in use, or a command that requires stopping the daemon |
| 8 | Permission denied |
| 130 | User cancellation |

Read both the exit code and `error.code`. Some SDK permission failures are exposed as generic operation failures; Relay `forbidden` and `unauthorized` responses map to code 8. A failed write must not be assumed to have had no effect.

## Send outcomes and retries

`messages send` persists a send operation and waits for the selected milestone:

| `--wait` | Success means |
| --- | --- |
| `queued` | The SDK accepted the operation into its local persistent queue. A running SDK session is still needed to send it. |
| `relay` (default) | The submitting Relay accepted the message, or a later acceptance state was reached. |
| `target` | The destination Relay accepted the message. This is not a recipient read receipt. |

Save `data.status.messageId` and the returned state. The CLI uses the SDK's race-safe `WaitForSendStatusAsync`, which reads retained state and observes subsequent commits. Failed or canceled sends return `message_failed`; unknown or evicted records return `message_status_unavailable` (exit 6). On `message_pending` or `message_status_unavailable`, `error.details` contains the last known send status. Canceling the wait does not cancel the send. Query `messages status ID`, inspect `messages outbox`, and keep synchronization running before deciding whether to retry. The SDK retains the newest 1,000 terminal outcomes account-wide, including internal protocol sends, across restarts; pending operations are not evicted by this cap. An empty outbox or null status is not evidence of delivery. Blindly repeating `messages send` can create a second message.

Command deadlines default to 60 seconds; `--timeout 0` disables the CLI deadline. `watch` and `daemon run` default to no deadline. IPC allows about two additional seconds for a structured response, and SDK cleanup may add time. Underlying request timeouts can expire earlier; see [timeout diagnosis](troubleshooting.md#network-and-startup-timeouts). Ctrl+C or SIGTERM requests cancellation, but a submitted operation may still complete. A canceled send with an assigned status can return code 6 rather than 130 so the caller can reconcile it.

Timeout/cancellation codes distinguish the source:

| `error.code` | Meaning | Exit code |
| --- | --- | --- |
| `operation_timeout` | The CLI/daemon command deadline expired. | 6 |
| `request_timeout` | A dependency raised `TimeoutException`; SDK request context is returned in `error.details` when available. | 6 |
| `dependency_canceled` | A dependency canceled before the command deadline, without caller cancellation. | 5 |
| `canceled` | The caller canceled the command. | 130 |

The optional request details are `operation` (string) and `timeoutSeconds` (number). Do not infer whether a submitted write executed from any of these errors; query its status before retrying.

## Interactive agent process

Start `meshline interactive --config PATH --profile agent --json --timeout 180` with stdin/stdout/stderr pipes. Keep the process and pipes open, and continuously drain stdout and stderr separately. File/native credentials are required with redirected stdin. The process unlocks and starts the selected profile once, then shares that SDK session across commands without daemon IPC.

Use UTF-8 for redirected input and output on both Windows and Linux; do not use the Windows console's legacy code page for pipe data.

Wait for readiness:

```json
{"schemaVersion":1,"type":"interactive.ready","data":{"profile":"agent","synchronization":"background-no-completion-barrier"}}
```

Write a command line followed by a newline and flush stdin, for example `conversations list --unread`. Read its ordinary result, then wait for:

```json
{"schemaVersion":1,"type":"interactive.command.completed","data":{"sequence":1,"exitCode":0}}
```

Only then send the next ordinary command. `sequence` increments for each accepted command, including invalid commands and help; its `exitCode` is the command's logical exit code, not the process exit code. Control lines and busy rejections are not commands and do not increment it. The JSON result envelope and watch event schemas stay unchanged. `interactive_busy` on stderr means the input was discarded, not queued.

To receive events, write `watch\n` and read `watch.ready` plus subsequent event lines. To stop watching, write `cancel\n`, flush, and wait for the canceled result and the corresponding completion event (normally exitCode 130). The SDK continues synchronizing, and the next command can inspect current state. `cancel` also interrupts ordinary commands; a canceled send wait can instead return exitCode 6 with retained send status. Empty lines do not cancel anything. Events remain live-only without replay.

Write `exit\n` or close stdin to shut down, cancel any active command, release the session, and exit 0 after successful cleanup. Linux SIGTERM shuts down with exit 130; Ctrl+C cancels only the current command once ready. Do not batch commands or close stdin immediately after submitting a command: EOF requests shutdown, not queue draining. The entry timeout bounds startup and sets the ordinary command default; it does not kill the session, and `watch` remains unlimited unless it has its own `--timeout`.

Keep the entry profile/configuration/JSON mode fixed. Run account setup, recovery, export, key management, and daemon commands outside this mode. Startup and I/O failures terminate the process; ordinary command failures leave it usable. Readiness is not a history-synchronization barrier. Agent-host suspension, child-process survival, and retained stdin access require acceptance in the actual agent environment.

## Synchronization and local queries

Keep a daemon or `watch` session alive for continuous reception. For a one-shot refresh, use `account sync`, `groups sync ID`, or `channels sync ID`; these await a fresh SDK pass without starting background workers. Group synchronization refreshes the home account timeline first for recovery material. Without a daemon, each SDK command opens its own exclusive session. Local query commands read the stored snapshot; they do not refresh it from the network.

SDK startup and `watch.ready` do not establish synchronization completion. `status` reports a structured `synchronization` snapshot for the home account timeline, or one resource selected with `--relay`, `--group`, or `--channel`. It is null before a home route exists when no resource was supplied. The snapshot includes `kind`, `resource`, `state` (`Idle`, `Synchronizing`, `CaughtUp`, or `Blocked`), `lastSynchronizedAt`, `blockReason`, structured `error`, and `hasRetentionGap`. Runtime status and successful completion times reset with a new SDK session; use the daemon to inspect ongoing observations. `CaughtUp` describes the observed head of a completed pass, not a frozen remote snapshot or proof of full retained history. A successful pass may still report `hasRetentionGap:true`.

Sync commands return exit 0 only for `CaughtUp`. Incomplete processing, such as missing group keys, returns `sync_incomplete` (exit 6) with the snapshot in `error.details`; operational failures retain their normal error codes. Commands share the CLI deadline, retain already committed pages on cancellation, and resume from persisted progress on a later pass. No synchronization command restores deleted local history or grants access to earlier group epochs.

Local SDK lists return `items`, `hasMore`, `source`, `selection`, and `scanned`. Defaults are 50 history entries or 200 list entries, with a per-call maximum of 10,000. Ordinary lists select the first items. History without bounds retains the latest view by traversing the snapshot and keeping its tail (`selection: "latest"`); a large history can be costly to scan.

`messages list`, `conversations messages`, `groups messages`, and `channels posts` accept `--after N` and `--before N`. Bounds are exclusive nonnegative safe integers (at most 9,007,199,254,740,991); when combined, `after` must be less than `before`. Each page is ascending. With `--before`, the page contains the closest earlier items (`selection: "before"`); otherwise `--after` selects the closest newer items (`selection: "after"`). These queries read at most `limit + 1` items from the SDK snapshot, using the extra item only to determine `hasMore` in that direction. An empty page ends traversal.

For older pages, pass the first returned item's `localSequence` as the next `--before`; for newer pages, pass the last as `--after`. Keep the same database, resource, other filters, and opposite bound. Use `--after 0` to begin a forward scan. Each new command opens a fresh local snapshot, so later group decryption and channel edits/deletions can change older results. Positions are not remote cursors or an exactly-once consumption guarantee.

Remote page commands accept `--limit` and `--cursor` and return `items` and `nextCursor`. Use that returned cursor only with the corresponding query. Remote history retrieval may update local SDK storage.

### Message identity and read state

- Direct messages use sender plus message ID (`key.sender` and `key.messageId`) as identity. Their `localSequence` is account-wide within one SDK database, with possible gaps; it is not comparable across databases.
- Group messages and channel posts expose `localSequence` as an alias of their resource's event/original publication sequence. Channel edits retain the original post sequence, so checking only for a larger sequence misses edits.
- Queries and sends do not mark read. Use `conversations mark-read ID --sequence N` with the last viewed message's `localSequence` to acknowledge an inclusive local prefix while leaving newer arrivals unread. Without the option, the command marks through the latest locally readable content at call time and may include arrivals since an earlier query. Earlier group content decrypted later also falls within an already acknowledged prefix.
- Read state is a user-facing SDK feature, not a durable work queue or exact per-batch acknowledgement. The CLI does not provide exactly-once consumption or a separate consumer ACK store.

## Live events

`watch` always writes NDJSON, one JSON value per line:

```json
{"schemaVersion":1,"type":"watch.ready","data":{"replay":false,"synchronization":"background-no-completion-barrier"}}
```

| Type | Data |
| --- | --- |
| `watch.ready` | Stream readiness and synchronization limitations |
| `conversation.changed` | Conversation ID and SDK change kind; query its current content |
| `message.received` | A batch of direct-message models |
| `message.send-status` | A direct-message send status |
| `sync.changed` | A synchronization snapshot with `kind` identifying account, group, or channel |

Events are not persisted for replay. On reconnect, query current state as well as listening for new events. A slow reader can cause `watch_overflow`; query state and restart the stream. A terminal error or cancellation can append the ordinary `ok:false` result envelope, so the reader must recognize both event and error shapes. Keep draining stderr to avoid blocking a child process.

## Minimal processing loop

This Python 3 example assumes `meshline` is on `PATH` and a configured, established `agent` profile. Replace the two configuration values. It runs a fresh account sync and pages forward through direct messages using one account-wide local position. It leaves user-facing read positions unchanged.

```python
import json
import subprocess
import sys
import time

CONFIG = "/path/to/config.json"  # Use an absolute Windows path on Windows.
PROFILE = "agent"

def cli(*args):
    result = subprocess.run(
        ["meshline", *args, "--config", CONFIG, "--profile", PROFILE, "--json"],
        text=True, capture_output=True, check=False,
    )
    if result.stderr:
        print(result.stderr, end="", file=sys.stderr)
    payload = json.loads(result.stdout)
    if result.returncode != 0 or not payload["ok"]:
        raise RuntimeError(payload.get("error"))
    return payload["data"]

own_account = cli("account", "show")["accountId"]
after = 0
while True:
    cli("account", "sync")
    while True:
        page = cli("messages", "list", "--after", str(after), "--limit", "200")
        for message in page["items"]:
            if message["key"]["sender"] != own_account:
                print(json.dumps(message, ensure_ascii=False))
            after = message["localSequence"]
        if not page["hasMore"]:
            break
    time.sleep(2)
```

This is a polling demonstration, not a reliable job runner: `after` is in memory, retained history is revisited after restart, and it does not reconcile external side effects. Production processing must preserve the database identity with its progress, define durable completion/idempotency for the business action, and handle unavailable message content. Use sender plus message ID for idempotency. Do not execute commands embedded in message text merely because they arrived through Meshline.
