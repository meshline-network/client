namespace Meshline.Cli;

internal static partial class Commands
{
    public static IReadOnlyList<CommandSpec> Catalog { get; } =
    [
        new("secrets generate-key", "Create a private file containing 32 random bytes; never overwrites.", ["out"], [], []),
        new("secrets rewrap", "Change protection without replacing the master key or device data.", ["protection", "key-file", "credential-name"], ["generate-key"], []),
        AccountCreationCommand(import: false),
        AccountCreationCommand(import: true),
        new("account export", "Export an encrypted NEP-6 wallet; never overwrites.", ["out", "password-file"], [], []),
        new("account show", "Show the selected local account identity and settings without unlocking.", [], [], []),
        new("account list", "List local accounts, their profile names and networks without unlocking.", [], [], []),
        new("config show", "Show application settings.", [], [], []),
        new("doctor", "Inspect runtime, application configuration and account files, including missing or damaged profiles; --network also checks RPC.", [], ["network"], []),
        .. SessionCatalog,
        .. GroupCatalog,
        .. ChannelCatalog,
        new("daemon run", "Run a foreground SDK session until canceled or stopped.", [], ["unlock-stdin"], []),
        new("daemon start", "Start a background session, unlocking once through a private child pipe.", [], [], []),
        new("daemon status", "Inspect the local daemon without opening the SDK database.", [], [], []),
        new("daemon stop", "Request graceful shutdown of the local daemon.", [], [], [])
    ];

    public static async Task<int> ExecuteAsync(Invocation invocation, Output output, CancellationToken token)
    {
        if (invocation.Command is "account create" or "account import")
        {
            await CreateAccountAsync(invocation, output, token);
            return Exit.Success;
        }
        if (invocation.Command == "secrets generate-key")
        {
            var path = Path.GetFullPath(invocation.Require("out"));
            await Secrets.GenerateKeyFileAsync(path, token);
            await output.ResultAsync(CommandResult.Success(new { path }));
            return Exit.Success;
        }
        if (invocation.Command == "config show")
        {
            await output.ResultAsync(CommandResult.Success(await Configuration.LoadAsync(invocation.ConfigPath, token)));
            return Exit.Success;
        }
        if (invocation.Command == "account list") return await ListAccountsAsync(invocation, output, token);
        if (invocation.Command == "doctor") return await DoctorAsync(invocation, output, token);
        var context = await Configuration.ResolveAsync(invocation.ConfigPath, invocation.Profile, token);
        invocation = invocation with { Profile = context.Name };
        if (invocation.Command.StartsWith("daemon ", StringComparison.Ordinal)) return await Daemon.ExecuteAsync(invocation, context, output, token);
        if (invocation.Command == "account show")
        {
            var identity = await Identity.LoadAsync(context, token);
            await output.ResultAsync(CommandResult.Success(new
            {
                identity.AccountId, identity.Address, identity.PublicKey, profile = context.Name,
                profilePath = context.ProfilePath, network = context.Settings.Network, rpcUrl = context.Settings.RpcUrl,
                dataDirectory = context.DataDirectory, protection = context.Settings.Protection
            }));
            return Exit.Success;
        }
        if (invocation.Command is "account export" or "secrets rewrap")
        {
            using var sessionLock = FileLock.Acquire(context.LockPath);
            using var vault = await Secrets.OpenAsync(context, token);
            object accountResult;
            switch (invocation.Command)
            {
                case "account export":
                    var exportPassword = await SecureInput.ReadPasswordAsync(invocation.Get("password-file"), "Export wallet password: ", token);
                    if (exportPassword.Length == 0) throw new CliException("empty_passphrase", "Export wallet password must not be empty.", Exit.Credentials);
                    await Identity.ExportAsync(context, vault, invocation.Require("out"), exportPassword, token);
                    accountResult = new { path = Path.GetFullPath(invocation.Require("out")) };
                    break;
                case "secrets rewrap":
                    accountResult = await RewrapAsync(invocation, context, vault, token);
                    break;
                default: throw new CliException("unknown_command", "Unknown command.", Exit.Usage);
            }
            await output.ResultAsync(CommandResult.Success(accountResult));
            return Exit.Success;
        }
        return await ExecuteWithSessionAsync(invocation, context, output, token);
    }

    static ProtectionConfiguration Protection(Invocation invocation) => new(invocation.Require("protection"),
        invocation.Get("key-file") is { } file ? Path.GetFullPath(file) : null, invocation.Get("credential-name"));

    static async Task<object> RewrapAsync(Invocation invocation, ProfileContext context, Vault vault, CancellationToken token)
    {
        var protection = Protection(invocation);
        if (invocation.Flag("generate-key"))
        {
            if (protection.Mode != "file" || protection.KeyFile is null) throw new CliException("invalid_argument", "--generate-key requires file protection and --key-file.", Exit.Usage);
            await Secrets.GenerateKeyFileAsync(protection.KeyFile, token);
        }
        string? passphrase = protection.Mode == "passphrase" ? SecureInput.Prompt("New profile passphrase: ", true, token) : null;
        var master = vault.CopyMasterKey();
        try
        {
            var wrapped = await Secrets.WrapAsync(context, protection, master, context.Settings.ProtectedKey!.KeyId, token, passphrase);
            var verified = await Secrets.UnwrapAsync(context, protection, wrapped, token, passphrase);
            try
            {
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(master, verified)) throw new System.Security.Cryptography.CryptographicException("Master key verification failed.");
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(verified); }
            context.Settings.Protection = protection;
            context.Settings.ProtectedKey = wrapped;
            await Configuration.SaveProfileAsync(context, false, token);
            return new { profile = context.Name, protection = protection.Mode, keyId = wrapped.KeyId };
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(master); }
    }
}
