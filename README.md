# Meshline Clients

## Overview

This repository provides clients for Meshline. Its first client, `meshline`, is a command-line application for people, scripts, and AI agents.

The CLI manages accounts, devices, contacts, encrypted direct and group messages, public channels, and local conversation history. An optional daemon keeps an account connected while other CLI processes issue commands. Each local profile belongs to one account.

`meshline interactive --profile agent --json` keeps a profile connected in one foreground process and accepts commands through stdin, without daemon IPC. See [interactive sessions](docs/operations.md#interactive-sessions).

The client is a **development preview**. It uses the published .NET SDK for synchronization, conversation state, message history, read positions, and retained send outcomes.

## Installation

Download the archive for your platform and `SHA256SUMS` from the [latest GitHub Release](https://github.com/meshline-network/client/releases/latest). The initial development-preview release is [v0.1.0](https://github.com/meshline-network/client/releases/tag/v0.1.0).

| Platform | Archive |
| --- | --- |
| Windows x64 | `meshline-<version>-win-x64.zip` |
| Linux x64 | `meshline-<version>-linux-x64.tar.gz` |

Verify the archive checksum, extract it into a writable directory, and put that directory on your `PATH`. Packages include the executable, default `config.json`, and these documents; no separate .NET installation is required. Linux packages target Ubuntu 24.04 x64 and still require the operating system's native libraries. macOS packages are not provided.

See [installation and first use](docs/getting-started.md) for platform-specific steps, or [development](docs/development.md) to build from source. Follow the [upgrade procedure](docs/operations.md#backup-restore-and-upgrade) when replacing an existing installation.

## Quick Start

With `meshline` on `PATH`, run the following from a writable working directory. These single-line commands work in PowerShell and a Linux shell. The network and RPC below are an example Neo mainnet configuration; RPC availability is not guaranteed.

```sh
meshline account create --network neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70 --rpc https://n3seed1.ngd.network:10332 --protection file --key-file ./secrets/default.key --generate-key
meshline relays list --json
```

Choose an active Relay from the result and replace `RELAY_ID` below:

```sh
meshline account establish --relay "RELAY_ID" --timeout 180
meshline daemon start --timeout 180
meshline account sync --json
meshline status --json
```

Account creation also creates its local profile. There is no initialization or configuration-generation command. The CLI reads `config.json` beside the executable unless `--config` selects another existing file. Keep the external key file: it is required to unlock this profile.

Continue with [your first conversation](docs/getting-started.md#your-first-conversation), which walks through both participants, contact approval, sending, and reading.

## For Agents

Use file protection or an appropriate platform credential provider for unattended execution. Use `--json` for compact results, keep a daemon running for background synchronization or explicitly run `account sync`, and inspect operation state before retrying an uncertain write. Page history with `--after`/`--before` and acknowledge viewed content with `conversations mark-read --sequence`. Reading messages does not mark them read.

The [automation guide](docs/automation.md) defines output, exit codes, delivery states, event handling, and a minimal processing loop. `watch` is a live event stream without durable replay; a successful daemon start does not mean history synchronization has finished.

## Documentation

| Task | Guide |
| --- | --- |
| Install and exchange a first message | [Getting started](docs/getting-started.md) |
| Look up any command or option | [Complete command reference](docs/commands.md) |
| Choose configuration paths and manage profiles | [Configuration](docs/configuration.md) |
| Integrate a script or Agent | [Automation](docs/automation.md) |
| Run daemons, deploy, back up, and upgrade | [Operations](docs/operations.md) |
| Choose key protection and understand its limits | [Security](docs/security.md) |
| Diagnose errors and check known limitations | [Troubleshooting](docs/troubleshooting.md) |
| Build, test, package, and develop the client | [Development](docs/development.md) |

## Current Limitations

Linux builds and offline credential/signal checks have passed, but ordinary-user daemon socket lifecycle and the complete systemd service deployment still need live acceptance. Cross-Relay delivery and home-Relay migration also remain unverified without a second Relay.

Account authority recovery does not restore deleted local history or guarantee immediate contact/key synchronization. See [known limitations](docs/troubleshooting.md#known-limitations) before relying on unattended operation.

## Related resources

- [Meshline website and developer resources](https://meshline.org/en/resources).
- [Meshline SDKs](https://github.com/meshline-network/sdk) and the [.NET SDK guide](https://meshline.org/sdk/dotnet/index.html).
- [Protocol specification](https://github.com/meshline-network/protocol) and [online reader](https://meshline.org/protocol/v1/en/index.html).
- [Registry reference contracts](https://github.com/meshline-network/contracts).
