using Meshline.Cli;
using System.Text.Json;

namespace Meshline.Cli.Tests;

public sealed class FoundationTests
{
    [Fact]
    public async Task Invalid_arguments_return_one_json_error_without_help_pollution()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApplication.RunAsync(["--json", "--not-a-real-option"], stdout, stderr);
        Assert.Equal(Exit.Usage, code);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Help_does_not_require_configuration()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["--help"], stdout, stderr));
        Assert.Contains("Meshline", stdout.ToString());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Profile_names_reject_paths(string name) => Assert.Throws<CliException>(() => Configuration.ValidateName(name));

    [Fact]
    public async Task Configuration_preserves_other_profiles_and_excludes_shared_data_directories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshline-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "config.json");
            await PrivateFiles.WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(new ClientConfiguration
            {
                DefaultProfile = "first", ProfilesDirectory = "profiles"
            }, Json.Options), false, TestContext.Current.CancellationToken);
            var credential = Path.Combine(directory, "credential.key");
            await Secrets.GenerateKeyFileAsync(credential, TestContext.Current.CancellationToken);
            ProfileConfiguration Profile(string name) => new()
            {
                Id = Guid.NewGuid().ToString("N"), Network = "neo:123:0x" + new string('a', 40), RpcUrl = "http://localhost:10332",
                DataDirectory = Path.Combine(directory, name), Protection = new("file", credential)
            };
            async Task CreateIdentity(string name, ProfileConfiguration profile)
            {
                var context = new ProfileContext(path, name, profile);
                profile.ProtectedKey = await Secrets.CreateAsync(context, TestContext.Current.CancellationToken);
                using var vault = await Secrets.OpenAsync(context, TestContext.Current.CancellationToken);
                await Identity.CreateAsync(context, vault, TestContext.Current.CancellationToken);
            }
            var first = Profile("first");
            await CreateIdentity("first", first);
            await Configuration.SaveProfileAsync(path, "first", first, true, TestContext.Current.CancellationToken);
            var second = Profile("second");
            await CreateIdentity("second", second);
            await Configuration.SaveProfileAsync(path, "second", second, true, TestContext.Current.CancellationToken);
            var collision = Profile("first");
            collision.ProtectedKey = first.ProtectedKey;
            await Assert.ThrowsAsync<CliException>(() => Configuration.SaveProfileAsync(path, "third", collision, true, TestContext.Current.CancellationToken));
            Assert.Equal("first", (await Configuration.LoadAsync(path, TestContext.Current.CancellationToken)).DefaultProfile);
            Assert.Equal(first.Id, (await Configuration.ResolveAsync(path, "first", TestContext.Current.CancellationToken)).Settings.Id);
            Assert.NotNull(await Configuration.ResolveAsync(path, "second", TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(directory, "profiles", "third", "profile.json")));
            using var held = FileLock.Acquire(Path.Combine(directory, "first", "session.lock"));
            Assert.Throws<CliException>(() => FileLock.Acquire(Path.Combine(directory, "first", "session.lock")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
