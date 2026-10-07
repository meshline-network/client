using Meshline.Cli;
using System.Text.Json;

namespace Meshline.Cli.Tests;

public sealed class ConfigurationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static async Task<JsonElement> Run(string config, int expected, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApplication.RunAsync([.. args, "--config", config, "--json"], stdout, stderr);
        Assert.True(code == expected, $"Expected {expected}, got {code}: {stdout}\n{stderr}");
        using var document = JsonDocument.Parse(stdout.ToString());
        return document.RootElement.Clone();
    }

    static Task<JsonElement> Create(TemporaryProfile fixture, string name, bool generate = false)
        => Run(fixture.Context.ConfigPath, Exit.Success, ["account", "create", "--profile", name,
            "--network", fixture.Context.Settings.Network, "--rpc", fixture.Context.Settings.RpcUrl,
            "--protection", "file", "--key-file", fixture.Context.Settings.Protection.KeyFile!,
            .. (generate ? new[] { "--generate-key" } : Array.Empty<string>())]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Application_settings_select_the_default_profile_and_keep_profile_secrets_separate(bool relative)
    {
        using var fixture = new TemporaryProfile();
        var config = fixture.Context.ConfigPath;
        var root = Path.Combine(fixture.DirectoryPath, "identities");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new ClientConfiguration
        {
            DefaultProfile = "alpha", ProfilesDirectory = relative ? "identities" : root
        }, Json.Options), Token);
        var appBytes = await File.ReadAllBytesAsync(config, Token);
        var created = await Create(fixture, "alpha", true);
        Assert.Equal(appBytes, await File.ReadAllBytesAsync(config, Token));
        using (var app = JsonDocument.Parse(appBytes))
        {
            Assert.Equal(1, app.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("alpha", app.RootElement.GetProperty("defaultProfile").GetString());
            Assert.Equal(relative ? "identities" : root, app.RootElement.GetProperty("profilesDirectory").GetString());
            Assert.False(app.RootElement.TryGetProperty("profiles", out _));
            Assert.False(app.RootElement.TryGetProperty("protectedKey", out _));
        }
        var alpha = await Configuration.ResolveAsync(config, null, Token);
        Assert.Equal(Path.Combine(root, "alpha"), alpha.DataDirectory);
        using (var profile = JsonDocument.Parse(await File.ReadAllTextAsync(alpha.ProfilePath, Token)))
        {
            Assert.True(profile.RootElement.TryGetProperty("protectedKey", out _));
            Assert.False(profile.RootElement.TryGetProperty("dataDirectory", out _));
        }
        Assert.Equal(created.GetProperty("data").GetProperty("accountId").GetString(), (await Identity.LoadAsync(alpha, Token)).AccountId);
        await Create(fixture, "beta");
        Assert.Equal(appBytes, await File.ReadAllBytesAsync(config, Token));
        Assert.Equal("beta", (await Run(config, 0, "account", "show", "--profile", "beta")).GetProperty("data").GetProperty("profile").GetString());
        var shown = (await Run(config, 0, "account", "show")).GetProperty("data");
        Assert.Equal("alpha", shown.GetProperty("profile").GetString());
        Assert.Equal(alpha.ProfilePath, shown.GetProperty("profilePath").GetString());
        Assert.Equal(alpha.DataDirectory, shown.GetProperty("dataDirectory").GetString());
        Assert.Equal(alpha.Settings.Network, shown.GetProperty("network").GetString());
        Assert.Equal(alpha.Settings.RpcUrl, shown.GetProperty("rpcUrl").GetString());
        Assert.Equal("file", shown.GetProperty("protection").GetProperty("mode").GetString());
        Assert.False(shown.TryGetProperty("protectedKey", out _));
        var listed = (await Run(config, 0, "account", "list")).GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "alpha", "beta" }, listed.Select(item => item.GetProperty("profile").GetString()));
        Assert.True(listed[0].GetProperty("isDefault").GetBoolean());
        Assert.False(listed[1].GetProperty("isDefault").GetBoolean());
        // App settings remain readable even when the selected profile is absent.
        var appSettings = await Configuration.LoadAsync(config, Token);
        appSettings.DefaultProfile = "missing";
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(appSettings, Json.Options), Token);
        Assert.Equal("missing", (await Run(config, 0, "config", "show")).GetProperty("data").GetProperty("defaultProfile").GetString());
        Assert.Equal("profile_missing", (await Run(config, Exit.Configuration, "account", "show")).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, (await Run(config, 0, "account", "list")).GetProperty("data").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Rewrap_changes_only_the_selected_manifest_and_keeps_its_master_key()
    {
        using var fixture = new TemporaryProfile();
        await Create(fixture, "alpha", true);
        await Create(fixture, "beta");
        var config = fixture.Context.ConfigPath;
        var alpha = await Configuration.ResolveAsync(config, "alpha", Token);
        var beta = await Configuration.ResolveAsync(config, "beta", Token);
        var appBytes = await File.ReadAllBytesAsync(config, Token);
        var betaBytes = await File.ReadAllBytesAsync(beta.ProfilePath, Token);
        byte[] ciphertext;
        using (var original = await Secrets.OpenAsync(alpha, Token)) ciphertext = await original.ProtectAsync(new byte[] { 1, 2, 3 }, "device-secret", Token);
        await Run(config, 0, "secrets", "rewrap", "--profile", "alpha", "--protection", "file",
            "--key-file", Path.Combine(fixture.DirectoryPath, "replacement.key"), "--generate-key");
        var updated = await Configuration.ResolveAsync(config, "alpha", Token);
        Assert.Equal(alpha.Settings.ProtectedKey!.KeyId, updated.Settings.ProtectedKey!.KeyId);
        using var reopened = await Secrets.OpenAsync(updated, Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, await reopened.UnprotectAsync(ciphertext, "device-secret", Token));
        Assert.Equal(appBytes, await File.ReadAllBytesAsync(config, Token));
        Assert.Equal(betaBytes, await File.ReadAllBytesAsync(beta.ProfilePath, Token));
    }

    [Fact]
    public async Task Bundled_configuration_is_readable_before_creating_a_profile()
    {
        using var fixture = new TemporaryProfile();
        var config = fixture.Context.ConfigPath;
        var settings = (await Run(config, 0, "config", "show")).GetProperty("data");
        Assert.Equal("default", settings.GetProperty("defaultProfile").GetString());
        Assert.Equal("profiles", settings.GetProperty("profilesDirectory").GetString());
        Assert.Equal("profile_missing", (await Run(config, Exit.Configuration, "account", "show")).GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "profiles")));
    }

    [Fact]
    public async Task Missing_explicit_configuration_is_not_created_or_replaced_by_the_bundled_default()
    {
        using var fixture = new TemporaryProfile();
        var missing = Path.Combine(fixture.DirectoryPath, "missing", "config.json");
        var result = await Run(missing, Exit.Configuration, "account", "create", "--profile", "agent",
            "--network", fixture.Context.Settings.Network, "--rpc", fixture.Context.Settings.RpcUrl,
            "--protection", "file", "--key-file", fixture.Context.Settings.Protection.KeyFile!, "--generate-key");
        Assert.Equal("config_missing", result.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        Assert.False(File.Exists(fixture.Context.Settings.Protection.KeyFile));
    }
}
