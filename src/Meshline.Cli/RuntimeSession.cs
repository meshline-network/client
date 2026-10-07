using Meshline;
using Meshline.Components;
using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Storage;
using Meshline.Transport;
using System.Threading.Channels;

namespace Meshline.Cli;

internal sealed class RuntimeSession : IAsyncDisposable
{
    readonly HttpClient http;
    readonly RelayClientPool pool;
    readonly Vault vault;
    readonly FileLock sessionLock;
    readonly Channel<(string Code, string Message)> diagnostics = Channel.CreateUnbounded<(string, string)>(new() { SingleReader = true });
    readonly Task diagnosticPump;
    public MeshlineClient Client { get; }
    public ProfileContext Context { get; }
    public RpcRelayRegistry Registry { get; }

    RuntimeSession(ProfileContext context, IdentityDocument identity, Vault vault, FileLock sessionLock, Output output)
    {
        Context = context;
        this.vault = vault;
        this.sessionLock = sessionLock;
        http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        Registry = new RpcRelayRegistry(http, new() { Context = context.Network, RpcUrl = new(context.Settings.RpcUrl) });
        var options = new ClientOptions { Context = context.Network, AccountId = identity.AccountId };
        pool = new RelayClientPool(options, Registry);
        Client = new MeshlineClient(options, new() { Path = context.DatabasePath }, pool, vault, new VaultAccountSigner(context, identity, vault));
        Client.BackgroundError += OnBackgroundError;
        if (Environment.GetEnvironmentVariable("MESHLINE_TRACE") == "1")
            foreach (var component in new ClientComponent[] { Client, Client.AccountManager, Client.DeviceManager, Client.ProfileManager, Client.MessageManager, Client.GroupManager, Client.ChannelManager })
                component.StateChanged += (_, args) => diagnostics.Writer.TryWrite(("lifecycle", $"{component.GetType().Name}: {args.PreviousState} -> {args.CurrentState}"));
        diagnosticPump = PumpAsync(output);
    }

    public static async Task<RuntimeSession> OpenAsync(ProfileContext context, Output output, CancellationToken token, Vault? unlockedVault = null)
    {
        var sessionLock = FileLock.Acquire(context.LockPath);
        Vault? vault = null;
        RuntimeSession? session = null;
        try
        {
            PrivateFiles.RestrictDataDirectory(context.DataDirectory);
            vault = unlockedVault ?? await Secrets.OpenAsync(context, token);
            var identity = await Identity.LoadAsync(context, token);
            session = new(context, identity, vault, sessionLock, output);
            await MeshlineDatabase.MigrateAsync(new() { Path = context.DatabasePath }, token);
            PrivateFiles.RestrictFile(context.DatabasePath);
            await session.Client.InitializeAsync(token);
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync();
            else { vault?.Dispose(); sessionLock.Dispose(); }
            throw;
        }
    }

    void OnBackgroundError(object? sender, BackgroundErrorEventArgs args)
    {
        var error = CliApplication.MapError(args.Error);
        diagnostics.Writer.TryWrite((error.Code, $"{args.Operation} ({args.Resource}): {error.Message}"));
    }
    async Task PumpAsync(Output output)
    {
        await foreach (var item in diagnostics.Reader.ReadAllAsync())
        {
            await output.DiagnosticAsync(item.Code, item.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisposeAsync(); }
        finally
        {
            Client.BackgroundError -= OnBackgroundError;
            diagnostics.Writer.TryComplete();
            try { await pool.DisposeAsync(); }
            finally
            {
                http.Dispose(); vault.Dispose(); sessionLock.Dispose();
                await diagnosticPump;
            }
        }
    }
}
