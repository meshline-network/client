using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Protocol;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Meshline.Cli.Tests;

internal sealed class TestAccountSigner(NetworkContext context) : IAccountSigner, IDisposable
{
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public ImmutableArray<byte> PublicKey
    {
        get
        {
            var q = key.ExportParameters(false).Q;
            return [(byte)(2 + (q.Y![^1] & 1)), .. q.X!];
        }
    }
    public string AccountId => AccountAdapter.GetAccountId($"neo:{context.Reference}", PublicKey.AsSpan());
    public byte[] Sign(ReadOnlySpan<byte> data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Sign(data.Span));
    }
    public void Dispose() => key.Dispose();
}

internal sealed class TestDeviceKeys : IDisposable
{
    public byte[] SigningKey { get; } = RandomNumberGenerator.GetBytes(32);
    public byte[] EncryptionKey { get; } = RandomNumberGenerator.GetBytes(32);
    public DeviceCertificate Certificate { get; private set; } = null!;

    public static async Task<TestDeviceKeys> CreateAsync(IAccountSigner account, NetworkContext context, bool expired = false)
    {
        var keys = new TestDeviceKeys();
        var signing = new byte[32];
        var encryption = new byte[32];
        Ed25519.GeneratePublicKey(keys.SigningKey.AsSpan(), signing.AsSpan());
        X25519.GeneratePublicKey(keys.EncryptionKey.AsSpan(), encryption.AsSpan());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var certificate = new DeviceCertificate
        {
            Account = account.AccountId, AccountPublicKey = account.PublicKey,
            SigningPublicKey = [.. signing], EncryptionPublicKey = [.. encryption],
            NotBefore = now - 3600, ExpiresAt = expired ? now - 1 : now + 30 * 86400,
            DeviceSignature = [], AccountSignature = []
        };
        certificate = certificate with { DeviceSignature = [.. keys.Sign(certificate.GetDeviceSigningInput(context))] };
        keys.Certificate = certificate with { AccountSignature = [.. await account.SignAsync(certificate.GetAccountSigningInput(context), TestContext.Current.CancellationToken)] };
        return keys;
    }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        var signature = new byte[64];
        Ed25519.Sign(SigningKey.AsSpan(), data, signature.AsSpan());
        return signature;
    }
    public byte[] DeriveSharedSecret(ReadOnlySpan<byte> publicKey)
    {
        var secret = new byte[32];
        Assert.True(X25519.CalculateAgreement(EncryptionKey.AsSpan(), publicKey, secret.AsSpan()));
        return secret;
    }
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(SigningKey);
        CryptographicOperations.ZeroMemory(EncryptionKey);
    }
}
