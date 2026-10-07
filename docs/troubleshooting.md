# Troubleshooting

[Documentation home](../README.md#documentation) · [Error and exit-code contract](automation.md#output-contract)

Start with the least invasive checks. With `meshline` on `PATH`, replace configuration/profile values as appropriate:

```sh
meshline config show --config ./config.json --json
meshline account list --config ./config.json --json
meshline account show --config ./config.json --profile agent --json
meshline doctor --config ./config.json --profile agent --json
meshline daemon status --config ./config.json --profile agent --json
```

Preserve stderr and the structured error, including `error.details`. A diagnostic command can fail while still returning a useful partial report. Avoid sharing credential files, wallets, or private data directories with a bug report.

## Configuration and profiles

| Symptom | Check and action |
| --- | --- |
| `config_missing` | Restore the bundled configuration or use `--config` with an existing file. The default is beside the application, not in the working directory. |
| Unexpected account selected | Check `defaultProfile` or pass explicit `--profile`. Creating a named profile does not make it the default. |
| `profile_missing` | Check the resolved profiles root and profile name. Create/import an account there only if a new account/profile is intended. |
| `profile_exists` | Create/import refuses any existing target directory. Select a new name instead of overwriting an account. |
| `profile_incomplete` or damaged identity/configuration | Restore a complete backup. A manifest without its identity/key is not a usable empty profile. |
| `invalid_config`, version, or JSON error | Compare with the [current configuration format](configuration.md); unknown fields and unsupported schema versions are rejected. |
| `account list` exits 3 | Inspect `error.details.items` for readable accounts and `error.details.issues` for damaged entries. |
| Leftover `.pending-*` directory | It is an unpublished temporary profile and is ignored. Confirm no creator is still running before operator cleanup. |

`doctor` can report an absent default profile successfully before first use. Conversely, structural readiness does not prove that a credential can decrypt the key or a Relay is reachable.

## Credentials and permissions

`interaction_required` means a terminal-only prompt cannot be served in the current process. Use file/native protection for unattended unlocking, or let an operator start the daemon interactively. A wallet `--password-file` does not replace a profile passphrase.

For file protection, confirm that the configured absolute path exists, contains exactly 32 binary bytes, and has private permissions. For DPAPI, confirm the Windows user/environment. For Linux native protection, confirm the service has the named credential under `CREDENTIALS_DIRECTORY`; simply launching the binary outside systemd does not supply that environment.

`credential_invalid` can mean a wrong credential, modified ciphertext, or a mismatched encryption binding. Do not edit profile IDs/networks or relax permissions to force access. See [security](security.md) for rewrap and recovery boundaries.

## Busy profiles and daemon connectivity

`profile_busy` means another process owns the SDK session lock. If a daemon is running, use the same configuration/profile and operating-system user so normal commands reach it through IPC. Otherwise wait for the owning command to exit. Export, establish, recover, and rewrap require stopping the daemon first.

`daemon stop` acknowledges a request, not completed cleanup. Wait for process exit. Do not delete a lock file to defeat an active lock. A leftover lock file after process exit does not itself imply a live owner.

On Linux, shorten the data path if `ipc_path` reports the Unix socket path is too long. Large IPC replies can exceed the 4 MiB frame limit; lower the query limit. For a lost connection during a write, treat the outcome as uncertain and query state before retrying.

## Network and startup timeouts

```sh
meshline doctor --profile agent --network --json
meshline relays list --profile agent --json
```

These checks exercise RPC/Registry access. `daemon status` only checks local IPC, while `status` can return locally stored state without refreshing the network. Neither alone proves remote connectivity.

Confirm the RPC belongs to the configured network and Registry, inspect the returned Relay status, and check DNS, TCP, TLS, and Relay reachability separately. A reachable TCP port does not prove the TLS handshake or Neo RPC succeeds. If changing RPC, use an endpoint for the same network and restart affected processes; do not alter the network identity to work around connectivity.

The CLI reports `operation_timeout` only when its own deadline expires. `request_timeout` means a dependency raised a timeout; when supplied by the SDK, `error.details.operation` and `timeoutSeconds` identify the request and its budget. `dependency_canceled` means a dependency canceled while the command was still allowed to run; it does not establish a timeout or user cancellation. This also covers SDK versions that still report request deadlines as cancellation. Raising `--timeout` does not change SDK request deadlines. Preserve the error details and timing, and reconcile submitted writes before retrying.

TLS-handshake stalls have been reproduced with independent clients and more than one RPC endpoint in the available test environment. The exact failing network component remains unresolved. This is not evidence that every endpoint is down or that disabling certificate validation is a solution. Linux live daemon startup remains unverified in that environment.

## Missing messages or uncertain delivery

Keep a daemon or `watch` session running, or explicitly await `account sync`, `groups sync ID`, or `channels sync ID` before querying. Local queries do not fetch newly arrived messages by themselves. Startup readiness is not a synchronization-completion signal. Inspect `status` (optionally `--group` or `--channel`) for runtime progress. A new short-lived session reports fresh observations, usually `Idle`; completion times are not persisted. For `sync_incomplete`, inspect `error.details.blockReason`, resolve missing keys or other blockers, and retry. Check contact approval, active local contact grants, and group membership/keys as relevant.

`TargetAccepted` confirms destination-Relay acceptance, not recipient read state. Terminal outcomes survive restart but are capped at the newest 1,000 account-wide records, including internal sends. An absent outbox entry or null `messages status` does not confirm delivery. When a send returns `message_pending` or `message_status_unavailable`, retain its ID and last state from the error, inspect status/history, and avoid blindly sending a duplicate.

An empty or truncated local list is not a complete remote inventory. Check `hasMore`; use history `--after`/`--before` positions to continue within the same database/resource and filters. With `--before`, continue from the first returned position; with `--after`, continue from the last. Channel edits retain the original post sequence, so watching only increasing sequence values misses edits. Later group decryption can also reveal older messages.

If `watch_overflow` occurs or the event stream disconnects, query current state and restart watch. There is no durable replay cursor. Follow the [Agent guide](automation.md) for idempotency and read-state limitations.

## Recovery and missing history

Importing a wallet creates a new local profile/device; `account recover` explicitly restores network authority. Neither recreates deleted SQLite history. A device can recover authority while still lacking local contact grants or group keys. Existing authorized devices may need to synchronize contact state, and group recovery may require approval.

Previously unreadable messages are not guaranteed to become readable retroactively after later synchronization. Inspect actual message content/state rather than treating account recovery success as complete data restoration. A complete offline backup offers a different recovery path from a wallet alone.

## Known limitations

| Area | Current boundary |
| --- | --- |
| Release maturity | Development preview; implemented commands and passing builds do not establish complete live acceptance. |
| Windows/Linux | Both platforms have build and automated-test coverage; Linux offline credential/permission/signal checks have passed. |
| Linux daemon/service | Ordinary-user socket ownership/lifecycle and the full systemd daemon template still need successful live acceptance. |
| Cross-Relay behavior | Cross-Relay delivery and home-Relay migration await a second Relay for acceptance. |
| Recovery | Restoring authority is separate from contacts, grants, keys, and historical readability. |
| Agent consumption | No exact batch ACK, durable watch replay, or exactly-once processing guarantee. |
| Large histories | Unbounded latest views still scan snapshots; use explicit bounds for efficient pages of up to 10,000 items. Successive commands open new snapshots and do not guarantee complete remote history. |
| Operations | No daemon log rotation; no Windows Service installation or automatic supervisor setup. |
| Additional hardening | Broader fault/permission/interruption matrices and large-history performance need further validation. |

Hosted [CI and Release workflows](https://github.com/meshline-network/client/actions) have completed successfully, and [release downloads](https://github.com/meshline-network/client/releases/latest) are available. Their automated checks do not establish the pending live acceptance listed above.

Report a problem with the application version, OS, command with secrets removed, exit code, structured error, and relevant diagnostics. Keep local environment evidence outside published user documentation.
