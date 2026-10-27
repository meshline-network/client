using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Models.Registry;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace Meshline.Cli.Tests;

// A single-account HTTP relay for command tests. Unknown requests fail rather than reaching the network.
internal sealed class AccountRelay : HttpMessageHandler, IRelayRegistry
{
    const string Endpoint = "https://cli-relay.example";
    readonly TestAccountSigner key;
    readonly RelayDescriptor descriptor;
    readonly Dictionary<string, DeviceCertificate> deviceSessions = [];
    int sessions;
    AccountDeviceState? staged;
    ProfileResolveResult? profile;
    public NetworkContext Context { get; }
    public string RelayId { get; }
    public AccountRoute? Route { get; private set; }
    public AccountDeviceState? State { get; private set; }
    public DeviceStatePublishStatus? PublishStatus { get; set; }
    public List<AccountDeviceState> Publications { get; } = [];

    public AccountRelay(NetworkContext context)
    {
        Context = context;
        key = new(context);
        RelayId = RelayIdentity.GetRelayId(key.PublicKey.AsSpan());
        // sha2-256 multihash encoded as a libp2p peer ID; the endpoint is advertised but never dialed.
        byte[] bytes = [0x12, 0x20, .. Enumerable.Repeat((byte)1, 32)];
        var number = new System.Numerics.BigInteger(bytes, true, true);
        var peer = "";
        while (number > 0)
        {
            number = System.Numerics.BigInteger.DivRem(number, 58, out var remainder);
            peer = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"[(int)remainder] + peer;
        }
        descriptor = new()
        {
            RelayId = RelayId, PublicKey = key.PublicKey,
            Endpoints = [Endpoint, "/dns4/cli-relay.example/tcp/4201/p2p/" + peer],
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds(), RelaySignature = []
        };
        descriptor = descriptor with { RelaySignature = [.. key.Sign(descriptor.GetSigningInput(Context))] };
    }

    RelayEntry Entry => new() { RelayId = RelayId, Endpoint = Endpoint, Status = RelayStatus.Active, UpdatedAt = 1 };
    public Task<RelayEntry?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(relayId == RelayId ? Entry : null);
    }
    public async IAsyncEnumerable<RelayEntry> GetRelaysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return Entry;
    }

    public async Task SeedAsync(IAccountSigner account, IEnumerable<DeviceCertificate> certificates)
    {
        var state = new AccountDeviceState { Account = account.AccountId, AccountPublicKey = account.PublicKey, Revision = 7, Certificates = [.. certificates], AccountSignature = [] };
        State = state with { AccountSignature = [.. await account.SignAsync(state.GetSigningInput(Context), TestContext.Current.CancellationToken)] };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var route = new AccountRoute { Account = account.AccountId, AccountPublicKey = account.PublicKey, Revision = 3, RelayId = RelayId, UpdatedAt = now, ExpiresAt = now + 86400, AccountSignature = [] };
        route = route with { AccountSignature = [.. await account.SignAsync(route.GetAccountSigningInput(Context), TestContext.Current.CancellationToken)] };
        Route = SignRoute(route);
    }

    AccountRoute SignRoute(AccountRoute route) => route with { RelaySignature = [.. key.Sign(route.GetRelaySigningInput(Context))] };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("cli-relay.example", request.RequestUri!.Host);
        var method = request.RequestUri.AbsolutePath.Trim('/').Replace('/', '.');
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch (method)
        {
            case "relay.descriptor": return Json(descriptor);
            case "relay.info": return Json(new RelayInfo
            {
                RelayId = RelayId, Name = "CLI test relay", ServerTime = now,
                Limits = new() { MessageRetention = 86400, ChannelTimelineRetention = 86400, MaxChannelSubscriptions = 10, GroupMessageRetention = 86400, MaxGroupMembers = 100, MaxGroupSubscriptions = 10, MaxGroupInviteTtl = 86400 }
            });
            case "auth.challenge": return Json(new AuthenticationChallenge { Nonce = "cli-challenge", CreatedAt = now, ExpiresAt = now + 60 });
            case "auth.account.verify":
            {
                var proof = ProtocolModel.FromJson<AccountAuthenticationRequest>(body!)!;
                var account = AccountAdapter.GetAccountId($"neo:{Context.Reference}", proof.AccountPublicKey.AsSpan());
                Assert.Equal("cli-challenge", proof.Nonce);
                Assert.True(AccountAdapter.VerifySignature(account, proof.AccountPublicKey.AsSpan(), proof.GetSigningInput(account, RelayId, Endpoint, Context), proof.AccountSignature.AsSpan()));
                return Json(new SessionCredentials { Token = "account-" + ++sessions, Mode = SessionMode.Account, ExpiresAt = now + 300 });
            }
            case "auth.device.verify":
            {
                var proof = ProtocolModel.FromJson<DeviceAuthenticationRequest>(body!)!;
                Assert.Null(proof.SignerCertificate.Validate(Context));
                Assert.Equal("cli-challenge", proof.Nonce);
                Assert.True(Ed25519.Verify(proof.DeviceSignature.AsSpan(), proof.SignerCertificate.SigningPublicKey.AsSpan(), proof.GetSigningInput(RelayId, Endpoint, Context)));
                var token = "device-" + ++sessions;
                deviceSessions[token] = proof.SignerCertificate;
                return Json(new SessionCredentials { Token = token, Mode = SessionMode.Device, ExpiresAt = now + 300 });
            }
            case "account.route.resolve": return Route is null ? NotFound() : Json(Route);
            case "account.route.publish":
                var route = ProtocolModel.FromJson<AccountRoute>(body!)!;
                Assert.Null(route.Validate(Context));
                Route = SignRoute(route);
                if (staged is not null) { State = staged; staged = null; }
                return Json(Route);
            case "device.state.resolve": return State is null ? NotFound() : Json(State);
            case "device.state.publish":
                var state = ProtocolModel.FromJson<AccountDeviceState>(body!)!;
                Assert.Null(state.Validate(Context));
                Publications.Add(state);
                var status = PublishStatus ?? (Route is null ? DeviceStatePublishStatus.Staged : DeviceStatePublishStatus.Accepted);
                if (status == DeviceStatePublishStatus.Accepted) { State = state; staged = null; }
                else staged = state;
                return Json(new DeviceStatePublishResponse { Status = status, StagedUntil = status == DeviceStatePublishStatus.Staged ? now + 600 : null });
            case "profile.resolve": return profile is null ? NotFound() : Json(profile);
            case "profile.publish":
                var published = ProtocolModel.FromJson<AccountProfile>(body!)!;
                var signer = deviceSessions[request.Headers.GetValues("X-Meshline-Session").Single()];
                Assert.Equal(signer.Account, published.Account);
                Assert.True(Ed25519.Verify(published.DeviceSignature.AsSpan(), signer.SigningPublicKey.AsSpan(), published.GetSigningInput(Context)));
                profile = new() { Profile = published, SignerCertificate = signer };
                return new(HttpStatusCode.NoContent);
            case "message.timeline.sync": return Json(new MessageTimelinePage { Items = [], Certificates = [], HasMore = false });
            default: throw new InvalidOperationException("Unexpected account test request: " + method);
        }
    }
    static HttpResponseMessage Json(ProtocolModel model, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(model.ToJson(), Encoding.UTF8, "application/json") };
    static HttpResponseMessage NotFound() => Json(new RelayError { Code = "not_found", Message = "No account document" }, HttpStatusCode.BadRequest);
    protected override void Dispose(bool disposing)
    {
        if (disposing) key.Dispose();
        base.Dispose(disposing);
    }
}
