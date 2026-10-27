using Meshline.Cli;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Meshline.Cli.Tests;

internal sealed class SessionFixture : IAsyncDisposable
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    public TemporaryProfile Profile { get; } = new();
    public RuntimeSession Session { get; private set; } = null!;

    public static async Task<SessionFixture> OpenAsync(Func<ProfileContext, Task>? prepareDatabase = null)
    {
        var fixture = new SessionFixture();
        try
        {
            var context = fixture.Profile.Context;
            await Secrets.GenerateKeyFileAsync(context.Settings.Protection.KeyFile!, Token);
            context.Settings.ProtectedKey = await Secrets.CreateAsync(context, Token);
            using (var vault = await Secrets.OpenAsync(context, Token)) await Identity.CreateAsync(context, vault, Token);
            await Configuration.SaveProfileAsync(context, true, Token);
            if (prepareDatabase is not null) await prepareDatabase(context);
            fixture.Session = await RuntimeSession.OpenAsync(context, new(TextWriter.Null, TextWriter.Null, true), Token);
            return fixture;
        }
        catch { fixture.Profile.Dispose(); throw; }
    }

    public async Task ReopenAsync()
    {
        await Session.DisposeAsync();
        Session = await RuntimeSession.OpenAsync(Profile.Context, new(TextWriter.Null, TextWriter.Null, true), Token);
    }

    // Only test fixtures seed SDK projections. Production code always uses SDK APIs.
    public async Task SeedAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Profile.Context.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Token);
    }

    public async Task<JsonElement> RunAsync(string command, params (string Name, string Value)[] values)
    {
        var invocation = new Invocation(command, values.ToDictionary(pair => pair.Name, pair => pair.Value), Profile.Context.ConfigPath, Profile.Context.Name, true, 60);
        using var stdout = new StringWriter();
        Assert.Equal(Exit.Success, await Commands.ExecuteSessionAsync(invocation, Session.Client, new(stdout, TextWriter.Null, true), Token));
        return JsonSerializer.Deserialize<JsonElement>(stdout.ToString()).GetProperty("data");
    }

    public async ValueTask DisposeAsync()
    {
        try { await Session.DisposeAsync(); }
        finally { Profile.Dispose(); }
    }
}
