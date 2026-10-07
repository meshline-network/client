using Meshline.Cli;
using System.Text.Json;

namespace Meshline.Cli.Tests;

public sealed class TimeoutTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class PendingRequest : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The fixture must be canceled.");
        }
    }

    [Fact]
    public async Task Published_SDK_deadline_maps_to_request_timeout_instead_of_dependency_cancellation()
    {
        using var fixture = new TemporaryProfile();
        using var http = new HttpClient(new PendingRequest()) { Timeout = Timeout.InfiniteTimeSpan };
        var registry = new Meshline.Interactions.RpcRelayRegistry(http, new()
        {
            Context = fixture.Context.Network,
            RpcUrl = new("http://localhost:10332"),
            RequestTimeout = TimeSpan.FromMilliseconds(20)
        });
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => CliApplication.RunOperationAsync(async token =>
        {
            await foreach (var relay in registry.GetRelaysAsync(token)) { }
            return Exit.Success;
        }, 60, Token));
        var mapped = CliApplication.MapError(timeout);
        Assert.Equal("request_timeout", mapped.Code);
        var details = JsonSerializer.SerializeToElement(mapped.Details, Json.Options);
        Assert.Equal("registry.getversion", details.GetProperty("operation").GetString());
        Assert.Equal(0.02, details.GetProperty("timeoutSeconds").GetDouble());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    public async Task Dependency_cancellation_is_not_a_command_deadline(double seconds)
    {
        var error = await Assert.ThrowsAsync<CliException>(() => CliApplication.RunOperationAsync(
            _ => Task.FromException<int>(new OperationCanceledException("Dependency stopped.")), seconds, Token));
        Assert.Equal("dependency_canceled", error.Code);
        Assert.Equal(Exit.Network, error.ExitCode);
    }

    [Fact]
    public async Task Caller_cancellation_stays_canceled()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = CliApplication.RunOperationAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Exit.Success;
        }, 120, caller.Token);
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("canceled", CliApplication.MapError(error).Code);
    }

    [Fact]
    public async Task Only_an_expired_command_deadline_is_operation_timeout()
    {
        var error = await Assert.ThrowsAsync<CliException>(() => CliApplication.RunOperationAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Exit.Success;
        }, 0.01, Token));
        Assert.Equal("operation_timeout", error.Code);
        Assert.Equal(Exit.Pending, error.ExitCode);
    }

    [Fact]
    public async Task Request_timeout_keeps_its_context_in_structured_output()
    {
        var timeout = new TimeoutException("Registry request timed out.", new TaskCanceledException());
        timeout.Data["operation"] = "registry.getversion";
        timeout.Data["timeoutSeconds"] = 15d;
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => CliApplication.RunOperationAsync(
            _ => Task.FromException<int>(timeout), 120, Token));
        Assert.Same(timeout, thrown);
        var error = CliApplication.MapError(thrown);
        Assert.Equal("request_timeout", error.Code);
        Assert.Equal(Exit.Pending, error.ExitCode);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(CommandResult.Failure(error), Json.Options));
        var details = json.RootElement.GetProperty("error").GetProperty("details");
        Assert.Equal("registry.getversion", details.GetProperty("operation").GetString());
        Assert.Equal(15, details.GetProperty("timeoutSeconds").GetDouble());
    }
}
