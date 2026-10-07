# Security and key protection

[Documentation home](../README.md#documentation) · [Backup and restore](operations.md#backup-restore-and-upgrade)

## Choose a protection mode

Protection is selected explicitly at account creation/import. The CLI does not silently fall back to another mode.

| Mode | Unlocking mechanism | Operational requirement |
| --- | --- | --- |
| `file` | An external file containing 32 random binary bytes | Keep the file accessible only to the intended user/administrators and include it in protected backups. Suitable for unattended execution. |
| `native` on Windows | DPAPI `CurrentUser` | Use the original Windows user/environment or an independently tested migration procedure. |
| `native` on Linux | A credential file delivered by systemd | Use a service credential environment and the configured `--credential-name`; see [deployment](operations.md#linux-systemd). |
| `passphrase` | A key derived from an interactively entered passphrase | Requires a terminal when unlocking; an operator may unlock once before leaving a daemon running. |

A profile wrapping key is different from a NEP-6 wallet password. `--password-file` supplies the password for wallet import/export; it does not unlock a passphrase-protected profile. Do not put secrets in command arguments or ordinary environment variables.

## Credential files and profile creation

Generate a wrapping key using `secrets generate-key --out PATH` or create/import with `--protection file --key-file PATH --generate-key`. The file contains binary key material, not a password or base64 text. Existing files are never overwritten. Keep it outside the new profile directory.

Credential checks reject invalid sizes, symbolic links, and overly broad permissions. On Windows, access is restricted to the current user, SYSTEM, and Administrators; on Linux, group/other access is not allowed for ordinary private credential files. Do not resolve permission errors by making secrets world-readable.

Account create/import prepares identity, protected key, and configuration in a private temporary profile, then publishes the directory atomically. Import verifies the wallet and selected account first. If creation fails after generating an external key file, that file can remain; inspect it before retrying, and reuse it without `--generate-key` when appropriate. An existing profile directory is never replaced.

## What is protected

The CLI generates a random 32-byte master key and protects it using the selected mode. SDK secrets are encrypted with AES-256-GCM and bound to the profile identity, network, and SDK purpose. The randomly chosen password of the local NEP-6 wallet is protected through this vault as well.

Passphrase wrapping uses Argon2id v1.3 with 64 MiB memory, three iterations, parallelism one, and a random 16-byte salt. AES-GCM uses a random 12-byte nonce and 16-byte authentication tag. Envelopes are versioned. These are current implementation parameters, not configuration options to edit in a profile.

Message bodies, contacts, and metadata in SQLite are **not encrypted as a complete database**. Protect the data directory and backups with appropriate access control and disk encryption. Key protection does not defend against a process that already controls the operating-system user session. Explicit byte-key buffers are cleared when released, but managed strings and wallet objects cannot guarantee complete memory erasure.

For Linux native protection, the CLI reads the supplied credential; it cannot determine from the resulting plaintext file whether the administrator used an encrypted source. Configure `LoadCredentialEncrypted` when that is the intended protection. systemd manages source decryption and access to the runtime credential directory; see its [credential documentation](https://systemd.io/CREDENTIALS/).

## Change protection

Stop the profile's daemon and confirm that it has exited before running rewrap. The old credential must still work:

```sh
meshline daemon stop --profile agent
meshline daemon status --profile agent --json
meshline secrets rewrap --profile agent --protection file --key-file ./secrets/new-agent.key --generate-key
```

The CLI unlocks with the old mode, wraps and verifies the same master key using the new mode, then atomically replaces the profile manifest. The key ID and SDK data remain unchanged. A failure preserves the old manifest; a newly generated but unused external key may remain and requires operator review.

For a passphrase destination, choose `--protection passphrase` in a terminal and enter the new passphrase. For Windows DPAPI, use `--protection native`. Linux native rewrap must run with the destination systemd credential available. The source protection's requirements still apply throughout the operation.

Do not manually edit the profile ID, network, encryption envelope, or protection mode as a substitute for rewrap. Do not discard the old recovery material until a protected backup and intended restore path have been verified.

## Backup boundaries

| Material | What it can restore |
| --- | --- |
| Application `config.json` only | Application selection and storage paths; no account identity |
| Encrypted wallet export plus its password | Account signing authority for import/recovery; no local history |
| Complete profile/data plus usable unlocking material | The stored device state, history, and encrypted secrets, subject to database/runtime compatibility |

Native protection may depend on the original operating-system account or machine. Rewrap into an appropriate portable mode before migration when needed. Keep account authority recovery distinct from historical message recovery: missing contacts, grants, or group keys can still prevent decryption. Follow the [operational backup procedure](operations.md#backup-restore-and-upgrade) and [recovery guidance](operations.md#account-authority-recovery).
