using Meshline.Cli;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using System.Text.Json;

namespace Meshline.Cli.Tests;

public sealed class AccountDeviceCommandsTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static async Task<Invocation> ParseAsync(params string[] args)
    {
        Invocation? invocation = null;
        var parsed = CliParser.Parse(args, (value, _) => { invocation = value; return Task.FromResult(Exit.Success); });
        Assert.Empty(parsed.Errors);
        Assert.Equal(Exit.Success, await parsed.InvokeAsync(cancellationToken: Token));
        return Assert.IsType<Invocation>(invocation);
    }

    [Theory]
    [InlineData("account establish", "certificate-days", "721")]
    [InlineData("account recover", "certificate-days", "721")]
    [InlineData("account establish", "route-days", "3651")]
    [InlineData("account recover", "route-days", "3651")]
    [InlineData("devices renew", "days", "721")]
    [InlineData("devices renew", "days", "0")]
    [InlineData("account establish", "replace-device", "invalid-device")]
    public async Task Invalid_setup_options_fail_before_network_or_device_mutation(string command, string option, string value)
    {
        await using var fixture = await SessionFixture.OpenAsync();
        var invocation = await ParseAsync([.. command.Split(' '), "--" + option, value]);
        var error = await Record.ExceptionAsync(() => Commands.ExecuteSessionAsync(invocation, fixture.Session.Client, new(TextWriter.Null, TextWriter.Null, true), Token));
        var mapped = CliApplication.MapError(Assert.IsAssignableFrom<Exception>(error));
        Assert.Equal("invalid_argument", mapped.Code);
        Assert.Equal(Exit.Usage, mapped.ExitCode);
        Assert.Null(fixture.Session.Client.Device);
    }

    [Fact]
    public async Task Missing_home_route_does_not_renew_the_device()
    {
        await using var fixture = await SessionFixture.OpenAsync();
        var original = await fixture.Session.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(7), Token);
        var missing = await ParseAsync("devices", "renew", "--days", "30");
        var noRoute = await Assert.ThrowsAsync<CliException>(() => Commands.ExecuteSessionAsync(missing, fixture.Session.Client, new(TextWriter.Null, TextWriter.Null, true), Token));
        Assert.Equal("account_not_established", noRoute.Code);
        Assert.Equal(original.ToJson(), fixture.Session.Client.Device!.ToJson());
    }

    [Fact]
    public async Task Full_device_list_reports_selection_and_forwards_the_selected_replacement()
    {
        await using var fixture = await AccountCommandFixture.OpenAsync();
        var certificates = new List<DeviceCertificate>();
        for (var i = 0; i < 8; i++)
        {
            using var keys = await TestDeviceKeys.CreateAsync(fixture.Signer, fixture.Relay.Context, expired: i == 0);
            certificates.Add(keys.Certificate);
        }
        await fixture.Relay.SeedAsync(fixture.Signer, certificates);
        var (exitCode, result) = await fixture.RunAsync("account", "establish");
        Assert.Equal(Exit.Configuration, exitCode);
        var details = EstablishmentDetails(result, "DeviceSelectionRequired");
        var selection = details.GetProperty("deviceSelection");
        Assert.Equal(8, selection.GetProperty("deviceLimit").GetInt32());
        Assert.Equal(7, selection.GetProperty("revision").GetInt64());
        var devices = selection.GetProperty("devices").EnumerateArray().ToArray();
        Assert.Equal(certificates.Select(c => c.GetDeviceId(fixture.Relay.Context)).Order(), devices.Select(d => d.GetProperty("deviceId").GetString()!).Order());
        Assert.All(devices, device => Assert.Equal(fixture.Signer.AccountId, device.GetProperty("certificate").GetProperty("account").GetString()));
        Assert.Empty(fixture.Relay.Publications);

        var removed = certificates[0].GetDeviceId(fixture.Relay.Context);
        var replaced = await fixture.RunAsync("account", "establish", "--replace-device", removed);
        Assert.True(replaced.Result.GetProperty("ok").GetBoolean(), replaced.Result.ToString());
        Assert.Equal(Exit.Success, replaced.ExitCode);
        var published = Assert.Single(fixture.Relay.Publications);
        Assert.Equal(8, published.Certificates.Length);
        Assert.DoesNotContain(published.Certificates, c => c.GetDeviceId(fixture.Relay.Context) == removed);
        Assert.Contains(published.Certificates, c => c.ToJson() == fixture.Client.Device!.ToJson());
        Assert.All(certificates.Skip(1), original => Assert.Contains(published.Certificates, c => c.ToJson() == original.ToJson()));
    }

    [Fact]
    public async Task Replacing_an_active_device_without_history_reports_contact_continuity_details()
    {
        await using var fixture = await AccountCommandFixture.OpenAsync();
        using var keys = await TestDeviceKeys.CreateAsync(fixture.Signer, fixture.Relay.Context);
        await fixture.Relay.SeedAsync(fixture.Signer, [keys.Certificate]);
        var replacedId = keys.Certificate.GetDeviceId(fixture.Relay.Context);
        var (exitCode, result) = await fixture.RunAsync("account", "establish", "--replace-device", replacedId);
        Assert.Equal(Exit.Configuration, exitCode);
        var details = EstablishmentDetails(result, "ContactContinuityUnverifiable");
        var issue = Assert.Single(details.GetProperty("contactIssues").EnumerateArray());
        Assert.Equal("contact_history_unavailable", issue.GetProperty("code").GetString());
        Assert.Equal(replacedId, Assert.Single(details.GetProperty("deviceSelection").GetProperty("devices").EnumerateArray()).GetProperty("deviceId").GetString());
        Assert.Empty(fixture.Relay.Publications);
    }

    [Fact]
    public async Task Missing_bound_device_keys_report_the_real_key_access_failure()
    {
        await using var fixture = await SessionFixture.OpenAsync();
        var device = await fixture.Session.Client.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(7), Token);
        await fixture.SeedAsync("""
            INSERT INTO Messages (LocalSequence, Sender, MessageId, SenderDeviceId, Recipient, CreatedAt, PayloadType, PayloadJson, IsDirect)
            VALUES (1, $account, $message, $device, $account, 1, 'meshline.message.direct', $payload, 1);
            DELETE FROM LocalDevice;
            """, ("$account", device.Account), ("$message", Identifiers.CreateMessageId()),
            ("$device", device.GetDeviceId(fixture.Profile.Context.Network)),
            ("$payload", new DirectMessage { Body = new() { ContentType = "text/plain", Text = "Retained history" } }.ToJson()));
        await fixture.ReopenAsync();
        var invocation = await ParseAsync("account", "establish");
        var error = await Assert.ThrowsAsync<AccountEstablishmentException>(() => Commands.ExecuteSessionAsync(invocation, fixture.Session.Client, new(TextWriter.Null, TextWriter.Null, true), Token));
        var mapped = CliApplication.MapError(error);
        Assert.Equal(Exit.Credentials, mapped.ExitCode);
        var details = EstablishmentDetails(JsonSerializer.SerializeToElement(CommandResult.Failure(mapped), Json.Options), "LocalKeyAccessRequired");
        Assert.Equal("PermanentlyLost", details.GetProperty("keyIssue").GetString());
    }

    [Theory]
    [InlineData(DeviceStatePublishStatus.Accepted)]
    [InlineData(DeviceStatePublishStatus.Staged)]
    public async Task Renewal_reports_authoritative_and_pending_publications(DeviceStatePublishStatus status)
    {
        await using var fixture = await AccountCommandFixture.OpenAsync();
        var established = await fixture.RunAsync("account", "establish", "--certificate-days", "7");
        Assert.True(established.Result.GetProperty("ok").GetBoolean(), established.Result.ToString());
        var original = fixture.Client.Device!;
        var originalState = fixture.Relay.State!.ToJson();
        fixture.Relay.Publications.Clear();
        fixture.Relay.PublishStatus = status;
        var (exitCode, result) = await fixture.RunAsync("devices", "renew", "--days", "30");
        var renewed = fixture.Client.Device!;
        Assert.Equal(original.GetDeviceId(fixture.Relay.Context), renewed.GetDeviceId(fixture.Relay.Context));
        Assert.True(renewed.ExpiresAt > original.ExpiresAt);
        Assert.Equal(30 * 86400, renewed.ExpiresAt - renewed.NotBefore);
        Assert.Equal(renewed.ToJson(), Assert.Single(Assert.Single(fixture.Relay.Publications).Certificates).ToJson());
        if (status == DeviceStatePublishStatus.Accepted)
        {
            Assert.Equal(Exit.Success, exitCode);
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal("Accepted", result.GetProperty("data").GetProperty("publication").GetProperty("status").GetString());
            Assert.Equal(renewed.ToJson(), Assert.Single(fixture.Relay.State!.Certificates).ToJson());
        }
        else
        {
            Assert.Equal(Exit.Pending, exitCode);
            Assert.False(result.GetProperty("ok").GetBoolean());
            var error = result.GetProperty("error");
            Assert.Equal("device_publication_pending", error.GetProperty("code").GetString());
            Assert.Contains("account establish", error.GetProperty("message").GetString());
            var publication = error.GetProperty("details").GetProperty("publication");
            Assert.Equal("Staged", publication.GetProperty("status").GetString());
            Assert.True(publication.GetProperty("stagedUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow);
            Assert.Equal(originalState, fixture.Relay.State!.ToJson());
            fixture.Relay.PublishStatus = DeviceStatePublishStatus.Accepted;
            var reconciled = await fixture.RunAsync("account", "establish");
            Assert.True(reconciled.Result.GetProperty("ok").GetBoolean(), reconciled.Result.ToString());
            Assert.Equal(Exit.Success, reconciled.ExitCode);
            Assert.Equal(renewed.ToJson(), fixture.Client.Device!.ToJson());
            Assert.Equal(renewed.ToJson(), Assert.Single(fixture.Relay.State!.Certificates).ToJson());
        }
    }

    static JsonElement EstablishmentDetails(JsonElement result, string reason)
    {
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("account_establishment_incomplete", result.GetProperty("error").GetProperty("code").GetString());
        var details = result.GetProperty("error").GetProperty("details");
        Assert.Equal(reason, details.GetProperty("reason").GetString());
        return details;
    }

    [Theory]
    [InlineData(AccountEstablishmentReason.SubmissionOutcomeUnknown, Exit.Pending)]
    [InlineData(AccountEstablishmentReason.AuthorizationNotYetEffective, Exit.Pending)]
    [InlineData(AccountEstablishmentReason.DeviceSelectionRequired, Exit.Configuration)]
    [InlineData(AccountEstablishmentReason.ContactContinuityUnverifiable, Exit.Configuration)]
    [InlineData(AccountEstablishmentReason.LocalKeyAccessRequired, Exit.Credentials)]
    [InlineData(AccountEstablishmentReason.AccountSignerRequired, Exit.Permission)]
    [InlineData(AccountEstablishmentReason.SignatureDeclined, Exit.Permission)]
    [InlineData(AccountEstablishmentReason.RelayMismatch, Exit.Usage)]
    [InlineData(AccountEstablishmentReason.OperationParametersConflict, Exit.Usage)]
    [InlineData(AccountEstablishmentReason.AccountStateUnavailable, Exit.Network)]
    [InlineData(AccountEstablishmentReason.StateConflict, Exit.Configuration)]
    public void Setup_failures_retain_reason_and_serializable_dependency_context(AccountEstablishmentReason reason, int exitCode)
    {
        Exception cause;
        try { throw new TimeoutException("Relay deadline"); }
        catch (Exception error) { cause = error; }
        cause.Data["operation"] = "relay.http.device.state.publish";
        cause.Data["timeoutSeconds"] = 15d;
        var mapped = CliApplication.MapError(new AccountEstablishmentException(reason, "Setup requires attention", cause));
        Assert.Equal("account_establishment_incomplete", mapped.Code);
        Assert.Equal(exitCode, mapped.ExitCode);
        var result = JsonSerializer.SerializeToElement(CommandResult.Failure(mapped), Json.Options);
        Assert.False(result.GetProperty("ok").GetBoolean());
        var details = result.GetProperty("error").GetProperty("details");
        Assert.Equal(reason.ToString(), details.GetProperty("reason").GetString());
        Assert.True(details.TryGetProperty("deviceSelection", out _));
        Assert.True(details.TryGetProperty("keyIssue", out _));
        Assert.Empty(details.GetProperty("contactIssues").EnumerateArray());
        var dependency = details.GetProperty("cause");
        Assert.Equal("request_timeout", dependency.GetProperty("code").GetString());
        Assert.Equal("relay.http.device.state.publish", dependency.GetProperty("details").GetProperty("operation").GetString());
        Assert.False(dependency.TryGetProperty("stackTrace", out _));
    }
}
