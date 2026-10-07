using System.Globalization;
using System.Text.Json;

namespace Meshline.Cli;

internal static partial class Commands
{
    static CommandSpec AccountCreationCommand(bool import) => new(
        import ? "account import" : "account create",
        import ? "Import one NEP-6 account and create its local profile together; refuses an existing target directory."
               : "Create an account and its local profile together; refuses an existing target directory.",
        [.. (import ? new[] { "wallet", "password-file", "account-index" } : Array.Empty<string>()), "network", "rpc", "protection", "key-file", "credential-name"],
        ["generate-key"], [])
    {
        RequiredOptions = ["network", "rpc", "protection", .. (import ? new[] { "wallet" } : Array.Empty<string>())],
        OptionDescriptions = new Dictionary<string, string>
        {
            ["network"] = "Required. Network identity: neo:<network-magic>:<registry-hash>.",
            ["rpc"] = "Required. Neo HTTP(S) RPC endpoint for this account.",
            ["protection"] = "Required. Master-key protection: file, native, or passphrase. Native uses Windows DPAPI or Linux systemd credentials.",
            ["key-file"] = "Required with file protection. Credential file outside the new profile directory; relative paths use the current working directory.",
            ["credential-name"] = "Required for Linux native protection: credential name inside CREDENTIALS_DIRECTORY supplied by systemd.",
            ["generate-key"] = "With file protection and --key-file, generate a new 32-byte credential file; refuses an existing file.",
            ["wallet"] = "Required for import. Source NEP-6 wallet path; the source is not changed.",
            ["password-file"] = "Private file containing the source wallet password; otherwise prompts without echo.",
            ["account-index"] = "Zero-based source wallet account index; defaults to 0. Only this account is imported."
        }
    };

    internal static async Task CreateAccountAsync(Invocation invocation, Output output, CancellationToken token,
        Func<ProfileContext, Vault, CancellationToken, Task<IdentityDocument>>? createIdentity = null)
    {
        var app = await Configuration.LoadAsync(invocation.ConfigPath, token);
        var name = invocation.Profile ?? app.DefaultProfile;
        Configuration.ValidateName(name);
        var root = Configuration.ResolveProfilesDirectory(invocation.ConfigPath, app);
        var destination = Path.Combine(root, name);
        EnsureNewProfile(destination);
        var settings = new ProfileConfiguration
        {
            Id = Guid.NewGuid().ToString("N"), Network = invocation.Require("network"),
            RpcUrl = invocation.Require("rpc"), Protection = Protection(invocation)
        };
        Configuration.Validate(settings);
        if (invocation.Flag("generate-key") && settings.Protection.Mode != "file")
            throw new CliException("invalid_argument", "--generate-key requires file protection.", Exit.Usage);
        if (settings.Protection.KeyFile is { } keyFile)
        {
            var relative = Path.GetRelativePath(destination, keyFile);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new CliException("credential_path_conflict", "Keep the credential file outside the new profile directory.", Exit.Configuration);
        }

        // The lock is outside the profile so no partially initialized profile is exposed.
        using var creationLock = FileLock.Acquire(Path.Combine(root, ".create-" + name + ".lock"));
        EnsureNewProfile(destination);
        ImportedWallet? imported = null;
        if (invocation.Command == "account import")
        {
            var walletPath = invocation.Require("wallet");
            var password = await SecureInput.ReadPasswordAsync(invocation.Get("password-file"), "Wallet password: ", token);
            var index = int.Parse(invocation.Get("account-index") ?? "0", CultureInfo.InvariantCulture);
            imported = await Identity.PrepareImportAsync(Meshline.Models.NetworkContext.Parse(settings.Network), walletPath, password, index, token);
        }

        // A sibling directory allows one atomic rename to publish identity and configuration together.
        var staging = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N"));
        PrivateFiles.EnsureDirectory(staging);
        var context = new ProfileContext(invocation.ConfigPath, name, settings, Path.Combine(staging, "profile.json"));
        IdentityDocument identity;
        try
        {
            if (invocation.Flag("generate-key")) await Secrets.GenerateKeyFileAsync(settings.Protection.KeyFile!, token);
            var passphrase = settings.Protection.Mode == "passphrase" ? SecureInput.Prompt("New profile passphrase: ", true, token) : null;
            settings.ProtectedKey = await Secrets.CreateAsync(context, token, passphrase);
            using (var vault = await Secrets.OpenAsync(context, token, passphrase))
                identity = imported is null
                    ? await (createIdentity ?? Identity.CreateAsync)(context, vault, token)
                    : await Identity.ImportPreparedAsync(context, vault, imported, token);
            await PrivateFiles.WriteAtomicAsync(context.ProfilePath, JsonSerializer.SerializeToUtf8Bytes(settings, Json.Pretty), false, token);
            var current = await Configuration.LoadAsync(invocation.ConfigPath, token);
            if (!Configuration.PathComparer.Equals(root, Configuration.ResolveProfilesDirectory(invocation.ConfigPath, current)))
                throw new CliException("profile_changed", "The application profiles directory changed. Retry with the current configuration.", Exit.Configuration);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            // Only our generated sibling directory is eligible for cleanup, never the destination.
            if (!Configuration.PathComparer.Equals(Path.GetDirectoryName(staging), root))
                throw new InvalidOperationException("Temporary profile escaped its root.");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        await output.ResultAsync(CommandResult.Success(new
        {
            identity.AccountId, identity.Address, identity.PublicKey, profile = name,
            profilePath = Path.Combine(destination, "profile.json"), dataDirectory = destination
        }));
    }

    static void EnsureNewProfile(string destination)
    {
        if (Path.Exists(destination))
            throw new CliException("profile_exists", "The target profile directory already exists. Choose a new profile name; existing accounts are never replaced.", Exit.Configuration);
    }
}
