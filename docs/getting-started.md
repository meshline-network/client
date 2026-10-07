# Getting started

[Documentation home](../README.md#documentation) · [Command reference](commands.md)

This guide takes two participants from installation to their first direct message. Account setup publishes identity and device information to a Relay; it does not send a blockchain transaction. Use dedicated accounts while evaluating the development preview.

## Install a package

Download the archive for your platform and `SHA256SUMS` from the same [GitHub Release](https://github.com/meshline-network/client/releases/latest). In these examples, replace `0.2.0` with that release's version. To build the current source instead, follow [the build instructions](development.md#build-and-test).

### Windows PowerShell

Run in the download directory:

```powershell
$version = "0.2.0"
$archive = "meshline-$version-win-x64.zip"
$expected = ((Get-Content .\SHA256SUMS | Where-Object { $_.EndsWith("  $archive") }) -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if (-not $expected -or $actual -ne $expected) { throw "Archive checksum mismatch" }
Expand-Archive -LiteralPath $archive -DestinationPath .\meshline
$env:PATH = (Resolve-Path .\meshline).Path + [IO.Path]::PathSeparator + $env:PATH
meshline --version
meshline --help
```

The `PATH` change applies to this PowerShell session. Choose a writable extraction directory for the default profile storage layout. Do not extract over an existing installation without following the [upgrade instructions](operations.md#backup-restore-and-upgrade).

### Linux shell

Run in the download directory:

```sh
version=0.2.0
sha256sum --ignore-missing -c SHA256SUMS
mkdir meshline
tar -xzf "meshline-$version-linux-x64.tar.gz" -C meshline
export PATH="$PWD/meshline:$PATH"
meshline --version
meshline --help
```

Check that the downloaded archive reports `OK` before extracting it. The archive preserves the executable bit. The `PATH` change applies to this shell session. The target is Ubuntu 24.04 x64; a self-contained .NET package still relies on native system libraries.

## Choose credentials and storage

The executable comes with a ready-to-read `config.json`. Its defaults select the profile named `default` and store profiles in a `profiles` directory beside that configuration file. No account exists until you create or import one.

For a fixed data location independent of the installation, copy the bundled configuration to your chosen location, edit `profilesDirectory`, and use `--config` on every command. See [configuration](configuration.md). Otherwise, the examples below use the bundled file.

| Protection | Suitable use |
| --- | --- |
| `file` | Unattended clients with an access-restricted external key file; used in this tutorial |
| `native` on Windows | DPAPI protection for the same Windows user and environment |
| `native` on Linux | A service receiving a credential through systemd; requires the [service setup](operations.md#linux-systemd) |
| `passphrase` | A person unlocking the profile in an interactive terminal |

Do not place the external file credential inside the new profile directory. The CLI can generate it with private permissions. Keep it with your protected backups; losing it prevents unlocking this profile. See [security](security.md) for recovery boundaries and protection changes.

## Create an account

Both participants perform this setup independently. Single-line `sh` examples below also work in PowerShell once `meshline` is on `PATH`; replace quoted uppercase placeholders with real values.

```sh
meshline account create --network neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70 --rpc https://n3seed1.ngd.network:10332 --protection file --key-file ./secrets/default.key --generate-key
meshline account show --json
```

This is an example mainnet configuration, not a guarantee of RPC availability. The result includes `data.accountId`; share that public ID when adding a contact. Account creation makes the identity and profile together and refuses an existing target profile. The application configuration is not changed.

For two accounts in one installation, create separate profiles using `--profile alice` and `--profile bob`, and use distinct external key files. Add the same `--profile` to every subsequent command for each participant. Creating a named profile does not change `defaultProfile`.

### Import instead of creating

To select one account from an existing NEP-6 wallet, use a new profile name:

```sh
meshline account import --profile imported --wallet ./wallet.json --account-index 0 --network neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70 --rpc https://n3seed1.ngd.network:10332 --protection file --key-file ./secrets/imported.key --generate-key
```

The wallet password is prompted without echo. Automation can supply `--password-file` pointing to an access-restricted file. Only the selected account is imported; the source wallet is unchanged. For an identity already established on the network, follow [account recovery](operations.md#account-authority-recovery) instead of treating `establish` as implicit recovery.

## Select a Relay and connect

For a new account:

```sh
meshline relays list --json
```

Choose an entry whose status is active, then copy its `relayId` into these commands:

```sh
meshline account establish --relay "RELAY_ID" --timeout 180
meshline daemon start --timeout 180
meshline daemon status --json
meshline status --json
```

`establish` authorizes this device and establishes the account route. Keep the daemon running during the conversation so SDK synchronization and pending sends can progress. Readiness reports that the daemon has started; it is not a history-synchronization barrier. For RPC or startup failures, use [troubleshooting](troubleshooting.md#network-and-startup-timeouts); increasing the command deadline cannot override every underlying request timeout.

## Your first conversation

Alice and Bob must both have established accounts and running daemons. The names below identify participants, not required profile names. If using named profiles in one installation, append the appropriate `--profile alice` or `--profile bob` to each command.

### 1. Alice creates an invitation

```sh
meshline contacts invite --out ./alice-invite.json
```

Give Bob this invitation file through a channel you choose. The default invitation lifetime is one day. Do not give Bob your key file, wallet password, or profile directory.

### 2. Bob requests contact access

Bob places the invitation in his working directory and runs:

```sh
meshline contacts add --invite-file ./alice-invite.json
```

### 3. Alice accepts Bob

Alice refreshes the account timeline before querying the request:

```sh
meshline account sync --json
meshline contacts requests --direction Incoming --json
meshline contacts accept "BOB_ACCOUNT_ID"
```

Use Bob's account ID from the request or his `account show` result. A locally empty request list does not prove no remote request exists; keep synchronization running and query again.

### 4. Bob sends and Alice reads

Bob sends to Alice's public account ID:

```sh
meshline messages send --to "ALICE_ACCOUNT_ID" --text "Hello Alice" --wait target --json
```

`TargetAccepted` means the destination Relay accepted the message. It does not mean Alice has read it.

Alice queries synchronized history:

```sh
meshline account sync --json
meshline conversations list --unread --json
meshline conversations messages "BOB_ACCOUNT_ID" --limit 50 --json
```

A direct conversation ID is its peer's account ID. Querying messages leaves the read position unchanged. Alice can explicitly mark the conversation read:

```sh
meshline conversations mark-read "BOB_ACCOUNT_ID" --sequence 42 --json
```

Replace `42` with the last viewed message's `localSequence`. This marks the inclusive prefix through that position and leaves newer arrivals unread. Omitting `--sequence` marks through the latest locally readable message at call time. Both operations update cumulative local read state, not an exact application-processing acknowledgement.

### 5. Observe events and stop

```sh
meshline watch --json
```

Run `watch` in another terminal. Ctrl+C ends that watcher; it does not stop a separately running daemon. Events are live notifications without durable replay.

When finished, each participant requests daemon shutdown:

```sh
meshline daemon stop --json
meshline daemon status --json
```

Wait for the process to exit before backing up or replacing files. Continue with the [command reference](commands.md), [Agent guide](automation.md), or [operations guide](operations.md).
