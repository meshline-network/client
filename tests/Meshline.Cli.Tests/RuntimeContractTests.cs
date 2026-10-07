using Meshline.Cli;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Cli.Tests;

public sealed class RuntimeContractTests
{
    [Fact]
    public async Task Ipc_preserves_unicode_and_rejects_oversized_frame_before_allocating_body()
    {
        var token = TestContext.Current.CancellationToken;
        await using var stream = new MemoryStream();
        await Ipc.WriteAsync(stream, new IpcResponse("stdout", "消息\nsecond line"), token);
        stream.Position = 0;
        Assert.Equal("消息\nsecond line", (await Ipc.ReadAsync<IpcResponse>(stream, token)).Line);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, Ipc.MaximumBytes + 1);
        await using var oversized = new MemoryStream(header);
        var error = await Assert.ThrowsAsync<CliException>(() => Ipc.ReadAsync<IpcRequest>(oversized, token));
        Assert.Equal("ipc_size", error.Code);
    }

    [Fact]
    public void Protocol_content_round_trips_in_a_client_draft_without_changing_field_names()
    {
        var json = """{"body":{"content_type":"text/plain","text":"测试"},"attachments":[]}""";
        var draft = Json.Read<DirectMessageDraft>(json);
        Assert.Equal("测试", draft.Body!.Text);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(draft, Json.Options));
        Assert.Equal("text/plain", document.RootElement.GetProperty("body").GetProperty("content_type").GetString());
        Assert.False(document.RootElement.GetProperty("body").TryGetProperty("contentType", out _));
    }

    [Fact]
    public async Task Local_conversation_queries_work_without_a_network_or_a_second_message_store()
    {
        using var fixture = new TemporaryProfile();
        var token = TestContext.Current.CancellationToken;
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, token);
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, token);
        using (var vault = await Secrets.OpenAsync(fixture.Context, token)) await Identity.CreateAsync(fixture.Context, vault, token);
        await Configuration.SaveProfileAsync(fixture.Context.ConfigPath, fixture.Context.Name, fixture.Context.Settings, true, token);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["conversations", "list", "--json", "--config", fixture.Context.ConfigPath, "--profile", fixture.Context.Name], stdout, stderr));
        using var result = JsonDocument.Parse(stdout.ToString());
        Assert.Empty(result.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Single(Directory.EnumerateFiles(fixture.Context.DataDirectory, "*.db"));
        Assert.False(Commands.NeedsBackground("conversations mark-read"));
    }

    [Fact]
    public async Task Failed_profile_creation_can_be_retried_after_providing_the_credential()
    {
        using var fixture = new TemporaryProfile();
        var token = TestContext.Current.CancellationToken;
        var args = new[] { "account", "create", "--profile", fixture.Context.Name, "--config", fixture.Context.ConfigPath,
            "--network", fixture.Context.Settings.Network, "--rpc", fixture.Context.Settings.RpcUrl, "--protection", "file", "--key-file", fixture.Context.Settings.Protection.KeyFile!, "--json" };
        Assert.Equal(Exit.Credentials, await CliApplication.RunAsync(args, new StringWriter(), new StringWriter()));
        Assert.False(File.Exists(fixture.Context.ProfilePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Context.ProfilePath)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.DirectoryPath, "profiles")));
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, token);
        Assert.Equal(Exit.Success, await CliApplication.RunAsync(args, new StringWriter(), new StringWriter()));
        var created = await Configuration.ResolveAsync(fixture.Context.ConfigPath, fixture.Context.Name, token);
        Assert.True(File.Exists(created.IdentityPath));
    }

    [Fact]
    public async Task Native_protection_round_trips_on_the_current_platform()
    {
        using var fixture = new TemporaryProfile("native");
        var token = TestContext.Current.CancellationToken;
        var original = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        if (!OperatingSystem.IsWindows())
        {
            fixture.Context.Settings.Protection = new("native", CredentialName: "test-master");
            await Secrets.GenerateKeyFileAsync(Path.Combine(fixture.DirectoryPath, "test-master"), token);
            Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", fixture.DirectoryPath);
        }
        try
        {
            fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, token);
            using var vault = await Secrets.OpenAsync(fixture.Context, token);
            var encrypted = await vault.ProtectAsync(Encoding.UTF8.GetBytes("native test"), "secret", token);
            Assert.Equal("native test", Encoding.UTF8.GetString(await vault.UnprotectAsync(encrypted, "secret", token)));
        }
        finally { if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", original); }
    }

    [Fact]
    public async Task Separate_process_can_read_metadata_without_credentials_while_the_session_remains_exclusive()
    {
        using var fixture = new TemporaryProfile();
        var token = TestContext.Current.CancellationToken;
        await Secrets.GenerateKeyFileAsync(fixture.Context.Settings.Protection.KeyFile!, token);
        fixture.Context.Settings.ProtectedKey = await Secrets.CreateAsync(fixture.Context, token);
        using (var vault = await Secrets.OpenAsync(fixture.Context, token)) await Identity.CreateAsync(fixture.Context, vault, token);
        await Configuration.SaveProfileAsync(fixture.Context.ConfigPath, fixture.Context.Name, fixture.Context.Settings, true, token);
        using (FileLock.Acquire(fixture.Context.LockPath))
        {
            File.Delete(fixture.Context.Settings.Protection.KeyFile!);
            foreach (var (group, command, expected) in new[] { ("account", "show", Exit.Success), ("account", "list", Exit.Success), ("conversations", "list", Exit.Busy) })
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var arg in new[] { typeof(CliApplication).Assembly.Location, group, command, "--config", fixture.Context.ConfigPath, "--profile", fixture.Context.Name, "--json" }) start.ArgumentList.Add(arg);
                using var child = Process.Start(start)!;
                var output = await child.StandardOutput.ReadToEndAsync(token);
                var errors = await child.StandardError.ReadToEndAsync(token);
                await child.WaitForExitAsync(token);
                Assert.True(child.ExitCode == expected, output + errors);
                if (expected == Exit.Busy) Assert.Contains("profile_busy", output);
                else
                {
                    using var json = JsonDocument.Parse(output);
                    Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
                    Assert.DoesNotContain("protectedPassword", output);
                    Assert.DoesNotContain("walletJson", output);
                    Assert.DoesNotContain("ciphertext", output);
                }
                Assert.Empty(errors);
            }
        }
        using var reopened = FileLock.Acquire(fixture.Context.LockPath);
    }
}
