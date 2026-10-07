using Meshline.Interactions;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using System.Security.Cryptography;
using System.Text;

namespace Meshline.Cli;

internal static class SecureInput
{
    public static string Prompt(string label, bool confirm = false, CancellationToken token = default)
    {
        if (Console.IsInputRedirected)
            throw new CliException("interaction_required", "This operation requires a terminal for secret input. Configure file or native protection for unattended use.", Exit.Credentials);
        var value = Read(label, token);
        if (value.Length == 0) throw new CliException("empty_passphrase", "Passphrase must not be empty.", Exit.Credentials);
        if (confirm && value != Read("Confirm passphrase: ", token)) throw new CliException("passphrase_mismatch", "Passphrases do not match.", Exit.Credentials);
        return value;
    }

    static string Read(string label, CancellationToken token)
    {
        Console.Error.Write(label);
        var buffer = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable) { Thread.Sleep(25); continue; }
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return buffer.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; }
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }

    public static async Task<string> ReadPasswordAsync(string? path, string label, CancellationToken token)
    {
        if (path is null) return Prompt(label, token: token);
        PrivateFiles.VerifySecretFile(path);
        var text = await File.ReadAllTextAsync(path, token);
        return text.TrimEnd('\r', '\n');
    }
}

internal sealed class Vault : ISecretProtector, IDisposable
{
    readonly byte[] key;
    readonly string binding;
    bool disposed;
    internal Vault(byte[] key, string binding)
    {
        if (key.Length != 32) throw new CryptographicException("Master key must contain 32 bytes.");
        this.key = key.ToArray(); this.binding = binding;
    }
    public void Dispose() { if (disposed) return; CryptographicOperations.ZeroMemory(key); disposed = true; }
    internal byte[] CopyMasterKey() { ObjectDisposedException.ThrowIf(disposed, this); return key.ToArray(); }

    public Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Encrypt(key, plaintext.Span, Encoding.UTF8.GetBytes(binding + "/" + purpose)));
    }

    public Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> protectedData, string purpose, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Decrypt(key, protectedData.Span, Encoding.UTF8.GetBytes(binding + "/" + purpose)));
    }

    internal static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var result = new byte[29 + plaintext.Length];
        result[0] = 1;
        RandomNumberGenerator.Fill(result.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(1, 12), plaintext, result.AsSpan(29), result.AsSpan(13, 16), aad);
        return result;
    }

    internal static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> aad)
    {
        if (ciphertext.Length < 29 || ciphertext[0] != 1) throw new CryptographicException("Invalid secret envelope.");
        var result = new byte[ciphertext.Length - 29];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(ciphertext.Slice(1, 12), ciphertext[29..], ciphertext.Slice(13, 16), result, aad);
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
    }
}

internal static class Secrets
{
    public static async Task GenerateKeyFileAsync(string path, CancellationToken token)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try { await PrivateFiles.WriteAtomicAsync(path, bytes, false, token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static async Task<Vault> OpenAsync(ProfileContext context, CancellationToken token, string? passphrase = null)
    {
        var envelope = context.Settings.ProtectedKey ?? throw new CliException("key_missing", "The profile has no protected master key.", Exit.Credentials);
        var master = await UnwrapAsync(context, context.Settings.Protection, envelope, token, passphrase);
        try { return new Vault(master, context.KeyPurpose); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    public static async Task<KeyEnvelope> CreateAsync(ProfileContext context, CancellationToken token, string? passphrase = null)
    {
        var master = RandomNumberGenerator.GetBytes(32);
        try { return await WrapAsync(context, context.Settings.Protection, master, Guid.NewGuid().ToString("N"), token, passphrase); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    internal static async Task<KeyEnvelope> WrapAsync(ProfileContext context, ProtectionConfiguration protection, byte[] master, string keyId, CancellationToken token, string? passphrase = null)
    {
        if (master.Length != 32) throw new CryptographicException("Invalid master key size.");
        var aad = Encoding.UTF8.GetBytes(context.KeyPurpose + "/" + keyId);
        if (protection.Mode == "native" && OperatingSystem.IsWindows())
            return new(1, keyId, Convert.ToBase64String(ProtectedData.Protect(master, aad, DataProtectionScope.CurrentUser)));
        byte[]? salt = null;
        byte[] wrapping;
        if (protection.Mode == "passphrase")
        {
            salt = RandomNumberGenerator.GetBytes(16);
            wrapping = Derive(passphrase ?? SecureInput.Prompt("New profile passphrase: ", true, token), salt);
        }
        else wrapping = await ReadWrappingKeyAsync(protection, token);
        try
        {
            var bytes = Vault.Encrypt(wrapping, master, aad);
            return new(1, keyId, Convert.ToBase64String(bytes), Salt: salt is null ? null : Convert.ToBase64String(salt));
        }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    internal static async Task<byte[]> UnwrapAsync(ProfileContext context, ProtectionConfiguration protection, KeyEnvelope envelope, CancellationToken token, string? passphrase = null)
    {
        if (envelope.Version != 1 || !Guid.TryParseExact(envelope.KeyId, "N", out _) || envelope.Ciphertext.Length > 8192)
            throw new CliException("key_format", "Unsupported or invalid protected master key.", Exit.Credentials);
        var bytes = Convert.FromBase64String(envelope.Ciphertext);
        var aad = Encoding.UTF8.GetBytes(context.KeyPurpose + "/" + envelope.KeyId);
        byte[] master;
        if (protection.Mode == "native" && OperatingSystem.IsWindows()) master = ProtectedData.Unprotect(bytes, aad, DataProtectionScope.CurrentUser);
        else
        {
            byte[] wrapping;
            if (protection.Mode == "passphrase")
            {
                var salt = Convert.FromBase64String(envelope.Salt ?? throw new CryptographicException("Missing salt."));
                if (salt.Length != 16) throw new CryptographicException("Invalid salt.");
                wrapping = Derive(passphrase ?? SecureInput.Prompt("Profile passphrase: ", token: token), salt);
            }
            else wrapping = await ReadWrappingKeyAsync(protection, token);
            try { master = Vault.Decrypt(wrapping, bytes, aad); }
            finally { CryptographicOperations.ZeroMemory(wrapping); }
        }
        if (master.Length == 32) return master;
        CryptographicOperations.ZeroMemory(master);
        throw new CryptographicException("Invalid master key.");
    }

    static byte[] Derive(string password, byte[] salt)
    {
        if (password.Length == 0) throw new CliException("empty_passphrase", "Passphrase must not be empty.", Exit.Credentials);
        var bytes = Encoding.UTF8.GetBytes(password);
        var generator = new Argon2BytesGenerator();
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13).WithMemoryAsKB(65536).WithIterations(3).WithParallelism(1).WithSalt(salt).Build();
        try
        {
            generator.Init(parameters);
            var key = new byte[32];
            generator.GenerateBytes(bytes, key);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); parameters.Clear(); }
    }

    static async Task<byte[]> ReadWrappingKeyAsync(ProtectionConfiguration protection, CancellationToken token)
    {
        string path;
        if (protection.Mode == "file") path = protection.KeyFile ?? throw new CliException("key_missing", "No key file is configured.", Exit.Credentials);
        else if (protection.Mode == "native" && OperatingSystem.IsLinux())
        {
            var directory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
            var name = protection.CredentialName;
            if (string.IsNullOrEmpty(directory) || string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or "..")
                throw new CliException("native_unavailable", "Linux native protection requires CREDENTIALS_DIRECTORY and a configured systemd credential name. Use a service credential or explicitly configure another mode.", Exit.Credentials);
            path = Path.Combine(directory, name);
        }
        else throw new CliException("native_unavailable", "Native protection is supported on Windows and Linux/systemd.", Exit.Credentials);
        PrivateFiles.VerifySecretFile(path, allowCurrentUserAcl: protection.Mode == "native");
        if (new FileInfo(path).Length != 32) throw new CliException("key_format", "Credential must contain exactly 32 random binary bytes.", Exit.Credentials);
        return await File.ReadAllBytesAsync(path, token);
    }
}
