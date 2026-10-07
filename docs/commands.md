# Complete command reference

[Documentation home](../README.md#documentation) · [Output and exit-code contract](automation.md#output-contract)

## Contents

- [Conventions and shared options](#conventions-and-shared-options)
- [Configuration and diagnostics](#configuration-and-diagnostics)
- [Accounts](#accounts)
- [Secrets](#secrets)
- [Devices](#devices)
- [Network account profile](#network-account-profile)
- [Contacts](#contacts)
- [Conversations](#conversations)
- [Direct messages](#direct-messages)
- [Draft files](#draft-files)
- [Groups](#groups)
- [Channels](#channels)
- [Daemon and events](#daemon-and-events)

## Conventions and shared options

Usage lines show required values in uppercase, optional parts in brackets, and alternatives separated by `|`. Do not type the brackets. Examples use quoted placeholder IDs such as `"ACCOUNT_ID"`; replace them before execution. Single-line examples work in PowerShell and Linux shells with `meshline` on `PATH`. Use `meshline COMMAND --help` for parser help at any level.

| Global option | Behavior |
| --- | --- |
| `--config PATH` | Select an existing application configuration. Default: `config.json` beside the application. See [path resolution](configuration.md#path-resolution). |
| `--profile NAME` | Select a local account; default: the application's `defaultProfile`. For create/import, select a new profile name. |
| `--json` | Compact JSON output; ordinary results are indented JSON without it. `watch` always uses NDJSON. |
| `--timeout SECONDS` | 0–86400, default 60; 0 disables the CLI deadline. `watch` and `daemon run` default to 0. |
| `--help` | Show textual help. Root `--version` shows the application version. |

All command-specific results below describe the `data` field of the [result envelope](automation.md#output-contract). Successful void SDK operations return a receipt identifying the action, not an additional delivery or synchronization guarantee.

Unless stated otherwise, SDK commands require an existing profile and an unlockable credential, or a daemon already holding that profile's session. A temporary SDK session takes the profile's exclusive lock; commands use an existing daemon automatically. Network operations require a usable authorized device/route, except account establishment and recovery, which create or restore authority. Local queries need no live Relay connection and do not refresh remote state. See [operations](operations.md) for lifecycle details.

Local history defaults to the latest 50 entries; local lists default to the first 200. `--limit` accepts 1–10000. Local results contain `items`, `hasMore`, `source`, `selection`, and `scanned`. History commands accept exclusive `--after` and `--before` positions; see [local pagination](automation.md#synchronization-and-local-queries) for ordering and continuation. Without bounds, the latest-history view still scans the snapshot. Remote pages default to 50 and use optional `--cursor`; their results contain `items` and `nextCursor`, and the Relay may apply its own limits. `account list` is a separate filesystem scan without `--limit`.

Enumerated filters are case-insensitive and must be a defined value listed below; arbitrary combined flags are not accepted by the CLI. Comma-separated account lists trim whitespace and omit empty entries; provide at least one real account. File outputs refuse existing files. Protocol files must contain the requested document, not the whole CLI result envelope.

## Configuration and diagnostics

### config show

Usage: `meshline config show`

Reads the selected application configuration without requiring an account or credential. Returns its raw settings, not resolved account paths. No command-specific options.

Example: `meshline config show --config ./config.json --json`

### doctor

Usage: `meshline doctor [--network]`

Checks runtime, configuration, and readable profile/identity structure without unlocking the vault or opening the SDK database. `--network` additionally verifies RPC/network identity and reads the Registry when a complete profile exists. Returns a diagnostic report; failures preserve the report in `error.details`. A missing implicit default account is a normal first-use result, but an explicitly selected missing profile fails. Readiness is not proof of credential validity or complete synchronization.

Example: `meshline doctor --profile agent --network --json`

### relays list

Usage: `meshline relays list`

Requires a complete local profile to select network/RPC settings. Reads registered Relays from the Registry over RPC; it does not unlock the credential or open an SDK session. Returns `{items}`. Inspect each Relay's status before selection; the result is not restricted to active Relays.

Example: `meshline relays list --json`

### status

Usage: `meshline status [--relay RELAY | --group GROUP_ID | --channel CHANNEL_ID]`

Returns the local SDK account ID, lifecycle, route, device, device state, and a `synchronization` snapshot. Select at most one resource; the default is the home Relay's account timeline, or null before a route exists. The snapshot contains `kind`, `resource`, `state`, `lastSynchronizedAt`, `blockReason`, structured `error`, and `hasRetentionGap`. Queries do not access the network. A new SDK session normally reports `Idle`; runtime progress and completion times reset between sessions. Use the daemon to inspect its current observations. `CaughtUp` describes a completed pass, not a guarantee that no newer remote events exist.

Example: `meshline status --profile agent --json`

## Accounts

### account create

Usage: `meshline account create --network NETWORK --rpc URL --protection MODE [--key-file PATH] [--generate-key] [--credential-name NAME]`

Creates one account and its complete local profile atomically. Requires an existing application configuration and a profile name whose target directory does not exist. It does not establish the account on a Relay. Returns public identity, profile name, manifest path, and data directory.

| Option | Requirement and default |
| --- | --- |
| `--network` | Required; `neo:<network-magic>:<registry-hash>`. |
| `--rpc` | Required; HTTP(S) endpoint for that network. |
| `--protection` | Required; `file`, `native`, or `passphrase`. |
| `--key-file` | Required for file mode; stored as an absolute path and must be outside the new profile directory. |
| `--generate-key` | File mode only; generate a new 32-byte credential at `--key-file`, refusing an existing file. Otherwise read the existing credential. |
| `--credential-name` | Required for Linux native mode; selects a credential supplied through systemd. Windows native mode uses current-user DPAPI. |

Passphrase mode prompts for a new passphrase and confirmation. File/native modes support unattended creation when their credential requirements are met.

Example: `meshline account create --profile agent --network neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70 --rpc https://n3seed1.ngd.network:10332 --protection file --key-file ./secrets/agent.key --generate-key`

### account import

Usage: `meshline account import --wallet PATH --network NETWORK --rpc URL --protection MODE [--account-index N] [--password-file PATH] [--key-file PATH] [--generate-key] [--credential-name NAME]`

Uses the same profile and protection rules as [account create](#account-create). `--wallet` is required and selects a NEP-6 wallet. `--account-index` is zero-based, default 0. `--password-file` reads an access-restricted wallet-password file; otherwise a hidden terminal prompt is required. The selected account is imported into a new identity/profile; the source wallet is unchanged. Returns the same public identity/path fields as create. An existing network identity may require explicit [recovery](#account-recover).

Example: `meshline account import --profile imported --wallet ./wallet.json --account-index 0 --network neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70 --rpc https://n3seed1.ngd.network:10332 --protection file --key-file ./secrets/imported.key --generate-key`

### account export

Usage: `meshline account export --out PATH [--password-file PATH]`

Requires an unlocked profile and exclusive access: stop its daemon first. `--out` is required; the destination must not exist. Provide an access-restricted password file or enter a nonempty export password at the hidden prompt. Returns `{path}` for an encrypted NEP-6 wallet. This wallet does not include the profile database or message history.

Example: `meshline account export --out ./account-wallet.json`

### account list

Usage: `meshline account list`

Lists all readable accounts under the configured profiles root without unlocking or taking a session lock. Returns `{defaultProfile,profilesDirectory,items,issues}` including profile names and default markers. No profiles yields an empty list. Damaged profiles produce exit 3 with valid entries and issues in `error.details`; temporary `.pending-*` directories are ignored.

Example: `meshline account list --json`

### account show

Usage: `meshline account show`

Reads the selected local identity and settings without unlocking or opening an SDK session. Returns public account/address/key information, profile paths, network/RPC, data directory, and protection configuration. It does not return the protected master key, wallet, or wallet password.

Example: `meshline account show --profile agent --json`

### account sync

Usage: `meshline account sync [--relay RELAY]`

Awaits a fresh incremental account timeline pass on the supplied Relay, defaulting to the established home Relay. Requires usable device authorization. Works without a daemon and does not start background workers. Returns a synchronization snapshot when this pass reaches `CaughtUp`; incomplete processing returns `sync_incomplete` (exit 6), and operational failures preserve their error classification. The command deadline bounds the pass. A retention gap can coexist with `CaughtUp`; completion does not prove complete historical recovery.

Example: `meshline account sync --json`

### account establish

Usage: `meshline account establish [--relay ID] [--certificate-days N] [--route-days N]`

Establishes this identity/device and account route on a Relay; stop any daemon for this profile first. `--relay` chooses a Relay; omission delegates selection to the SDK. Both validity options default to 365 days and accept 1–3650. Returns the SDK status after establishment. This command does not implicitly recover an existing account onto a replacement device.

Example: `meshline account establish --relay "RELAY_ID" --timeout 180`

### account recover

Usage: `meshline account recover [--relay ID] [--previous-state PATH] [--device-state-revision N] [--route-revision N] [--certificate-days N] [--route-days N]`

Explicitly restores account authority on a device, using the imported account signing key. Stop the daemon first. `--previous-state` reads a protocol device-state document exported by `devices list --out`; revision options supply explicit integer revisions when needed. Otherwise the SDK resolves available state. Validity options default to 365 days, range 1–3650; optional `--relay` selects the recovery Relay. Returns SDK status, not proof that contacts, keys, or history have fully recovered. See [recovery](operations.md#account-authority-recovery).

Example: `meshline account recover --profile imported --relay "RELAY_ID" --previous-state ./device-state.json --timeout 180`

### account migrate

Usage: `meshline account migrate --relay ID`

Requires an established account and a destination registered Relay. Required `--relay` is passed to the SDK home-Relay migration workflow. Returns updated local SDK status. Cross-Relay live acceptance is still pending; it is not a local profile-folder move.

Example: `meshline account migrate --relay "DESTINATION_RELAY_ID"`

## Secrets

### secrets generate-key

Usage: `meshline secrets generate-key --out PATH`

Creates a private file containing 32 random binary bytes, with no account or application configuration required. `--out` is required; existing files are refused. Returns the absolute `{path}` without exposing the key.

Example: `meshline secrets generate-key --out ./secrets/agent.key`

### secrets rewrap

Usage: `meshline secrets rewrap --protection MODE [--key-file PATH] [--generate-key] [--credential-name NAME]`

Stop the daemon and unlock the old protection first. Required `--protection` and its companion options use the same mode rules as [account create](#account-create); passphrase mode prompts for the new passphrase. Rewrap verifies and saves the same master key under the new protection. Returns profile name, protection mode, and unchanged key ID. SDK data and application settings are not recreated. See [change protection](security.md#change-protection).

Example: `meshline secrets rewrap --protection file --key-file ./secrets/replacement.key --generate-key`

## Devices

Device operations use network authority and may require account signing. Device IDs are obtained from `devices list`; they are not profile names.

### devices list

Usage: `meshline devices list [--account ID] [--out PATH]`

Reads device state for the selected account or optional other `--account`. Returns `{state,devices}`, where each device includes `id` and its certificate. Optional `--out` saves only the protocol device state if present, suitable for recovery; it refuses an existing file. Null state creates no output file.

Example: `meshline devices list --out ./device-state.json --json`

### devices renew

Usage: `meshline devices renew [--days N]`

Renews this device's authorization and asks the SDK to publish device state to the home Relay. `--days` defaults to 365, range 1–3650. Returns `{device,publication}`; a staged publication is not yet proof of authoritative remote state.

Example: `meshline devices renew --days 365 --json`

### devices remove

Usage: `meshline devices remove ID`

Removes the specified device authorization using the account's authority. The device ID is required. Returns `{removed}` after the SDK call, not a guarantee that every peer has refreshed its state. Verify the ID before revoking it.

Example: `meshline devices remove "DEVICE_ID"`

## Network account profile

These commands manage public network profile information. The global `--profile` still selects the local account executing the command.

### account profile show

Usage: `meshline account profile show [--account ID]`

Reads the selected account's network profile, or another account with `--account`. Returns the SDK profile model, possibly null. This is not the local configuration returned by `account show`.

Example: `meshline account profile show --account "ACCOUNT_ID" --json`

### account profile update

Usage: `meshline account profile update [--nickname TEXT | --clear-nickname] [--bio TEXT | --clear-bio] [--avatar-file PATH | --clear-avatar] [--public-discovery true|false]`

Updates the selected account's profile. Omitted fields remain unchanged; each clear flag conflicts with its corresponding value option. `--avatar-file` is a protocol `ContentReference` JSON file, not an image upload. Returns the updated SDK profile. Background publication does not guarantee immediate visibility to all remote readers.

Example: `meshline account profile update --nickname "Alice" --bio "Available for messages" --public-discovery false`

## Contacts

Direct messaging depends on contact authorization. Invitation creation, local lists, requests, alias changes, and dismissal can run without starting networking; adding, accepting, and removing contacts start network activity when no daemon is present.

### contacts invite

Usage: `meshline contacts invite [--expires TIME] [--out PATH]`

Creates a contact invitation. `--expires` defaults to one day from now; use an ISO 8601 timestamp with a timezone. `--out` optionally writes the protocol document without overwriting. Returns `{document,path}`; without a file, `path` is null. Exchange the document with the intended participant.

Example: `meshline contacts invite --out ./invite.json`

### contacts add

Usage: `meshline contacts add (--account ID | --invite-file PATH) [--note TEXT]`

Supply exactly one account ID or saved protocol invitation. Optional `--note` accompanies the request. Returns the SDK contact-request result. Requesting access does not itself mean the other participant accepted it.

Example: `meshline contacts add --invite-file ./invite.json --note "Hello"`

### contacts requests

Usage: `meshline contacts requests [--account ID] [--direction Incoming|Outgoing|All] [--limit N]`

Reads locally synchronized requests. Account filtering is optional; direction defaults to `All`, limit to 200. Returns a local list wrapper. An empty result is not a remote synchronization guarantee.

Example: `meshline contacts requests --direction Incoming --json`

### contacts accept

Usage: `meshline contacts accept ACCOUNT`

Accepts an incoming request from the required account ID and returns the SDK contact result. The request must be available and valid for this account.

Example: `meshline contacts accept "ACCOUNT_ID"`

### contacts dismiss

Usage: `meshline contacts dismiss ACCOUNT`

Dismisses the required account's request in local SDK state. Returns `{dismissed}`; it does not start network synchronization on its own.

Example: `meshline contacts dismiss "ACCOUNT_ID"`

### contacts list

Usage: `meshline contacts list [--search TEXT] [--limit N]`

Reads local contacts with optional SDK search. Limit defaults to 200. Returns a local list wrapper and does not refresh or change read state.

Example: `meshline contacts list --search Alice --json`

### contacts show

Usage: `meshline contacts show ACCOUNT`

Reads a local contact by required account ID and returns the SDK contact model or null. It does not fetch a missing contact from the network.

Example: `meshline contacts show "ACCOUNT_ID" --json`

### contacts alias

Usage: `meshline contacts alias ACCOUNT (--alias TEXT | --clear)`

Changes a local alias for the required account, or clears it. Exactly one of `--alias` and `--clear` is required. Returns the SDK contact result; no network profile information is changed.

Example: `meshline contacts alias "ACCOUNT_ID" --alias "Work contact"`

### contacts remove

Usage: `meshline contacts remove ACCOUNT`

Removes the required contact through the SDK and returns `{removed}`. This is an authorization/contact operation, not a command to erase all historical copies of messages.

Example: `meshline contacts remove "ACCOUNT_ID"`

## Conversations

These commands use SDK-maintained local state. Conversation IDs are peer account IDs for direct conversations and group/channel IDs otherwise. They do not independently fetch new messages from Relays.

### conversations list

Usage: `meshline conversations list [--kind None|Direct|Group|Channel|All] [--unread] [--has-messages] [--limit N]`

Kind defaults to `All`; limit defaults to 200. `--unread` filters to unread conversations; `--has-messages` requires available messages. Returns local summaries including conversation ID, kind, latest preview, and unread count in a local list wrapper. Neither flag changes read state.

Example: `meshline conversations list --kind Direct --unread --json`

### conversations show

Usage: `meshline conversations show ID`

Returns one local conversation summary or null for the required ID. No command-specific options or implicit refresh.

Example: `meshline conversations show "CONVERSATION_ID" --json`

### conversations messages

Usage: `meshline conversations messages ID [--limit N] [--after SEQUENCE] [--before SEQUENCE]`

Dispatches to local direct-message, group-message, or channel-post history based on the existing conversation kind. Limit defaults to 50, selecting the latest entries when neither bound is supplied. Bounds select the adjacent page using each model's `localSequence`; each page is ascending. Returns a local history wrapper without marking messages read. A missing conversation returns `conversation_not_found`.

Example: `meshline conversations messages "CONVERSATION_ID" --limit 100 --json`

### conversations mark-read

Usage: `meshline conversations mark-read ID [--sequence LOCAL_SEQUENCE]`

With `--sequence`, marks the inclusive prefix through a positive `localSequence` returned for this conversation. It leaves later arrivals unread. The SDK rejects unavailable positions and never moves the marker backward. Returns `{conversation,semantics,localSequence}`, with `semantics` equal to `through-local-sequence`. Without the option, retains the mark-all behavior (`latest-local-at-call-time`, null `localSequence`), which can include arrivals after an earlier query. This is cumulative local read state, not an exact processing ACK.

Example: `meshline conversations mark-read "CONVERSATION_ID" --json`

## Direct messages

### messages send

Usage: `meshline messages send --to ACCOUNT (--text TEXT | --draft-file PATH) [--wait queued|relay|target]`

Requires the recipient account ID and exactly one content source. The peer must have usable contact authorization. `--wait` defaults to `relay`. Returns `{status,waitedFor}`; see [send outcomes](automation.md#send-outcomes-and-retries) before interpreting success or retrying. A draft uses the direct-message shape below. Sending does not mark a conversation read.

Example: `meshline messages send --to "ACCOUNT_ID" --text "Hello" --wait target --json`

### messages list

Usage: `meshline messages list [--peer ACCOUNT] [--limit N] [--after SEQUENCE] [--before SEQUENCE]`

Reads local direct-message history across peers or for the optional peer. Limit defaults to 50, selecting the latest entries without bounds. Bounds use the account-wide `localSequence` from this database; gaps are valid. Returns a local history wrapper with sender/message identity and sequence positions. Preserve the peer filter and database when continuing.

Example: `meshline messages list --peer "ACCOUNT_ID" --limit 50 --json`

### messages show

Usage: `meshline messages show ID --sender ACCOUNT`

Requires both the message ID and its sender account ID. Returns the matching local message or null. Message ID alone is not the complete lookup key.

Example: `meshline messages show "MESSAGE_ID" --sender "ACCOUNT_ID" --json`

### messages outbox

Usage: `meshline messages outbox [--to ACCOUNT] [--state STATE] [--limit N]`

Reads local persistent pending operations and retained terminal outcomes. State defaults to `All`; allowed values are `None`, `Queued`, `Submitting`, `SubmissionUnknown`, `RelayAccepted`, `TargetAccepted`, `Failed`, `Canceled`, and `All`. Limit defaults to 200. Returns a local list wrapper. The SDK retains the newest 1,000 terminal records account-wide, including internal protocol messages; pending records are outside that retention cap. An absent entry is not proof of successful delivery.

Example: `meshline messages outbox --state SubmissionUnknown --json`

### messages status

Usage: `meshline messages status ID`

Reads the pending or retained terminal status for the required message ID, including after restart. Returns `{status,absenceDoesNotProveDelivery:true}`; status is null for unknown or evicted records. This command does not initiate a resend.

Example: `meshline messages status "MESSAGE_ID" --json`

### messages cancel

Usage: `meshline messages cancel ID`

Asks the SDK to cancel a queued message identified by ID. Returns `{canceled}`; false means cancellation did not occur. It does not recall a message already accepted remotely.

Example: `meshline messages cancel "MESSAGE_ID"`

## Draft files

`--text` supplies a plain-text body. `--draft-file` reads JSON using the shapes below. They are mutually exclusive. Content references describe externally available attachments; the CLI does not upload or host files.

Direct message:

```json
{"body":{"content_type":"text/plain","text":"Hello"},"attachments":[],"replyTo":null}
```

To reply, replace `replyTo` with a reference such as:

```json
{"sender":"ACCOUNT_ID","messageId":"MESSAGE_ID"}
```

Group message:

```json
{"body":{"content_type":"text/plain","text":"Hello group"},"attachments":[],"replyToSequence":null}
```

Channel post:

```json
{"body":{"content_type":"text/plain","text":"News"},"attachments":[]}
```

Body fields are embedded protocol fields; outer draft fields use camelCase. A body may be null when valid attachments provide content, subject to SDK validation. A channel draft has no reply field. Signed invitation and device-state files should be exported through the corresponding commands rather than assembled from these draft examples.

## Groups

Group commands use encrypted groups and SDK/Relay authorization rules. An ID argument identifies the group. For commands accepting `--relay`, omission resolves the Relay from a locally known group; unknown groups require `--relay`. Creation instead defaults to the account's home Relay. Lists and message queries read local state; descriptor reads, remote pages, and mutations contact the Relay.

Unless a more specific result is described, successful mutations return `{group,operation,completed:true}` after the SDK call. The receipt does not certify that every member has synchronized. Local member/ban queries require a known group reference but do not force a refresh.

### groups create

Usage: `meshline groups create --name TEXT [--relay ID] [--description TEXT] [--capacity N] [--invite-policy POLICY]`

Creates a group owned by this account. Name is required; description is omitted by default. Capacity defaults to 32 and is subject to SDK/Relay limits. Policy is `Administrators` (default), `MembersTargeted`, or `MembersShareable`. Returns the SDK group model. The policies allow invitations by administrators only, ordinary members targeting one account, or ordinary members also sharing invitations, respectively.

Example: `meshline groups create --name "Team" --capacity 8 --invite-policy Administrators`

### groups list

Usage: `meshline groups list [--relay ID] [--membership STATE] [--role ROLE] [--limit N]`

Reads locally known groups. Optional filters default to no restriction; limit defaults to 200. Membership values: `Unknown`, `NotMember`, `Pending`, `Member`, `Left`, `Removed`, `Banned`. Role values: `Owner`, `Administrator`, `Member`. Returns a local list wrapper.

Example: `meshline groups list --membership Member --json`

### groups show

Usage: `meshline groups show ID [--relay ID]`

Fetches/refreshes a group from its Relay and returns the SDK group model. Supply `--relay` if the group is not already known locally. It is not a local-only snapshot query.

Example: `meshline groups show "GROUP_ID" --relay "RELAY_ID" --json`

### groups update

Usage: `meshline groups update ID [--relay ID] [--name TEXT] [--description TEXT | --clear-description] [--capacity N] [--invite-policy POLICY]`

Requires authority to update group metadata. Omitted fields remain unchanged. Policy values match create; `--description` conflicts with `--clear-description`. There is no implicit capacity or policy reset. Returns the updated SDK group model.

Example: `meshline groups update "GROUP_ID" --name "Project team" --clear-description`

### groups close

Usage: `meshline groups close ID [--relay ID]`

Closes an owned group through the SDK. Requires owner authority and returns the group operation receipt. This is not merely hiding the local conversation.

Example: `meshline groups close "GROUP_ID"`

### groups send

Usage: `meshline groups send ID [--relay ID] (--text TEXT | --draft-file PATH)`

Requires group membership, usable group keys, and exactly one content source. Uses the group draft shape, including optional `replyToSequence`. Returns the SDK group-message result. There is no direct-message-style `--wait` option, and success does not mean all members have read it.

Example: `meshline groups send "GROUP_ID" --text "Hello team"`

### groups sync

Usage: `meshline groups sync ID [--relay RELAY]`

Refreshes the home account timeline first to receive pending recovery material, then awaits a fresh group pass including keys and readable messages. The group must be known locally, or `--relay` must identify its host. Does not join the group or grant historical access. Returns the group synchronization snapshot on `CaughtUp`; missing keys or other unfinished processing return `sync_incomplete` (exit 6) with the snapshot in `error.details`. The combined work shares the command deadline. Background startup is not required.

Example: `meshline groups sync "GROUP_ID" --json`

### groups messages

Usage: `meshline groups messages ID [--sender ACCOUNT] [--limit N] [--after SEQUENCE] [--before SEQUENCE]`

Reads local readable group history without contacting a Relay. Sender filtering is optional; limit defaults to 50 and selects the latest entries without bounds. Bounds use this group's `localSequence` (an alias of the event sequence). Each page is ascending. Preserve the group and sender filter when continuing. It does not mark the group read.

Example: `meshline groups messages "GROUP_ID" --limit 100 --json`

### groups nickname

Usage: `meshline groups nickname ID [--relay ID] (--nickname TEXT | --clear)`

Changes or clears this account's nickname in the group. Requires membership and exactly one nickname action. Returns the group operation receipt; it does not change the global network profile nickname.

Example: `meshline groups nickname "GROUP_ID" --nickname "Alice"`

### groups invite create

Usage: `meshline groups invite create ID [--relay ID] [--expires TIME] [--account ACCOUNT | --max-uses N] [--out PATH]`

Requires invitation authority under the group's policy. Expiry defaults to one day from now; use ISO 8601 with timezone. `--account` binds the invitation to one account and conflicts with `--max-uses`. Without an account, the invitation is shareable; omitted usage count is left to SDK/Relay semantics. Optional `--out` writes the invitation envelope without overwriting. Returns `{relayId,document}`, the same shape expected by `groups join`.

Example: `meshline groups invite create "GROUP_ID" --max-uses 1 --out ./group-invite.json`

### groups invite list

Usage: `meshline groups invite list ID [--relay ID] [--limit N] [--cursor VALUE]`

Reads an authorized remote page of group invitations. Defaults to limit 50 and no cursor; returns `{items,nextCursor}`. Use invitation IDs from these results for show/revoke.

Example: `meshline groups invite list "GROUP_ID" --limit 50 --json`

### groups invite show

Usage: `meshline groups invite show ID [--relay ID] --invite INVITE_ID`

Reads one invitation's state from the group Relay, subject to its access rules. `--invite` is required. Returns the SDK invitation-state result.

Example: `meshline groups invite show "GROUP_ID" --invite "INVITE_ID" --json`

### groups invite revoke

Usage: `meshline groups invite revoke ID [--relay ID] --invite INVITE_ID`

Revokes the required invitation with appropriate authority and returns the group operation receipt. Revocation is not removal of members already admitted.

Example: `meshline groups invite revoke "GROUP_ID" --invite "INVITE_ID"`

### groups join

Usage: `meshline groups join --invite-file PATH`

Applies to join using the complete envelope saved by `groups invite create --out`, which includes the Relay and signed document. The file is required; do not substitute a contact invitation or only the inner document. Returns `{applied}` identifying the group. Application submission does not prove admission; approval and synchronization may still be required.

Example: `meshline groups join --invite-file ./group-invite.json`

### groups applications list

Usage: `meshline groups applications list ID [--relay ID] [--limit N] [--cursor VALUE]`

Reads an authorized remote page of admission applications. Defaults to limit 50 and no cursor; returns `{items,nextCursor}`.

Example: `meshline groups applications list "GROUP_ID" --json`

### groups applications approve

Usage: `meshline groups applications approve ID [--relay ID] --accounts LIST`

Approves pending applications for a required comma-separated list of account IDs, subject to group admission authority. Returns the group operation receipt. Applicants still need to synchronize membership and keys.

Example: `meshline groups applications approve "GROUP_ID" --accounts "ACCOUNT_A,ACCOUNT_B"`

### groups applications reject

Usage: `meshline groups applications reject ID [--relay ID] --accounts LIST`

Rejects pending applications for the required comma-separated account IDs, subject to admission authority. Returns the group operation receipt.

Example: `meshline groups applications reject "GROUP_ID" --accounts "ACCOUNT_ID"`

### groups members list

Usage: `meshline groups members list ID [--relay ID] [--role Owner|Administrator|Member] [--search TEXT] [--limit N]`

Reads locally synchronized members with optional role/search filtering. No filter is applied by default; limit defaults to 200. Returns a local list wrapper, not a remote membership refresh.

Example: `meshline groups members list "GROUP_ID" --role Administrator --json`

### groups members remove

Usage: `meshline groups members remove ID [--relay ID] --accounts LIST`

Removes the required comma-separated accounts under the group's authorization rules. Returns the group operation receipt. For this account to leave voluntarily, use `groups leave`.

Example: `meshline groups members remove "GROUP_ID" --accounts "ACCOUNT_ID"`

### groups members role

Usage: `meshline groups members role ID [--relay ID] --account ACCOUNT --role ROLE`

Assigns a role to a member, subject to SDK/Relay authority. Both account and role are required. The parser recognizes `Owner`, `Administrator`, and `Member`; use `groups transfer` for ownership transfer rather than assuming every parsed role assignment is permitted. Returns the group operation receipt.

Example: `meshline groups members role "GROUP_ID" --account "ACCOUNT_ID" --role Administrator`

### groups bans list

Usage: `meshline groups bans list ID [--relay ID] [--search TEXT] [--limit N]`

Reads locally synchronized bans with optional SDK search. Limit defaults to 200. Returns a local list wrapper without a network refresh.

Example: `meshline groups bans list "GROUP_ID" --json`

### groups bans add

Usage: `meshline groups bans add ID [--relay ID] --accounts LIST`

Bans the required comma-separated accounts under the group's authorization rules. Returns the group operation receipt.

Example: `meshline groups bans add "GROUP_ID" --accounts "ACCOUNT_ID"`

### groups bans remove

Usage: `meshline groups bans remove ID [--relay ID] --accounts LIST`

Removes bans for the required comma-separated accounts with appropriate authority. Returns the group operation receipt; removing a ban does not itself promise restored membership.

Example: `meshline groups bans remove "GROUP_ID" --accounts "ACCOUNT_ID"`

### groups transfer

Usage: `meshline groups transfer ID [--relay ID] --account ACCOUNT`

Transfers ownership to the required member account. Requires the current owner's authority and SDK/Relay eligibility checks. Returns the group operation receipt. Review the target carefully before transferring.

Example: `meshline groups transfer "GROUP_ID" --account "ACCOUNT_ID"`

### groups leave

Usage: `meshline groups leave ID [--relay ID]`

Leaves the group as the selected account, subject to its membership and ownership rules. Returns the group operation receipt. An owner may need to transfer ownership or close the group instead.

Example: `meshline groups leave "GROUP_ID"`

### groups keys rotate

Usage: `meshline groups keys rotate ID [--relay ID] [--owner-member-key]`

Requires ownership of an active group. Rotates the group secret; `--owner-member-key` additionally rotates the owner's member encryption key (default false). Returns the group operation receipt. An interrupted rotation must be reconciled using the original key-rotation choice before changing that choice.

Example: `meshline groups keys rotate "GROUP_ID" --owner-member-key`

### groups keys request

Usage: `meshline groups keys request ID [--relay ID]`

Requests group-key recovery for the selected account, subject to current membership. Returns the SDK recovery-request result. Submission does not mean that recovery was approved or keys are already available.

Example: `meshline groups keys request "GROUP_ID" --json`

### groups keys requests

Usage: `meshline groups keys requests ID [--relay ID] [--limit N] [--cursor VALUE]`

Reads an authorized remote page of group-key recovery requests. Defaults to limit 50 and no cursor; returns `{items,nextCursor}`.

Example: `meshline groups keys requests "GROUP_ID" --json`

### groups keys withdraw

Usage: `meshline groups keys withdraw ID [--relay ID]`

Withdraws this account's pending recovery request and returns the group operation receipt. It does not revoke another account's request or delete keys already recovered.

Example: `meshline groups keys withdraw "GROUP_ID"`

### groups keys approve

Usage: `meshline groups keys approve ID [--relay ID] --accounts LIST`

Approves recovery for the required comma-separated accounts using the authority and key material required by the SDK/Relay. Returns the group operation receipt; recipients must still synchronize the result.

Example: `meshline groups keys approve "GROUP_ID" --accounts "ACCOUNT_ID"`

### groups keys reject

Usage: `meshline groups keys reject ID [--relay ID] --accounts LIST`

Rejects pending recovery requests for the required comma-separated accounts with appropriate authority. Returns the group operation receipt.

Example: `meshline groups keys reject "GROUP_ID" --accounts "ACCOUNT_ID"`

## Channels

Channels contain public posts. For commands taking `--relay`, omission resolves a locally followed channel. An unknown or newly created but unfollowed channel requires explicit `--relay`. Creation defaults to the account's home Relay and **does not automatically follow the channel**. Local lists/posts read stored state; remote history reads can populate local storage.

Mutations without a model result return `{channel,operation,completed:true}`. Editing, deletion, and reporting use the original post sequence, not an edit-event sequence. Relay permissions govern creation, moderation, publishing, editing, and closure.

### channels create

Usage: `meshline channels create --name TEXT [--relay ID] [--description TEXT] [--moderators LIST]`

Creates a public channel. Name is required; description and comma-separated moderator account IDs are optional. Defaults to the home Relay when omitted. Returns the SDK channel model; save its ID and Relay for follow-up commands.

Example: `meshline channels create --name "Updates" --description "Project news"`

### channels list

Usage: `meshline channels list [--relay ID] [--limit N]`

Reads locally followed channels, optionally filtered by Relay. Limit defaults to 200. Returns a local list wrapper; an owned but unfollowed channel need not appear.

Example: `meshline channels list --json`

### channels show

Usage: `meshline channels show ID [--relay ID]`

Reads the channel descriptor from its Relay and returns the SDK channel model. An unfollowed channel requires `--relay` for reference resolution.

Example: `meshline channels show "CHANNEL_ID" --relay "RELAY_ID" --json`

### channels update

Usage: `meshline channels update ID [--relay ID] [--name TEXT] [--description TEXT | --clear-description] [--moderators LIST | --clear-moderators]`

Requires authority to update the descriptor. Omitted fields remain unchanged. A supplied comma-separated moderator list replaces the list; `--clear-moderators` empties it. Clear flags conflict with the corresponding value options. Returns the updated SDK channel model.

Example: `meshline channels update "CHANNEL_ID" --relay "RELAY_ID" --name "Release news" --clear-description`

### channels close

Usage: `meshline channels close ID [--relay ID]`

Closes an owned channel with owner authority and returns the channel operation receipt. For local subscription changes, use unfollow instead.

Example: `meshline channels close "CHANNEL_ID" --relay "RELAY_ID"`

### channels follow

Usage: `meshline channels follow ID [--relay ID]`

Follows a public channel and lets the SDK synchronize its content. Supply `--relay` when not already followed. Returns the channel operation receipt; it is not a guarantee that all history has loaded.

Example: `meshline channels follow "CHANNEL_ID" --relay "RELAY_ID"`

### channels unfollow

Usage: `meshline channels unfollow ID [--relay ID]`

Stops following the channel and returns the channel operation receipt. It does not close the public channel or imply deletion of every locally stored post.

Example: `meshline channels unfollow "CHANNEL_ID"`

### channels publish

Usage: `meshline channels publish ID [--relay ID] (--text TEXT | --draft-file PATH)`

Requires publishing authority and exactly one content source. Drafts use the channel shape above. Returns the SDK post result; the post is public. There is no `--wait` milestone option.

Example: `meshline channels publish "CHANNEL_ID" --relay "RELAY_ID" --text "First post"`

### channels edit

Usage: `meshline channels edit ID [--relay ID] --sequence N (--text TEXT | --draft-file PATH) [--clear-attachments]`

Requires edit authority, the original post sequence, and exactly one content source. With `--text`, existing attachments are preserved; with `--draft-file`, the draft's attachments replace them. `--clear-attachments` explicitly empties attachments and takes precedence over a draft attachment list. A draft with `body:null` requests body deletion, subject to SDK content validation. Returns the SDK edited-post result.

Example: `meshline channels edit "CHANNEL_ID" --relay "RELAY_ID" --sequence 1 --text "Updated post"`

### channels delete

Usage: `meshline channels delete ID [--relay ID] --sequence N`

Deletes the post at the required original sequence using channel moderation/author authority as enforced by the Relay. Returns the channel operation receipt. It cannot guarantee removal of copies already retrieved by readers.

Example: `meshline channels delete "CHANNEL_ID" --relay "RELAY_ID" --sequence 1`

### channels report

Usage: `meshline channels report ID [--relay ID] --sequence N --reason TEXT`

Submits a report for the required original post sequence; reason is required. Returns the channel operation receipt after submission, not a moderation decision.

Example: `meshline channels report "CHANNEL_ID" --relay "RELAY_ID" --sequence 1 --reason "Spam"`

### channels sync

Usage: `meshline channels sync ID [--relay RELAY]`

Awaits a fresh forward synchronization pass for the descriptor and timeline. The channel must be known locally, or `--relay` must identify its host. Does not follow the channel or start background workers. Returns a synchronization snapshot on `CaughtUp`; unfinished processing returns `sync_incomplete` (exit 6). This does not establish complete historical coverage; use `channels history` for remote backward history pages.

Example: `meshline channels sync "CHANNEL_ID" --json`

### channels posts

Usage: `meshline channels posts ID [--author ACCOUNT] [--limit N] [--after SEQUENCE] [--before SEQUENCE]`

Reads locally stored nondeleted posts without resolving a Relay. Author filtering is optional; limit defaults to 50 and selects the latest entries without bounds. Bounds use `localSequence`, an alias of the original publication sequence within this channel. Edits retain that position. Returns a local history wrapper and does not mark the conversation read.

Example: `meshline channels posts "CHANNEL_ID" --limit 100 --json`

### channels history

Usage: `meshline channels history ID [--relay ID] [--limit N] [--cursor VALUE]`

Loads a page of historical posts from the Relay into the SDK. Defaults to limit 50 and no cursor; returns `{items,nextCursor}`. This is a remote history operation, unlike `channels posts`.

Example: `meshline channels history "CHANNEL_ID" --relay "RELAY_ID" --limit 50 --json`

## Daemon and events

There is one exclusive SDK session per profile. Separate profiles can run concurrently. A daemon has no interactive shell; open another terminal and issue normal CLI commands for the same configuration/profile. See [operations](operations.md) for supervision, IPC, and shutdown.

### daemon run

Usage: `meshline daemon run`

Unlocks the selected profile and runs its SDK session in the foreground. It emits readiness after startup and stays running until canceled or stopped. Default timeout is unlimited; diagnostics go to stderr. Use this under a service supervisor. The exposed `--unlock-stdin` flag is reserved for `daemon start`'s private child pipe, not a supported user credential-input interface.

Example: `meshline daemon run --profile agent --json`

### daemon start

Usage: `meshline daemon start`

Unlocks once, launches a background child, and waits for readiness. Returns `{running,processId,diagnostics}` when started or `{running:true,alreadyRunning:true}` if reachable already. The default timeout bounds startup, not the lifetime of the started daemon. Windows uses an ordinary hidden process, not a Windows Service.

Example: `meshline daemon start --profile agent --timeout 180 --json`

### daemon status

Usage: `meshline daemon status`

Checks local IPC without unlocking or opening the SDK database. Returns `{running:false}` when no daemon is reachable, or `{running:true,processId,account}`. The profile must exist. This is process reachability, not network synchronization status.

Example: `meshline daemon status --profile agent --json`

### daemon stop

Usage: `meshline daemon stop`

Requests graceful shutdown over IPC without unlocking. Returns `{stopping:true}` when accepted or `{running:false}` if no daemon is reachable. A response is not proof that process cleanup has completed; wait for exit before backup or replacement.

Example: `meshline daemon stop --profile agent --json`

### watch

Usage: `meshline watch`

Streams SDK notifications as NDJSON, with or without `--json`. It uses the daemon when available; otherwise it owns a foreground SDK session and starts networking. Default timeout is unlimited. Events have no replay guarantee and never automatically mark read. See [live events](automation.md#live-events) for event types, overflow, and terminal errors.

Example: `meshline watch --profile agent --json`
