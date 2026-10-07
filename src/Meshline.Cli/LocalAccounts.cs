using System.Runtime.InteropServices;
using System.Text.Json;

namespace Meshline.Cli;

internal static partial class Commands
{
    static async Task<int> ListAccountsAsync(Invocation invocation, Output output, CancellationToken token)
    {
        var app = await Configuration.LoadAsync(invocation.ConfigPath, token);
        var root = Configuration.ResolveProfilesDirectory(invocation.ConfigPath, app);
        var items = new List<object>();
        var issues = new List<object>();
        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (DirectoryNotFoundException) { directories = []; }
        foreach (var directory in directories.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            // Creation staging directories are not local profiles.
            try { Configuration.ValidateName(name); }
            catch (CliException) { continue; }
            try
            {
                var path = Configuration.ProfilePath(root, name);
                if (!File.Exists(path)) throw new CliException("profile_incomplete", "The profile directory has no profile.json.", Exit.Configuration);
                var context = new ProfileContext(invocation.ConfigPath, name, await Configuration.ReadProfileAsync(path, token), path);
                var identity = await Identity.LoadAsync(context, token);
                items.Add(new
                {
                    profile = name, identity.AccountId, identity.Address, network = context.Settings.Network,
                    isDefault = Configuration.PathComparer.Equals(name, app.DefaultProfile)
                });
            }
            catch (Exception error) when (error is CliException or IOException or UnauthorizedAccessException or JsonException)
            {
                var mapped = CliApplication.MapError(error);
                issues.Add(new { profile = name, mapped.Code, mapped.Message });
            }
        }
        var report = new { defaultProfile = app.DefaultProfile, profilesDirectory = root, items, issues };
        if (issues.Count == 0)
        {
            await output.ResultAsync(CommandResult.Success(report));
            return Exit.Success;
        }
        await output.ResultAsync(CommandResult.Failure(new("invalid_local_profiles",
            "Some local profiles could not be read. Details include the readable accounts and each profile issue.", Exit.Configuration, report)));
        return Exit.Configuration;
    }

    sealed class DoctorReport(string configPath, bool networkRequested)
    {
        public string Runtime { get; } = Environment.Version.ToString();
        public string Os { get; } = RuntimeInformation.OSDescription;
        public string ConfigPath { get; } = configPath;
        public bool ConfigurationValid { get; set; }
        public string? Profile { get; set; }
        public string? ProfilePath { get; set; }
        public string ProfileStatus { get; set; } = "not-checked";
        public bool? IdentityExists { get; set; }
        public bool? DatabaseExists { get; set; }
        public string? AccountId { get; set; }
        public bool NetworkRequested { get; } = networkRequested;
        public bool NetworkChecked { get; set; }
        public string NetworkStatus { get; set; } = networkRequested ? "skipped" : "not-requested";
        public List<object> Relays { get; } = [];
        public List<ErrorInfo> Issues { get; } = [];
    }

    static async Task<int> DoctorAsync(Invocation invocation, Output output, CancellationToken token)
    {
        var report = new DoctorReport(invocation.ConfigPath, invocation.Flag("network"));
        try
        {
            var app = await Configuration.LoadAsync(invocation.ConfigPath, token);
            report.ConfigurationValid = true;
            report.Profile = invocation.Profile ?? app.DefaultProfile;
            var root = Configuration.ResolveProfilesDirectory(invocation.ConfigPath, app);
            report.ProfilePath = Configuration.ProfilePath(root, report.Profile);
            var directory = Path.GetDirectoryName(report.ProfilePath)!;
            if (!Path.Exists(directory))
            {
                report.ProfileStatus = "missing";
                // An installation without its default account is a normal first-use state.
                if (invocation.Profile is not null || report.NetworkRequested)
                    throw new CliException("profile_missing", "The selected profile does not exist. Create or import an account before checking its network.", Exit.Configuration);
            }
            else
            {
                report.ProfileStatus = "checking";
                if (!File.Exists(report.ProfilePath))
                    throw new CliException("profile_incomplete", "The profile directory has no profile.json.", Exit.Configuration);
                var context = new ProfileContext(invocation.ConfigPath, report.Profile,
                    await Configuration.ReadProfileAsync(report.ProfilePath, token), report.ProfilePath);
                report.IdentityExists = File.Exists(context.IdentityPath);
                report.DatabaseExists = File.Exists(context.DatabasePath);
                report.AccountId = (await Identity.LoadAsync(context, token)).AccountId;
                report.ProfileStatus = "ready";
                if (report.NetworkRequested)
                {
                    report.NetworkStatus = "checking";
                    using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
                    var registry = new Meshline.Interactions.RpcRelayRegistry(http, new() { Context = context.Network, RpcUrl = new(context.Settings.RpcUrl) });
                    await foreach (var relay in registry.GetRelaysAsync(token)) report.Relays.Add(relay);
                    report.NetworkChecked = true;
                    report.NetworkStatus = "ok";
                }
            }
            await output.ResultAsync(CommandResult.Success(report));
            return Exit.Success;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (report.ProfileStatus == "checking") report.ProfileStatus = "invalid";
            if (report.NetworkStatus == "checking") report.NetworkStatus = "failed";
            var mapped = CliApplication.MapError(error);
            var exit = error is JsonException && report.NetworkStatus != "failed" ? Exit.Configuration : mapped.ExitCode;
            report.Issues.Add(new(mapped.Code, mapped.Message, mapped.Details));
            await output.ResultAsync(CommandResult.Failure(new(mapped.Code, mapped.Message, exit, report)));
            return exit;
        }
    }
}
