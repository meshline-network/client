# Development

[Documentation home](../README.md#documentation) · [Current limitations](troubleshooting.md#known-limitations)

The repository currently contains the Meshline CLI and leaves room for other clients. The CLI targets .NET 10 and consumes published Meshline.Sdk 1.2.0 APIs. It uses the SDK's state, storage, synchronization, and cryptographic protocol implementation instead of modifying or vendoring the SDK.

## Source layout

| Area | Responsibility |
| --- | --- |
| `src/Meshline.Cli` | Command parsing/catalogs, result/error handling, configuration, credentials, account creation, SDK sessions, and daemon IPC |
| `tests/Meshline.Cli.Tests` | Argument/contract, configuration/profile, credential, account, persistence, and process-related tests |
| `scripts` | Packaging and Linux platform/daemon acceptance tools |
| `.github/workflows` | Build/test CI and version-based GitHub Release automation |
| `docs` | User, Agent, operations, and development documentation distributed with packages |

Command catalogs declare paths and options; execution code supplies several runtime-required checks, mutually exclusive choices, and defaults. Keep [commands](commands.md) aligned with both. `Output` defines the result envelope; protocol converters preserve protocol-specific encoding within it. `RuntimeSession` owns the SDK, vault, connections, diagnostics, and exclusive profile lock. `Daemon` forwards CLI invocations over same-user IPC.

`CliParser` builds the shared command tree. `Interactive` owns one foreground SDK session, a single stdin reader, and separate command/session cancellation. Its inner parser disables the library's process-termination handler so Ctrl+C cancels a command without ending the session. Test stream framing and cancellation through the loop, shared SDK queries through a real local session, and actual process signals/live Relay behavior through acceptance.

## Build and test

Install the .NET 10 SDK. From the repository root, these commands work in PowerShell and Linux shells:

```sh
dotnet restore Meshline.Client.slnx
dotnet build Meshline.Client.slnx -c Release --no-restore
dotnet run --project tests/Meshline.Cli.Tests -c Release --no-build -- --minimum-expected-tests 83 --fail-skips on --report-xunit-trx --results-directory TestResults
dotnet src/Meshline.Cli/bin/Release/net10.0/meshline.dll --help
```

The test application uses the Microsoft Testing Platform runner. Empty/short runs and skipped tests must fail the documented check. Runtime identifiers are Windows and Linux x64. There is no required `packages.lock.json` workflow.

For interactive source execution:

```sh
dotnet run --project src/Meshline.Cli -- --help
```

The bundled configuration is copied to build outputs. With `dotnet run`, an omitted `--config` resolves beside the built application. Use a copied configuration and explicit path for persistent development accounts so rebuilding does not blur the boundary between runtime data and source outputs.

## Scripts

### Test-Interactive.py

Opt-in live acceptance for Windows or Linux, accepting a native executable or a framework-dependent DLL. It creates two fresh accounts, publishes device/route state, grants contact access, and sends a test message between them. Use an unused private work directory and a Relay/network you intend to test; it does not reuse existing accounts or send blockchain transactions.

```sh
python scripts/Test-Interactive.py PATH_TO_BINARY --network NETWORK --rpc RPC_URL --relay RELAY_ID --work-directory NEW_PRIVATE_DIRECTORY
```

The retained report covers session reuse without a credential file, NDJSON help/errors, direct-message delivery and watch receipt, busy input, cancellation, command deadlines, EOF/exit cleanup, and (on Linux) SIGINT/SIGTERM. This verifies the CLI on that host, not survival or tool wake-up in a separate agent environment.

### Publish.ps1

Requires PowerShell 7 with the .NET tar APIs and the .NET 10 SDK. By default it builds both self-contained targets, includes the bundled configuration plus README/docs, and writes archives and `SHA256SUMS` to `artifacts`.

```powershell
pwsh ./scripts/Publish.ps1
pwsh ./scripts/Publish.ps1 -Runtime win-x64
pwsh ./scripts/Publish.ps1 -Runtime linux-x64
```

The project `Version` is the source of truth for binary/package versioning. The script reads it through MSBuild for each target; it has no `-Version` override. Windows uses zip; Linux uses tar.gz and assigns executable mode 0755 to `meshline`. `SHA256SUMS` describes only the runtimes requested in that invocation, so use the default invocation for a complete release. Use a clean packaging output directory for release work; the script can leave older files in reused output directories.

### Test-Linux.py

Runs offline Linux platform acceptance with disposable profiles and a loopback HTTP fixture; it does not contact a Relay or Neo mainnet. It accepts a native executable or a framework-dependent DLL (the latter requires `dotnet` on `PATH`). It requires systemd and either root or noninteractive sudo access for transient services.

```sh
python3 scripts/Test-Linux.py src/Meshline.Cli/bin/Release/net10.0/meshline.dll
```

Checks cover actual encrypted-credential delivery, private file/directory permissions, missing credentials, excessive credential permissions, and SIGTERM cancellation. This script is still needed alongside ordinary .NET tests. It does not prove live ordinary-user daemon socket/service behavior.

### Test-Linux-Daemon.py

Opt-in live acceptance for an ordinary Linux user. It needs a native published binary and explicit network, RPC, Relay, and unused work-directory values. It creates dedicated accounts and retained private profiles, publishes device/route state, and checks daemon/IPC/multiple-profile/shutdown/crash behavior. It does not send blockchain transactions, but it does perform network writes and is not an offline CI test.

```sh
python3 scripts/Test-Linux-Daemon.py /path/to/meshline --network "NETWORK_ID" --rpc "RPC_URL" --relay "RELAY_ID" --work-directory /private/new-validation-directory
```

Replace every placeholder and choose a directory that does not already exist. The work directory and reports can contain private account data; keep them out of source control and release packages. Startup failure prevents later checks from establishing acceptance. The remaining Linux live acceptance is recorded as a limitation, not inferred from the offline checks.

## CI and releases

The `client` CI workflow runs on push, pull requests, manual dispatch, and reusable workflow calls. Its matrix is Windows and Ubuntu 24.04. It restores, builds, runs the test executable with skipped/empty-run protection, checks built CLI help, and runs the offline Linux script on Ubuntu. It neither packages binaries nor uploads Actions artifacts.

The separate `release` workflow runs on pushes to `main`:

1. Read the project version and form tag `v<Version>`.
2. Inspect releases, including drafts. An existing published release is skipped; a same-tag draft fails with recovery instructions.
3. For a new version, invoke the reusable Windows/Linux CI checks. Ordinary push CI can also run independently.
4. Check that any existing tag points to the triggering commit. A conflicting tag fails rather than relabeling binaries.
5. Call `Publish.ps1`, then create a GitHub Release with both archives and `SHA256SUMS`, generated notes, and the triggering commit as tag target. Versions with a prerelease suffix are marked prerelease.

Release runs queue serially. Publishing jobs request `contents: write` through the repository's `GITHUB_TOKEN`; no separate personal token is required by the workflow definition. API/authentication failures are not treated as missing versions. If an interrupted upload leaves a draft, inspect and finish or remove that draft before rerunning. Published versions are not overwritten; update the project version for another release.

Both workflows have completed successfully on GitHub; inspect the [CI runs](https://github.com/meshline-network/client/actions/workflows/ci.yml), [Release runs](https://github.com/meshline-network/client/actions/workflows/release.yml), and [published releases](https://github.com/meshline-network/client/releases) for current results and downloads. Build and offline-test success does not establish complete Linux daemon/systemd or cross-Relay live acceptance. `concurrency.queue: max` uses GitHub's [documented queue setting](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency#example-queueing-multiple-pending-runs); older actionlint versions may not recognize that field. Attaching assets through `gh release create` follows the [GitHub CLI release workflow](https://cli.github.com/manual/gh_release_create).

## SDK integration notes

| SDK behavior or integration boundary | Client practice |
| --- | --- |
| Conversations, unread counts, read positions, history, and retry state belong to the SDK | Query them directly; do not add a second message store or consumer ACK system to the CLI. |
| Message identity and history position have different scopes | Preserve sender plus message ID for identity; use `LocalSequence` with the same database and resource for paging. |
| Explicit read positions acknowledge a cumulative local prefix | Pass `--sequence` for viewed content; preserve mark-all behavior when omitted. Neither is an exact processing ACK. |
| Query readers are local process snapshots | Use `HistoryRange` for adjacent pages. A backward lookahead is the oldest extra item, so remove it from the start. Unbounded latest views still scan the snapshot. |
| The SDK retains terminal send outcomes and provides a race-safe wait | Use `WaitForSendStatusAsync`, inspect failures/cancellation/null, and preserve the message ID when a wait ends. Retention is bounded; absence is not a delivery receipt. |
| Startup is not a history synchronization barrier | Use manager `SynchronizeAsync` for a fresh pass and `GetSyncStatusAsync`/`SyncStatusChanged` for observations. Check `CaughtUp`, blocking details, and retention gaps separately. |
| Protocol models have dedicated JSON encoding | Use protocol conversion for embedded documents; avoid default serialization that changes their wire representation. |
| SQLite connection pools can retain file handles | Tests that remove a database release only that database's connection pool; avoid clearing unrelated pools globally. |
| Credentials depend on the host environment | Keep protection explicit and verify rewrap before saving; use private IPC for background startup. |
| Application preferences and account state have different lifecycles | Ship application configuration, create identity/profile together, and preserve the SDK's persistence ownership. |
| Account authority, contact synchronization, and message readability recover separately | Report the actual stage/result instead of declaring complete history restoration. |

The CLI keeps its own command/IPC deadline and JSON result policy. SDK 1.2.0 owns history-range validation, cumulative read-position updates, send-status retention/waiting, and synchronization gates. `groups sync` refreshes the home account timeline before the group to obtain recovery material. One-shot synchronization does not start background workers; daemon commands still serialize it with other network operations. Runtime synchronization exceptions are converted to structured errors before JSON serialization.

CLI error mapping separates its own elapsed deadline (`operation_timeout`), an SDK-owned or dependency timeout (`request_timeout`), and unexpected dependency cancellation (`dependency_canceled`). SDK 1.2.0 supplies `operation` and `timeoutSeconds` on its `TimeoutException`; the CLI forwards that context. Integration tests exercise the published package with a canceled HTTP fixture, real local history/read-position operations, and retained send outcomes. Test-only SQLite fixtures seed isolated projections; production code does not access SDK tables directly.

## Maintaining documentation

Treat the command catalog, handlers, configuration models, and workflows as sources of truth. Check every command entry and its options when changing behavior, including requirements enforced only at execution time. Keep general output semantics in the Agent guide and command-specific results in the reference.

Validate relative links and heading anchors, JSON examples, shell syntax, and the documents included in both release archives. Exercise offline examples with temporary profiles; do not run network-mutating examples merely to check documentation. Keep local reports and test identities in ignored private storage, and publish only current behavior and evidence-bounded limitations.
