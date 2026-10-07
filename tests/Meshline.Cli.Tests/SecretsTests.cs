using Meshline.Cli;
using Meshline.Interactions;
using System.Security.Cryptography;
using System.Text;

namespace Meshline.Cli.Tests;

internal sealed class TemporaryProfile : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "meshline-cli-test-" + Guid.NewGuid().ToString("N"));
    public ProfileContext Context { get; }
    public TemporaryProfile(string mode = "file")
    {
        PrivateFiles.EnsureDirectory(DirectoryPath);
        Context = new(Path.Combine(DirectoryPath, "config.json"), "test", new()
        {
            Id = Guid.NewGuid().ToString("N"), Network = "neo:123:0x" + new string('a', 40), RpcUrl = "http://localhost:10332",
            DataDirectory = Path.Combine(DirectoryPath, "profile"), Protection = new(mode, Path.Combine(DirectoryPath, "credential.key"))
        });
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config.json"), Context.ConfigPath);
        PrivateFiles.EnsureDirectory(Context.DataDirectory);
    }
    public void ReleaseDatabaseConnections()
    {
        // EF disposes connections into SQLite pools; release only this fixture's pools before deleting its files.
        foreach (var mode in new[] { Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate })
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Context.DatabasePath, Mode = mode }.ToString());
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }
    }
    public void Dispose()
    {
        ReleaseDatabaseConnections();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}

public sealed class SecretsTests
{
    [Fact]
    public async Task Import_keeps_only_the_selected_account_and_does_not_change_the_source()
    {
        using var fixture = new TemporaryProfile();
        var token = TestContext.Current.CancellationToken;
        const string password = "multiple-accounts-test";
        var wallet = new Neo.Wallets.NEP6.NEP6Wallet(Path.Combine(fixture.DirectoryPath, "unused.json"), password, Neo.ProtocolSettings.Default);
        wallet.CreateAccount(); wallet.CreateAccount();
        var source = wallet.ToJson().ToString();
        var path = Path.Combine(fixture.DirectoryPath, "source.json");
        await PrivateFiles.WriteAtomicAsync(path, Encoding.UTF8.GetBytes(source), false, token);
        using var expected = Nep6AccountSigner.Parse(source, password, fixture.Context.Network, 1);
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, token);
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, token);
        using var vault = await Secrets.OpenAsync(fixture.Context, token);
        var identity = await Identity.ImportAsync(fixture.Context, vault, path, password, 1, token);
        Assert.Equal(expected.AccountId, identity.AccountId);
        using var stored = System.Text.Json.JsonDocument.Parse(identity.WalletJson);
        Assert.Equal(1, stored.RootElement.GetProperty("accounts").GetArrayLength());
        Assert.Equal(0, identity.AccountIndex);
        Assert.Equal(source, await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task File_protection_reopens_and_rejects_tampering_and_wrong_purpose()
    {
        using var fixture = new TemporaryProfile();
        var token = TestContext.Current.CancellationToken;
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, token);
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, token);
        byte[] encrypted;
        using (var first = await Secrets.OpenAsync(fixture.Context, token))
            encrypted = await first.ProtectAsync(Encoding.UTF8.GetBytes("device-secret"), "device/signing", token);
        using var second = await Secrets.OpenAsync(fixture.Context, token);
        Assert.Equal("device-secret", Encoding.UTF8.GetString(await second.UnprotectAsync(encrypted, "device/signing", token)));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => second.UnprotectAsync(encrypted, "device/encryption", token));
        encrypted[^1] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => second.UnprotectAsync(encrypted, "device/signing", token));
    }

    [Fact]
    public async Task Passphrase_rewrap_retains_master_and_wrong_password_fails()
    {
        using var fixture = new TemporaryProfile("passphrase");
        var token = TestContext.Current.CancellationToken;
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, token, "test-password-one");
        using var first = await Secrets.OpenAsync(fixture.Context, token, "test-password-one");
        var encrypted = await first.ProtectAsync(new byte[] { 1, 2, 3 }, "secret", token);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Secrets.OpenAsync(fixture.Context, token, "wrong"));
        var master = first.CopyMasterKey();
        try
        {
            fixture.Context.Settings.ProtectedKey = await Secrets.WrapAsync(fixture.Context, fixture.Context.Settings.Protection, master,
                fixture.Context.Settings.ProtectedKey.KeyId, token, "test-password-two");
        }
        finally { CryptographicOperations.ZeroMemory(master); }
        using var second = await Secrets.OpenAsync(fixture.Context, token, "test-password-two");
        Assert.Equal(new byte[] { 1, 2, 3 }, await second.UnprotectAsync(encrypted, "secret", token));
    }

    [Fact]
    public async Task Protection_is_bound_to_profile_identity()
    {
        using var fixture = new TemporaryProfile();
        var token = TestContext.Current.CancellationToken;
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, token);
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, token);
        fixture.Context.Settings.Id = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Secrets.OpenAsync(fixture.Context, token));
    }

    [Fact]
    public async Task Key_generation_never_overwrites_an_existing_credential()
    {
        using var fixture = new TemporaryProfile();
        var path = fixture.Context.Settings.Protection.KeyFile!;
        var token = TestContext.Current.CancellationToken;
        await Secrets.GenerateKeyFileAsync(path, token);
        var original = await File.ReadAllBytesAsync(path, token);
        await Assert.ThrowsAsync<IOException>(() => Secrets.GenerateKeyFileAsync(path, token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, token));
    }

    [Fact]
    public async Task Wallet_creation_export_and_import_keep_the_same_identity()
    {
        using var fixture = new TemporaryProfile();
        var token = TestContext.Current.CancellationToken;
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, token);
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, token);
        using var vault = await Secrets.OpenAsync(fixture.Context, token);
        var identity = await Identity.CreateAsync(fixture.Context, vault, token);
        var export = Path.Combine(fixture.DirectoryPath, "export.json");
        await Identity.ExportAsync(fixture.Context, vault, export, "export-test-password", token);
        using var signer = Nep6AccountSigner.Load(export, "export-test-password", fixture.Context.Network);
        Assert.Equal(identity.AccountId, signer.AccountId);
        Assert.DoesNotContain("export-test-password", await File.ReadAllTextAsync(fixture.Context.IdentityPath, token));
        using var imported = new TemporaryProfile();
        await Secrets.GenerateKeyFileAsync(imported.Context.Settings.Protection.KeyFile!, token);
        imported.Context.Settings.ProtectedKey = await Secrets.CreateAsync(imported.Context, token);
        using var importedVault = await Secrets.OpenAsync(imported.Context, token);
        var copy = await Identity.ImportAsync(imported.Context, importedVault, export, "export-test-password", 0, token);
        Assert.Equal(identity.AccountId, copy.AccountId);
        var delegated = new VaultAccountSigner(imported.Context, copy, importedVault);
        var payload = Encoding.UTF8.GetBytes("verify signing after import");
        var signature = await delegated.SignAsync(payload, token);
        using var verifier = ECDsa.Create();
        var curve = Org.BouncyCastle.Asn1.Sec.SecNamedCurves.GetByName("secp256r1");
        var point = curve.Curve.DecodePoint(signer.PublicKey.ToArray()).Normalize();
        verifier.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point.AffineXCoord.GetEncoded(), Y = point.AffineYCoord.GetEncoded() }
        });
        Assert.True(verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

}
