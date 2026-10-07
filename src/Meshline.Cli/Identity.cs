using Meshline.Interactions;
using Neo;
using Neo.Wallets.NEP6;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Cli;

internal sealed record IdentityDocument(int Version, string AccountId, string PublicKey, string Address, string WalletJson, int AccountIndex, string ProtectedPassword);
internal sealed record ImportedWallet(string Json, string Password);

internal static class Identity
{
    public static async Task<IdentityDocument> LoadAsync(ProfileContext context, CancellationToken token)
    {
        if (!File.Exists(context.IdentityPath)) throw new CliException("profile_incomplete", "The profile has no account identity. Restore the complete profile from backup.", Exit.Configuration);
        var identity = Json.Read<IdentityDocument>(await File.ReadAllTextAsync(context.IdentityPath, token));
        if (identity.Version != 1) throw new CliException("identity_version", "Unsupported identity format.", Exit.Configuration);
        if (string.IsNullOrWhiteSpace(identity.AccountId) || string.IsNullOrWhiteSpace(identity.Address)
            || string.IsNullOrWhiteSpace(identity.PublicKey) || string.IsNullOrWhiteSpace(identity.WalletJson)
            || string.IsNullOrWhiteSpace(identity.ProtectedPassword) || identity.AccountIndex != 0)
            throw new CliException("profile_incomplete", "The profile has an incomplete account identity. Restore the complete profile from backup.", Exit.Configuration);
        return identity;
    }

    public static async Task<IdentityDocument> CreateAsync(ProfileContext context, Vault vault, CancellationToken token)
    {
        EnsureAbsent(context);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var wallet = new NEP6Wallet(Path.Combine(context.DataDirectory, Guid.NewGuid().ToString("N") + ".wallet"), password,
            ProtocolSettings.Default with { Network = context.Network.Reference }, "Meshline");
        wallet.CreateAccount();
        return await SaveAsync(context, vault, wallet.ToJson().ToString(), password, 0, token);
    }

    public static async Task<IdentityDocument> ImportAsync(ProfileContext context, Vault vault, string walletPath, string password, int accountIndex, CancellationToken token)
    {
        EnsureAbsent(context);
        var wallet = await PrepareImportAsync(context.Network, walletPath, password, accountIndex, token);
        return await ImportPreparedAsync(context, vault, wallet, token);
    }

    public static async Task<ImportedWallet> PrepareImportAsync(Meshline.Models.NetworkContext network, string walletPath, string password, int accountIndex, CancellationToken token)
    {
        var source = await File.ReadAllTextAsync(walletPath, token);
        using var selected = Nep6AccountSigner.Parse(source, password, network, accountIndex);
        var wallet = JsonNode.Parse(source)!.AsObject();
        var accounts = wallet["accounts"]!.AsArray();
        var account = accounts[accountIndex]!.DeepClone();
        accounts.Clear(); accounts.Add(account);
        return new(wallet.ToJsonString(), password);
    }

    public static Task<IdentityDocument> ImportPreparedAsync(ProfileContext context, Vault vault, ImportedWallet wallet, CancellationToken token)
    {
        EnsureAbsent(context);
        return SaveAsync(context, vault, wallet.Json, wallet.Password, 0, token);
    }

    static void EnsureAbsent(ProfileContext context)
    {
        if (File.Exists(context.IdentityPath) || File.Exists(context.DatabasePath))
            throw new CliException("identity_exists", "This profile already contains identity or device data. Use a different profile rather than replacing it.", Exit.Configuration);
    }

    static async Task<IdentityDocument> SaveAsync(ProfileContext context, Vault vault, string wallet, string password, int index, CancellationToken token)
    {
        using var signer = Nep6AccountSigner.Parse(wallet, password, context.Network, index);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var protectedPassword = await vault.ProtectAsync(passwordBytes, "wallet-password", token);
            var document = new IdentityDocument(1, signer.AccountId, Convert.ToBase64String(signer.PublicKey.AsSpan()), signer.Address, wallet, index, Convert.ToBase64String(protectedPassword));
            await PrivateFiles.WriteAtomicAsync(context.IdentityPath, JsonSerializer.SerializeToUtf8Bytes(document, Json.Options), false, token);
            return document;
        }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    public static async Task ExportAsync(ProfileContext context, Vault vault, string outputPath, string newPassword, CancellationToken token)
    {
        var document = await LoadAsync(context, token);
        var clear = await vault.UnprotectAsync(Convert.FromBase64String(document.ProtectedPassword), "wallet-password", token);
        try
        {
            var oldPassword = Encoding.UTF8.GetString(clear);
            var wallet = new NEP6Wallet(outputPath, oldPassword, ProtocolSettings.Default with { Network = context.Network.Reference },
                (Neo.Json.JObject)Neo.Json.JToken.Parse(document.WalletJson)!);
            if (!wallet.ChangePassword(oldPassword, newPassword)) throw new CliException("wallet_password", "Could not re-encrypt the wallet.", Exit.Credentials);
            var json = wallet.ToJson().ToString();
            using var verify = Nep6AccountSigner.Parse(json, newPassword, context.Network, document.AccountIndex);
            if (verify.AccountId != document.AccountId) throw new CryptographicException("Export identity mismatch.");
            await PrivateFiles.WriteAtomicAsync(outputPath, Encoding.UTF8.GetBytes(json), false, token);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}

internal sealed class VaultAccountSigner(ProfileContext context, IdentityDocument identity, Vault vault) : IAccountSigner
{
    public string AccountId => identity.AccountId;
    public ImmutableArray<byte> PublicKey { get; } = [.. Convert.FromBase64String(identity.PublicKey)];
    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var clear = await vault.UnprotectAsync(Convert.FromBase64String(identity.ProtectedPassword), "wallet-password", cancellationToken);
        try
        {
            using var signer = Nep6AccountSigner.Parse(identity.WalletJson, Encoding.UTF8.GetString(clear), context.Network, identity.AccountIndex);
            if (signer.AccountId != AccountId || !signer.PublicKey.AsSpan().SequenceEqual(PublicKey.AsSpan())) throw new CryptographicException("Stored wallet identity mismatch.");
            return await signer.SignAsync(data, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
