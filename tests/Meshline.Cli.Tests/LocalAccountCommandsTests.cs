using Meshline.Cli;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Meshline.Cli.Tests;

public sealed class LocalAccountCommandsTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static async Task<JsonElement> Run(string config, int expected, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApplication.RunAsync([.. args, "--config", config, "--json"], stdout, stderr);
        Assert.True(code == expected, $"Expected {expected}, got {code}: {stdout}\n{stderr}");
        Assert.Empty(stderr.ToString());
        using var result = JsonDocument.Parse(stdout.ToString());
        return result.RootElement.Clone();
    }

    static async Task Create(TemporaryProfile fixture)
    {
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, Token);
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, Token);
        using (var vault = await Secrets.OpenAsync(fixture.Context, Token)) await Identity.CreateAsync(fixture.Context, vault, Token);
        await Configuration.SaveProfileAsync(fixture.Context.ConfigPath, fixture.Context.Name, fixture.Context.Settings, true, Token);
    }

    [Fact]
    public async Task First_use_diagnostics_and_account_list_do_not_create_a_profile()
    {
        using var fixture = new TemporaryProfile();
        var config = fixture.Context.ConfigPath;
        var before = await File.ReadAllBytesAsync(config, Token);
        var report = (await Run(config, 0, "doctor")).GetProperty("data");
        Assert.True(report.GetProperty("configurationValid").GetBoolean());
        Assert.Equal("missing", report.GetProperty("profileStatus").GetString());
        Assert.False(report.GetProperty("networkChecked").GetBoolean());
        var list = (await Run(config, 0, "account", "list")).GetProperty("data");
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Empty(list.GetProperty("issues").EnumerateArray());
        var network = (await Run(config, Exit.Configuration, "doctor", "--network")).GetProperty("error").GetProperty("details");
        Assert.Equal("skipped", network.GetProperty("networkStatus").GetString());
        Assert.Equal("missing", network.GetProperty("profileStatus").GetString());
        Assert.Equal("profile_missing", (await Run(config, Exit.Configuration, "doctor", "--profile", "missing")).GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "profiles")));
        Assert.Equal(before, await File.ReadAllBytesAsync(config, Token));
    }

    [Fact]
    public async Task Doctor_preserves_runtime_diagnostics_when_application_configuration_is_unreadable()
    {
        using var fixture = new TemporaryProfile();
        var missing = Path.Combine(fixture.DirectoryPath, "missing.json");
        var report = (await Run(missing, Exit.Configuration, "doctor")).GetProperty("error").GetProperty("details");
        Assert.False(report.GetProperty("configurationValid").GetBoolean());
        Assert.Equal(Environment.Version.ToString(), report.GetProperty("runtime").GetString());
        Assert.Equal(missing, report.GetProperty("configPath").GetString());
        Assert.Equal("not-checked", report.GetProperty("profileStatus").GetString());
        await File.WriteAllTextAsync(missing, "{bad json", Token);
        report = (await Run(missing, Exit.Configuration, "doctor")).GetProperty("error").GetProperty("details");
        Assert.False(report.GetProperty("configurationValid").GetBoolean());
        Assert.Single(report.GetProperty("issues").EnumerateArray());
    }

    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("invalid-manifest")]
    [InlineData("missing-identity")]
    [InlineData("invalid-identity")]
    public async Task Damaged_profiles_are_diagnosed_and_never_listed_as_accounts(string damage)
    {
        using var fixture = new TemporaryProfile();
        var directory = Path.GetDirectoryName(fixture.Context.ProfilePath)!;
        PrivateFiles.EnsureDirectory(directory);
        if (damage != "missing-manifest")
        {
            fixture.Context.Settings.ProtectedKey = new(1, "test-key", "not-opened");
            await File.WriteAllTextAsync(fixture.Context.ProfilePath,
                damage == "invalid-manifest" ? "{bad json" : JsonSerializer.Serialize(fixture.Context.Settings, Json.Options), Token);
        }
        if (damage == "invalid-identity") await File.WriteAllTextAsync(fixture.Context.IdentityPath, "{bad json", Token);
        var report = (await Run(fixture.Context.ConfigPath, Exit.Configuration, "doctor", "--profile", fixture.Context.Name)).GetProperty("error").GetProperty("details");
        Assert.True(report.GetProperty("configurationValid").GetBoolean());
        Assert.Equal("invalid", report.GetProperty("profileStatus").GetString());
        Assert.Single(report.GetProperty("issues").EnumerateArray());
        var list = (await Run(fixture.Context.ConfigPath, Exit.Configuration, "account", "list")).GetProperty("error").GetProperty("details");
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Single(list.GetProperty("issues").EnumerateArray());
        Assert.Equal(fixture.Context.Name, list.GetProperty("issues")[0].GetProperty("profile").GetString());
    }

    [Fact]
    public async Task Account_list_keeps_valid_accounts_in_a_partial_report_and_ignores_pending_creation()
    {
        using var fixture = new TemporaryProfile();
        await Create(fixture);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(fixture.Context.ProfilePath))!;
        PrivateFiles.EnsureDirectory(Path.Combine(root, "broken"));
        PrivateFiles.EnsureDirectory(Path.Combine(root, ".pending-test"));
        File.Delete(fixture.Context.Settings.Protection.KeyFile!);
        using var held = FileLock.Acquire(fixture.Context.LockPath);
        var report = (await Run(fixture.Context.ConfigPath, Exit.Configuration, "account", "list")).GetProperty("error").GetProperty("details");
        Assert.Single(report.GetProperty("items").EnumerateArray());
        Assert.Equal(fixture.Context.Name, report.GetProperty("items")[0].GetProperty("profile").GetString());
        Assert.Single(report.GetProperty("issues").EnumerateArray());
        Assert.Equal("broken", report.GetProperty("issues")[0].GetProperty("profile").GetString());
        var doctor = (await Run(fixture.Context.ConfigPath, 0, "doctor", "--profile", fixture.Context.Name)).GetProperty("data");
        Assert.Equal("ready", doctor.GetProperty("profileStatus").GetString());
        Assert.True(doctor.GetProperty("identityExists").GetBoolean());
        Assert.False(doctor.GetProperty("databaseExists").GetBoolean());
    }

    [Fact]
    public async Task Network_failure_retains_successful_local_checks()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var fixture = new TemporaryProfile();
        fixture.Context.Settings.RpcUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        await Create(fixture);
        var run = Run(fixture.Context.ConfigPath, Exit.Network, "doctor", "--profile", fixture.Context.Name, "--network");
        using (var client = await listener.AcceptTcpClientAsync(deadline.Token))
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            Assert.True(await stream.ReadAsync(buffer, deadline.Token) > 0);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        }
        var report = (await run.WaitAsync(deadline.Token)).GetProperty("error").GetProperty("details");
        Assert.Equal("ready", report.GetProperty("profileStatus").GetString());
        Assert.True(report.GetProperty("configurationValid").GetBoolean());
        Assert.False(report.GetProperty("networkChecked").GetBoolean());
        Assert.Equal("failed", report.GetProperty("networkStatus").GetString());
    }

    [Fact]
    public async Task Command_help_and_parser_expose_the_account_bound_workflow()
    {
        using var fixture = new TemporaryProfile();
        foreach (var command in new[] { "create", "import" })
        {
            using var help = new StringWriter();
            Assert.Equal(0, await CliApplication.RunAsync(["account", command, "--help"], help, TextWriter.Null));
            Assert.Contains("file, native, or passphrase", help.ToString());
            Assert.Contains("neo:<network-magic>:<registry-hash>", help.ToString());
            Assert.Contains("must not already exist", help.ToString());
            var error = (await Run(Path.Combine(fixture.DirectoryPath, "absent.json"), Exit.Usage, "account", command)).GetProperty("error");
            Assert.Equal("usage", error.GetProperty("code").GetString());
            foreach (var required in new[] { "--network", "--rpc", "--protection" }) Assert.Contains(required, error.GetProperty("message").GetString()!);
            if (command == "import") Assert.Contains("--wallet", error.GetProperty("message").GetString()!);
        }
        foreach (var command in new[] { "show", "update" })
        {
            Assert.Equal(0, await CliApplication.RunAsync(["account", "profile", command, "--help"], TextWriter.Null, TextWriter.Null));
            await Run(fixture.Context.ConfigPath, Exit.Usage, "profile", command);
        }
        await Run(fixture.Context.ConfigPath, Exit.Usage, "config", "profile");
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "profiles")));
    }
}
