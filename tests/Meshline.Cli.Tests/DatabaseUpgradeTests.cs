using Meshline.Models.Protocol;
using Meshline.Storage.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Meshline.Cli.Tests;

public sealed class DatabaseUpgradeTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static readonly byte[] Challenge = "device keys survive the CLI upgrade"u8.ToArray();

    [Fact]
    public async Task Opening_an_SDK_1_2_profile_preserves_keys_state_contacts_and_history_across_restarts()
    {
        LegacySnapshot? legacy = null;
        await using var fixture = await SessionFixture.OpenAsync(async context => legacy = await CreateLegacyDatabaseAsync(context));
        Assert.NotNull(legacy);
        await VerifyAsync(fixture, legacy);
        await fixture.ReopenAsync();
        await VerifyAsync(fixture, legacy);
    }

    sealed record LegacySnapshot(DeviceCertificate Certificate, AccountDeviceState State, string Peer,
        byte[] IdentityFile, byte[] ProtectedSigningKey, byte[] ProtectedEncryptionKey,
        byte[] Signature, byte[] PeerEncryptionKey, byte[] SharedSecret, string Grant, string MessageId, string Payload);

    static async Task<LegacySnapshot> CreateLegacyDatabaseAsync(ProfileContext context)
    {
        using var vault = await Secrets.OpenAsync(context, Token);
        var identity = await Identity.LoadAsync(context, Token);
        var signer = new VaultAccountSigner(context, identity, vault);
        using var keys = await TestDeviceKeys.CreateAsync(signer, context.Network);
        using var peerSigner = new TestAccountSigner(context.Network);
        using var peerKeys = await TestDeviceKeys.CreateAsync(peerSigner, context.Network);
        var certificate = keys.Certificate;
        var deviceId = certificate.GetDeviceId(context.Network);
        var signingKey = await vault.ProtectAsync(keys.SigningKey, $"Meshline/device-signing/v1/{context.Network}/{identity.AccountId}/{deviceId}", Token);
        var encryptionKey = await vault.ProtectAsync(keys.EncryptionKey, $"Meshline/device-encryption/v1/{context.Network}/{identity.AccountId}/{deviceId}", Token);
        var state = new AccountDeviceState { Account = identity.AccountId, AccountPublicKey = signer.PublicKey, Revision = 7, Certificates = [certificate], AccountSignature = [] };
        state = state with { AccountSignature = [.. await signer.SignAsync(state.GetSigningInput(context.Network), Token)] };
        var grant = new ContactGrant { Grantor = identity.AccountId, Grantee = peerSigner.AccountId, Signatures = System.Collections.Immutable.ImmutableDictionary<string, System.Collections.Immutable.ImmutableArray<byte>>.Empty };
        grant = grant with { Signatures = grant.Signatures.Add(deviceId, [.. keys.Sign(grant.GetSigningInput(context.Network))]) };
        var messageId = Identifiers.CreateMessageId();
        var payload = new DirectMessage { Body = new() { ContentType = "text/plain", Text = "History from SDK 1.2" } }.ToJson();

        // InitialCreate is the schema shipped by SDK 1.2. Only that migration is marked applied.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = context.DatabasePath, Pooling = false }.ToString();
        await using (var database = new DbContext(new DbContextOptionsBuilder().UseSqlite(connectionString).Options))
        {
            var statements = database.GetService<IMigrationsSqlGenerator>().Generate(new InitialCreate().UpOperations);
            foreach (var statement in statements) await database.Database.ExecuteSqlRawAsync(statement.CommandText, Token);
        }
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO __EFMigrationsHistory VALUES ('20260925135953_InitialCreate', '10.0.0');
            INSERT INTO DatabaseBinding (Id, Context, AccountId, DeviceId) VALUES (1, $context, $account, $device);
            INSERT INTO LocalDevice (DeviceId, CertificateJson, ProtectedSigningKey, ProtectedEncryptionKey, Version)
                VALUES ($device, $certificate, $signing, $encryption, 1);
            INSERT INTO DeviceStates (AccountId, DocumentJson, Revision) VALUES ($account, $state, 7);
            INSERT INTO Contacts (AccountId, Alias, State, UpdatedAt, GrantToJson, ConfirmedGrantToJson)
                VALUES ($peer, 'Legacy peer', $active, 123, $grant, $grant);
            INSERT INTO Messages (LocalSequence, Sender, MessageId, SenderDeviceId, Recipient, CreatedAt, PayloadType, PayloadJson, IsDirect)
                VALUES (10, $peer, $message, $peerDevice, $account, 123, 'meshline.message.direct', $payload, 1);
            """;
        foreach (var (name, value) in new (string, object)[]
        {
            ("$context", context.Network.ToString()), ("$account", identity.AccountId), ("$device", deviceId),
            ("$certificate", certificate.ToJson()), ("$signing", signingKey), ("$encryption", encryptionKey),
            ("$state", state.ToJson()), ("$peer", peerSigner.AccountId), ("$active", (int)ContactRelationshipState.Active),
            ("$grant", grant.ToJson()), ("$message", messageId), ("$peerDevice", peerKeys.Certificate.GetDeviceId(context.Network)), ("$payload", payload)
        }) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Token);
        return new(certificate, state, peerSigner.AccountId, await File.ReadAllBytesAsync(context.IdentityPath, Token),
            signingKey, encryptionKey, keys.Sign(Challenge), peerKeys.Certificate.EncryptionPublicKey.ToArray(),
            keys.DeriveSharedSecret(peerKeys.Certificate.EncryptionPublicKey.AsSpan()), grant.ToJson(), messageId, payload);
    }

    static async Task VerifyAsync(SessionFixture fixture, LegacySnapshot legacy)
    {
        var context = fixture.Profile.Context;
        var client = fixture.Session.Client;
        Assert.Equal(legacy.Certificate.Account, client.Options.AccountId);
        Assert.Equal(legacy.IdentityFile, await File.ReadAllBytesAsync(context.IdentityPath, Token));
        Assert.Equal(legacy.Certificate.ToJson(), client.Device!.ToJson());
        Assert.Equal(legacy.State.ToJson(), client.DeviceState!.ToJson());
        Assert.Equal(legacy.Signature, await client.DeviceManager.SignAsync(Challenge, Token));
        Assert.Equal(legacy.SharedSecret, await client.DeviceManager.DeriveSharedSecretAsync(legacy.PeerEncryptionKey, Token));
        var contact = Assert.Single((await fixture.RunAsync("contacts list")).GetProperty("items").EnumerateArray());
        Assert.Equal(legacy.Peer, contact.GetProperty("accountId").GetString());
        Assert.Equal("Legacy peer", contact.GetProperty("alias").GetString());
        var message = Assert.Single((await fixture.RunAsync("messages list", ("peer", legacy.Peer))).GetProperty("items").EnumerateArray());
        Assert.Equal(10, message.GetProperty("localSequence").GetInt64());

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = context.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.Context, b.AccountId, b.DeviceId, d.ProtectedSigningKey, d.ProtectedEncryptionKey,
                c.DeviceNotificationRevision, m.MessageId, m.PayloadJson, c.GrantToJson, c.ConfirmedGrantToJson
            FROM DatabaseBinding b JOIN LocalDevice d ON d.DeviceId = b.DeviceId
            JOIN Contacts c ON c.AccountId = $peer JOIN Messages m ON m.LocalSequence = 10 WHERE b.Id = 1;
            """;
        command.Parameters.AddWithValue("$peer", legacy.Peer);
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(context.Network.ToString(), reader.GetString(0));
            Assert.Equal(client.Options.AccountId, reader.GetString(1));
            Assert.Equal(legacy.Certificate.GetDeviceId(context.Network), reader.GetString(2));
            Assert.Equal(legacy.ProtectedSigningKey, (byte[])reader[3]);
            Assert.Equal(legacy.ProtectedEncryptionKey, (byte[])reader[4]);
            Assert.Equal(legacy.State.Revision, reader.GetInt64(5));
            Assert.Equal(legacy.MessageId, reader.GetString(6));
            Assert.Equal(legacy.Payload, reader.GetString(7));
            Assert.Equal(legacy.Grant, reader.GetString(8));
            Assert.Equal(legacy.Grant, reader.GetString(9));
            Assert.False(await reader.ReadAsync(Token));
        }
        command.CommandText = "SELECT COUNT(*) FROM MessageOutbox";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Token));
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = '20261026130238_ContactDeviceNotifications'";
        Assert.Equal(1L, await command.ExecuteScalarAsync(Token));
    }
}
