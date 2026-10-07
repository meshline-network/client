# Operations

[Documentation home](../README.md#documentation) · [Daemon commands](commands.md#daemon-and-events)

## Sessions and interaction

The CLI can run a short-lived SDK session for a command or forward that command to a resident daemon. A daemon holds one profile's session lock, credentials, and SDK database connection. It keeps background synchronization and pending operations active.

The daemon is not an interactive command prompt. After starting it, open another terminal under the same operating-system user and run ordinary CLI commands with the same `--config` and `--profile`. Those commands use local IPC automatically. Commands needing background work and explicit synchronization passes are serialized in the daemon; local SDK queries and `watch` can execute alongside them.

```sh
meshline daemon start --profile agent --timeout 180
meshline daemon status --profile agent --json
meshline conversations list --profile agent --json
meshline watch --profile agent --json
```

The watcher occupies its terminal until canceled. Stopping a watcher attached to a daemon does not stop the daemon. Without a daemon, `watch` runs its own foreground SDK session.

`account show`, `account list`, `config show`, and local diagnostics do not need an unlocked SDK session. `relays list` queries the configured Registry separately. Account establishment, recovery, export, and key rewrap require stopping the profile's daemon first.

## Interactive sessions

Use one foreground process when a terminal or agent can keep stdin/stdout open:

```sh
meshline interactive --profile agent --timeout 180
```

After readiness, enter one command at a time (without `meshline`):

```text
status
conversations list
watch
cancel
messages send --to ACCOUNT_ID --text "Hello from the interactive session"
exit
```

Wait for each command to finish before entering the next; `cancel` is accepted while a command is running. Ctrl+C has the same effect. Empty Enter does not stop `watch`. The selected account stays unlocked and synchronized until `exit`, EOF, or process termination. Startup requires an established account with usable credentials; another daemon/session over that profile must be stopped first. Other profiles may run independently.

This mode creates no daemon listener and uses no CLI-to-daemon connection. An independent CLI process cannot attach to it and receives `profile_busy` for operations needing that same session. Reuse the original stdin/stdout handles instead. For agent output framing and cancellation, see [interactive agent process](automation.md#interactive-agent-process).

The entry timeout covers startup and ordinary commands, not the process lifetime. `watch --timeout 30` explicitly limits one watch. Successful startup does not mean synchronization is complete. Before backup, account/key management, or replacement, exit and wait for process termination so the exclusive lock and SQLite handles have been released.

## Foreground and background execution

| Command | Process behavior |
| --- | --- |
| `daemon run` | Remains in the foreground; suitable for a service supervisor. |
| `daemon start` | Unlocks the profile, starts a child process, and returns after readiness. |
| `daemon status` | Checks local IPC reachability without opening the database. |
| `daemon stop` | Requests graceful shutdown; wait for process exit before working with its files. |
| `interactive` | Keeps one foreground session and reads commands from stdin without IPC. |

Readiness does not guarantee completion of history synchronization. Use `status` for the daemon's runtime observations, or await `account sync`, `groups sync ID`, or `channels sync ID` for a fresh pass. Without a daemon, these commands perform one pass and exit without starting background workers. A stopped daemon means no continued background work unless another command/session is running.

Windows `daemon start` launches an ordinary hidden process. It does not install a Windows Service, register with SCM, or arrange automatic startup/restart. IPC uses a current-user-only named pipe derived from the profile identity. Each distinct profile can have its own instance:

```sh
meshline daemon start --profile alice
meshline daemon start --profile bob
meshline daemon status --profile alice --json
meshline daemon status --profile bob --json
```

Linux uses a Unix socket in the profile's data directory with private permissions. Keep the full `daemon.sock` path within the CLI's 100-byte limit. A stale socket alone is not evidence that a process is alive; the daemon handles its socket under the exclusive session lock. Do not remove lock files to bypass an active session.

## Linux systemd

The following is a deployment template, **not a completed production-service acceptance result**. Offline systemd encrypted-credential and signal handling checks have passed; ordinary-user live daemon/socket and full service acceptance remain pending. See [known limitations](troubleshooting.md#known-limitations).

Use `daemon run` under systemd so the supervisor owns the process. For native Linux protection, systemd supplies a decrypted credential file through `CREDENTIALS_DIRECTORY`; `LoadCredentialEncrypted` loads its encrypted source. The credential name must match the name used during encryption. See the [systemd credentials reference](https://systemd.io/CREDENTIALS/).

Prepare these paths and ownership before running the commands:

- Install the extracted program under `/opt/meshline`, readable/executable by the service user.
- Create a dedicated `meshline` user/group and a `/var/lib/meshline` directory owned by that user with mode 0700.
- Copy the bundled `config.json` to `/var/lib/meshline/config.json`, readable by the service user. Its relative `profilesDirectory` places data there, not under `/opt`.
- Create `/etc/credstore.encrypted` with restricted administrative access. The next commands use administrative privileges and an unused credential name/path.

Generate and encrypt a credential (Linux shell):

```sh
sudo /opt/meshline/meshline secrets generate-key --out /root/meshline-bootstrap.key
sudo systemd-creds encrypt --name=meshline-key /root/meshline-bootstrap.key /etc/credstore.encrypted/meshline-key.cred
```

Create the account in a transient service with the same user and credential environment:

```sh
sudo systemd-run --wait --pipe --collect --property=User=meshline --property=Group=meshline --property=UMask=0077 --property=LoadCredentialEncrypted=meshline-key:/etc/credstore.encrypted/meshline-key.cred /opt/meshline/meshline account create --config /var/lib/meshline/config.json --profile agent --network neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70 --rpc https://n3seed1.ngd.network:10332 --protection native --credential-name meshline-key
sudo -u meshline /opt/meshline/meshline relays list --config /var/lib/meshline/config.json --profile agent --json
```

Choose an active Relay and replace `RELAY_ID`. Establishment also needs the credential environment:

```sh
sudo systemd-run --wait --pipe --collect --property=User=meshline --property=Group=meshline --property=UMask=0077 --property=LoadCredentialEncrypted=meshline-key:/etc/credstore.encrypted/meshline-key.cred /opt/meshline/meshline account establish --config /var/lib/meshline/config.json --profile agent --relay "RELAY_ID" --timeout 180
```

After validating decryption and a protected backup strategy, handle the plaintext bootstrap key according to your secret-retention policy. It is not needed by this service at runtime, but losing all usable credentials prevents unlocking the profile.

Save this unit as `/etc/systemd/system/meshline-agent.service`:

```ini
[Unit]
Description=Meshline client (agent profile)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=meshline
Group=meshline
UMask=0077
LoadCredentialEncrypted=meshline-key:/etc/credstore.encrypted/meshline-key.cred
ExecStart=/opt/meshline/meshline daemon run --config /var/lib/meshline/config.json --profile agent --json
WorkingDirectory=/var/lib/meshline
Restart=on-failure
RestartSec=10
PrivateTmp=yes
PrivateMounts=yes
NoNewPrivileges=yes

[Install]
WantedBy=multi-user.target
```

Start and inspect it (Linux shell):

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now meshline-agent.service
sudo systemctl status meshline-agent.service
sudo journalctl -u meshline-agent.service -n 100
sudo -u meshline /opt/meshline/meshline daemon status --config /var/lib/meshline/config.json --profile agent --json
```

Same-user CLI processes can reuse the live daemon through IPC without loading its credential again. When the daemon is stopped, operations that unlock the profile must run in the appropriate credential environment. Use `systemctl stop meshline-agent.service` for a supervisor-managed shutdown. Multiple services need distinct unit names and distinct profiles; do not start two units over the same profile or device database.

## Logs and diagnosis

Background `daemon start` writes diagnostics to `daemon.log` in the data directory. Foreground `daemon run` emits diagnostics to stderr; systemd normally collects these in the journal. There is no built-in log rotation.

Set `MESHLINE_TRACE=1` before starting a process to include SDK lifecycle transitions. It does not enable credential, authentication-token, or message-body logging. Treat diagnostic and result files as private because identifiers and operational state can still be sensitive.

PowerShell:

```powershell
$env:MESHLINE_TRACE = "1"
meshline daemon start --profile agent --timeout 180
Remove-Item Env:MESHLINE_TRACE
```

Linux shell:

```sh
MESHLINE_TRACE=1 meshline daemon start --profile agent --timeout 180
```

Restart an already running daemon to change its inherited environment. IPC frames are limited to 4 MiB; reduce query limits when a response is too large. See [troubleshooting](troubleshooting.md) for diagnosis without changing account identity.

## Backup, restore, and upgrade

1. Stop the daemon or its supervisor and confirm the process has exited. Prevent other commands from opening the profile while copying files.
2. Back up the application configuration, complete profile directory, and any external file credentials. Include an explicitly configured separate `dataDirectory`. Preserve any SQLite `-wal` and `-shm` files alongside the database rather than copying only `client.db`.
3. Store the backup with encryption and restricted access. DPAPI and systemd protection can depend on the original user/machine; test the intended restore environment or [rewrap](security.md#change-protection) while the original environment is still available.
4. Restore the complete set, update storage/credential paths if necessary, and preserve profile identity and network. Do not run two copies of the same device database at once.
5. For upgrades, verify and extract the new package separately. Preserve customized `config.json`, profiles, and external credentials; do not overwrite them with package defaults. Replace binaries only after shutdown, then start the intended profile and inspect diagnostics.

SDK initialization applies database migrations. A failed upgrade does not automatically roll back the database or guarantee compatibility with an older binary; retain the pre-upgrade backup.

## Account authority recovery

A wallet export is an account-key backup, not a complete profile/history backup. To recover an existing identity onto a fresh device, import its wallet into a new profile, then explicitly recover authority while that profile's daemon is stopped:

```sh
meshline account recover --profile imported --relay "RELAY_ID" --previous-state ./device-state.json --timeout 180
```

`--previous-state` is optional and refers to a previously saved `devices list --out` protocol document. Explicit revision options are available when required; see [account recover](commands.md#account-recover). Inspect device IDs before revoking old devices.

Authority recovery does not recreate deleted SQLite history. Contact authorization state may need synchronization from an existing device; group-key recovery may need approval by another authorized member. A recovered account can therefore still lack readable messages. See [recovery troubleshooting](troubleshooting.md#recovery-and-missing-history).
