using Meshline.Cli;
using System.Text;
using System.Text.Json;

namespace Meshline.Cli.Tests;

public sealed class AccountCreationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static string[] Arguments(TemporaryProfile fixture, params string[] operation)
        => [.. operation, "--config", fixture.Context.ConfigPath, "--profile", "bound",
            "--network", fixture.Context.Settings.Network, "--rpc", fixture.Context.Settings.RpcUrl,
            "--protection", "file", "--key-file", fixture.Context.Settings.Protection.KeyFile!, "--json"];

    static async Task<(int Code, JsonElement Result)> Run(params string[] arguments)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApplication.RunAsync(arguments, stdout, stderr);
        using var result = JsonDocument.Parse(stdout.ToString());
        return (code, result.RootElement.Clone());
    }

    [Fact]
    public async Task Import_validates_the_wallet_before_creating_a_complete_profile_and_never_replaces_it()
    {
        using var fixture = new TemporaryProfile();
        const string password = "import-account-password";
        var wallet = new Neo.Wallets.NEP6.NEP6Wallet(Path.Combine(fixture.DirectoryPath, "unused.json"), password, Neo.ProtocolSettings.Default);
        wallet.CreateAccount(); wallet.CreateAccount();
        var walletPath = Path.Combine(fixture.DirectoryPath, "source.json");
        var source = wallet.ToJson().ToString();
        await File.WriteAllTextAsync(walletPath, source, Token);
        var passwordPath = Path.Combine(fixture.DirectoryPath, "password.txt");
        await PrivateFiles.WriteAtomicAsync(passwordPath, Encoding.UTF8.GetBytes("wrong-password"), false, Token);
        var arguments = Arguments(fixture, "account", "import", "--wallet", walletPath,
            "--password-file", passwordPath, "--account-index", "1", "--generate-key");
        var failed = await Run(arguments);
        Assert.NotEqual(0, failed.Code);
        var target = Path.Combine(fixture.DirectoryPath, "profiles", "bound");
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(fixture.Context.Settings.Protection.KeyFile));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(target)!));

        await PrivateFiles.WriteAtomicAsync(passwordPath, Encoding.UTF8.GetBytes(password), true, Token);
        var imported = await Run(arguments);
        Assert.Equal(0, imported.Code);
        var context = await Configuration.ResolveAsync(fixture.Context.ConfigPath, "bound", Token);
        var identity = await Identity.LoadAsync(context, Token);
        using var expected = Meshline.Interactions.Nep6AccountSigner.Parse(source, password, context.Network, 1);
        Assert.Equal(expected.AccountId, identity.AccountId);
        using (var json = JsonDocument.Parse(identity.WalletJson)) Assert.Single(json.RootElement.GetProperty("accounts").EnumerateArray());
        using (var vault = await Secrets.OpenAsync(context, Token))
            Assert.Equal(64, (await new VaultAccountSigner(context, identity, vault).SignAsync(new byte[] { 1, 2, 3 }, Token)).Length);
        Assert.Equal(source, await File.ReadAllTextAsync(walletPath, Token));
        var before = await File.ReadAllBytesAsync(context.IdentityPath, Token);
        Assert.Equal(Exit.Configuration, (await Run(arguments)).Code);
        Assert.Equal(Exit.Configuration, (await Run(Arguments(fixture, "account", "create"))).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(context.IdentityPath, Token));
    }

    [Fact]
    public async Task Existing_directories_are_preserved_and_missing_identities_are_not_valid_profiles()
    {
        using var fixture = new TemporaryProfile();
        var target = Path.Combine(fixture.DirectoryPath, "profiles", "bound");
        PrivateFiles.EnsureDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "existing data", Token);
        var refused = await Run(Arguments(fixture, "account", "create", "--generate-key"));
        Assert.Equal(Exit.Configuration, refused.Code);
        Assert.Equal("existing data", await File.ReadAllTextAsync(sentinel, Token));
        Assert.False(File.Exists(fixture.Context.Settings.Protection.KeyFile));

        // A manually damaged profile must not be accepted by metadata-only commands either.
        fixture.Context.Settings.ProtectedKey = new(1, Guid.NewGuid().ToString("N"), "not-opened");
        await File.WriteAllTextAsync(Path.Combine(target, "profile.json"), JsonSerializer.Serialize(fixture.Context.Settings, Json.Options), Token);
        var result = await Run("account", "show", "--config", fixture.Context.ConfigPath, "--profile", "bound", "--json");
        Assert.Equal(Exit.Configuration, result.Code);
        Assert.Equal("profile_incomplete", result.Result.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_creation_publishes_only_one_complete_account()
    {
        using var fixture = new TemporaryProfile();
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, Token);
        var arguments = Arguments(fixture, "account", "create");
        var attempts = await Task.WhenAll(Task.Run(() => Run(arguments), Token), Task.Run(() => Run(arguments), Token));
        Assert.Single(attempts, attempt => attempt.Code == 0);
        Assert.Single(attempts, attempt => attempt.Code is Exit.Busy or Exit.Configuration);
        var context = await Configuration.ResolveAsync(fixture.Context.ConfigPath, "bound", Token);
        var identity = await Identity.LoadAsync(context, Token);
        Assert.Equal(attempts.Single(attempt => attempt.Code == 0).Result.GetProperty("data").GetProperty("accountId").GetString(), identity.AccountId);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.DirectoryPath, "profiles"), ".pending-*"));
    }

    [Fact]
    public async Task Cancellation_before_publication_leaves_no_empty_profile()
    {
        using var fixture = new TemporaryProfile();
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, Token);
        var root = Path.Combine(fixture.DirectoryPath, "profiles");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var invocation = new Invocation("account create", new()
        {
            ["network"] = fixture.Context.Settings.Network, ["rpc"] = fixture.Context.Settings.RpcUrl,
            ["protection"] = "file", ["key-file"] = fixture.Context.Settings.Protection.KeyFile!
        }, fixture.Context.ConfigPath, "bound", true, 0);
        var reachedIdentity = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Commands.CreateAccountAsync(invocation,
            new Output(TextWriter.Null, TextWriter.Null, true), canceled.Token, async (context, vault, token) =>
        {
            var identity = await Identity.CreateAsync(context, vault, token);
            Assert.True(File.Exists(context.IdentityPath));
            Assert.Single(Directory.EnumerateDirectories(root, ".pending-*"));
            Assert.False(Directory.Exists(Path.Combine(root, "bound")));
            reachedIdentity = true;
            canceled.Cancel();
            return identity;
        }));
        Assert.True(reachedIdentity);
        Assert.False(Directory.Exists(Path.Combine(root, "bound")));
        Assert.Empty(Directory.EnumerateDirectories(root, ".pending-*"));
    }
}
