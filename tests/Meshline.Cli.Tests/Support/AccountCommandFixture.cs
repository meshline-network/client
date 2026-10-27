using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Transport;
using System.Text.Json;

namespace Meshline.Cli.Tests;

internal sealed class AccountCommandFixture : IAsyncDisposable
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    readonly TemporaryProfile profile;
    readonly HttpClient http;
    readonly RelayClientPool pool;
    readonly Vault vault;
    public TestAccountSigner Signer { get; }
    public AccountRelay Relay { get; }
    public MeshlineClient Client { get; }

    AccountCommandFixture(TemporaryProfile profile, Vault vault)
    {
        this.profile = profile;
        this.vault = vault;
        Signer = new(profile.Context.Network);
        Relay = new(profile.Context.Network);
        http = new(Relay) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new ClientOptions { Context = profile.Context.Network, AccountId = Signer.AccountId };
        pool = new(options, Relay, http);
        Client = new(options, new() { Path = profile.Context.DatabasePath }, pool, vault, Signer);
    }

    public static async Task<AccountCommandFixture> OpenAsync()
    {
        var profile = new TemporaryProfile();
        AccountCommandFixture? fixture = null;
        Vault? vault = null;
        try
        {
            await Secrets.GenerateKeyFileAsync(profile.Context.Settings.Protection.KeyFile!, Token);
            profile.Context.Settings.ProtectedKey = await Secrets.CreateAsync(profile.Context, Token);
            vault = await Secrets.OpenAsync(profile.Context, Token);
            fixture = new(profile, vault);
            await MeshlineDatabase.MigrateAsync(new() { Path = profile.Context.DatabasePath }, Token);
            await fixture.Client.InitializeAsync(Token);
            return fixture;
        }
        catch
        {
            if (fixture is not null) await fixture.DisposeAsync();
            else { vault?.Dispose(); profile.Dispose(); }
            throw;
        }
    }

    public async Task<(int ExitCode, JsonElement Result)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        var output = new Output(stdout, TextWriter.Null, true);
        var parsed = CliParser.Parse(args, async (invocation, token) =>
        {
            try { return await Commands.ExecuteSessionAsync(invocation, Client, output, token); }
            catch (Exception error)
            {
                var mapped = CliApplication.MapError(error);
                await output.ResultAsync(CommandResult.Failure(mapped));
                return mapped.ExitCode;
            }
        });
        Assert.Empty(parsed.Errors);
        var exitCode = await parsed.InvokeAsync(cancellationToken: Token);
        return (exitCode, JsonSerializer.Deserialize<JsonElement>(stdout.ToString()));
    }

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisposeAsync(); }
        finally
        {
            try { await pool.DisposeAsync(); }
            finally { http.Dispose(); Signer.Dispose(); vault.Dispose(); profile.Dispose(); }
        }
    }
}
