using Meshline.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Meshline.Cli;

internal sealed class ClientConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string DefaultProfile { get; set; } = "default";
    public required string ProfilesDirectory { get; set; }
}

internal sealed class ProfileConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public required string Id { get; set; }
    public required string Network { get; set; }
    public required string RpcUrl { get; set; }
    // Omitted when profile.json and SDK data share a directory.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataDirectory { get; set; }
    public required ProtectionConfiguration Protection { get; set; }
    public KeyEnvelope? ProtectedKey { get; set; }
}

internal sealed record ProtectionConfiguration(string Mode, string? KeyFile = null, string? CredentialName = null);
internal sealed record KeyEnvelope(int Version, string KeyId, string Ciphertext, string? Nonce = null, string? Tag = null, string? Salt = null);
internal sealed record ProfileContext(string ConfigPath, string Name, ProfileConfiguration Settings, string? ManifestPath = null)
{
    public string ProfilePath => ManifestPath ?? Configuration.ProfilePath(Configuration.DefaultProfilesDirectory(ConfigPath), Name);
    public string DataDirectory => Settings.DataDirectory ?? Path.GetDirectoryName(ProfilePath)!;
    public NetworkContext Network => NetworkContext.Parse(Settings.Network);
    public string DatabasePath => Path.Combine(DataDirectory, "client.db");
    public string IdentityPath => Path.Combine(DataDirectory, "identity.json");
    public string LockPath => Path.Combine(DataDirectory, "session.lock");
    public string KeyPurpose => $"meshline-cli/master/1/{Settings.Id}/{Settings.Network}";
}

internal static partial class Configuration
{
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileNamePattern();

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static string DefaultProfilesDirectory(string configPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "profiles");

    public static string ResolveProfilesDirectory(string configPath, ClientConfiguration configuration)
        => NormalizePath(Path.GetFullPath(configuration.ProfilesDirectory, Path.GetDirectoryName(Path.GetFullPath(configPath))!));

    public static string ProfilePath(string root, string name)
    {
        ValidateName(name);
        return Path.Combine(root, name, "profile.json");
    }

    public static void ValidateName(string name)
    {
        if (name is null || !ProfileNamePattern().IsMatch(name) || Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new CliException("invalid_profile", "Profile must contain 1-64 letters, digits, hyphens or underscores, beginning with a letter or digit.", Exit.Usage);
    }

    public static async Task<ClientConfiguration> LoadAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) throw new CliException("config_missing", "Application configuration does not exist. Restore the bundled config.json or select an existing file with --config.", Exit.Configuration);
        var text = await File.ReadAllTextAsync(path, token);
        try
        {
            var configuration = Json.Read<ClientConfiguration>(text);
            ValidateApplication(configuration);
            return configuration;
        }
        catch (JsonException e) { throw new CliException("invalid_config", e.Message, Exit.Configuration); }
    }

    static void ValidateApplication(ClientConfiguration configuration)
    {
        if (configuration.SchemaVersion != 1) throw new CliException("config_version", "Unsupported application configuration version.", Exit.Configuration);
        ValidateName(configuration.DefaultProfile);
        if (string.IsNullOrWhiteSpace(configuration.ProfilesDirectory)
            || (Path.IsPathRooted(configuration.ProfilesDirectory) && !Path.IsPathFullyQualified(configuration.ProfilesDirectory)))
            throw new CliException("invalid_profiles_path", "Profiles directory must be absolute or relative to the configuration file.", Exit.Configuration);
    }

    public static async Task<ProfileContext> ResolveAsync(string path, string? name, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        var configuration = await LoadAsync(path, token);
        name ??= configuration.DefaultProfile;
        var profilePath = ProfilePath(ResolveProfilesDirectory(path, configuration), name);
        if (!File.Exists(profilePath)) throw new CliException("profile_missing", $"Profile '{name}' does not exist.", Exit.Configuration);
        var context = new ProfileContext(path, name, await ReadProfileAsync(profilePath, token), profilePath);
        _ = await Identity.LoadAsync(context, token);
        return context;
    }

    internal static async Task<ProfileConfiguration> ReadProfileAsync(string path, CancellationToken token)
    {
        try
        {
            var profile = Json.Read<ProfileConfiguration>(await File.ReadAllTextAsync(path, token));
            Validate(profile);
            if (profile.ProtectedKey is null) throw new CliException("profile_incomplete", "The profile has no protected master key. Restore the complete profile from backup.", Exit.Configuration);
            return profile;
        }
        catch (JsonException e) { throw new CliException("invalid_profile_config", e.Message, Exit.Configuration); }
    }

    public static void Validate(ProfileConfiguration profile)
    {
        if (profile.SchemaVersion != 1) throw new CliException("profile_version", "Unsupported profile configuration version.", Exit.Configuration);
        if (!Guid.TryParseExact(profile.Id, "N", out _)) throw new CliException("invalid_config", "Invalid profile identity.", Exit.Configuration);
        if (!NetworkContext.TryParse(profile.Network, out _)) throw new CliException("invalid_network", "Expected neo:<network-magic>:<registry-hash>.", Exit.Configuration);
        if (!Uri.TryCreate(profile.RpcUrl, UriKind.Absolute, out var rpc) || rpc.Scheme is not ("http" or "https") || rpc.UserInfo.Length > 0 || rpc.Fragment.Length > 0)
            throw new CliException("invalid_rpc", "RPC URL must be HTTP(S), without embedded credentials or fragment.", Exit.Configuration);
        if (profile.DataDirectory is not null && !Path.IsPathFullyQualified(profile.DataDirectory)) throw new CliException("invalid_data_path", "Profile data directory must be absolute.", Exit.Configuration);
        if (profile.Protection?.Mode is not ("file" or "native" or "passphrase")) throw new CliException("invalid_protection", "Protection mode must be file, native or passphrase.", Exit.Configuration);
        if (profile.Protection.Mode == "file" && (profile.Protection.KeyFile is null || !Path.IsPathFullyQualified(profile.Protection.KeyFile)))
            throw new CliException("invalid_key_path", "File protection requires an absolute key file path.", Exit.Configuration);
    }

    public static async Task SaveProfileAsync(string path, string name, ProfileConfiguration profile, bool create, CancellationToken token)
    {
        var app = await LoadAsync(path, token);
        await SaveProfileAsync(new(Path.GetFullPath(path), name, profile, ProfilePath(ResolveProfilesDirectory(path, app), name)), create, token);
    }

    public static async Task SaveProfileAsync(ProfileContext context, bool create, CancellationToken token)
    {
        ValidateName(context.Name);
        Validate(context.Settings);
        _ = await Identity.LoadAsync(context, token);
        if (context.Settings.ProtectedKey is null) throw new CliException("profile_incomplete", "The profile requires a protected master key.", Exit.Configuration);
        using var appLock = FileLock.Acquire(context.ConfigPath + ".lock");
        var app = await LoadAsync(context.ConfigPath, token);
        var profilesDirectory = ResolveProfilesDirectory(context.ConfigPath, app);
        if (!PathComparer.Equals(NormalizePath(ProfilePath(profilesDirectory, context.Name)), NormalizePath(context.ProfilePath)))
            throw new CliException("profile_changed", "The application profiles directory changed. Reload before saving.", Exit.Configuration);
        using var fileLock = FileLock.Acquire(context.ProfilePath + ".lock");
        PrivateFiles.RestrictDataDirectory(Path.GetDirectoryName(context.ProfilePath)!);
        if (create && File.Exists(context.ProfilePath)) throw new CliException("profile_exists", $"Profile '{context.Name}' already exists.", Exit.Configuration);
        if (!create && (!File.Exists(context.ProfilePath) || (await ReadProfileAsync(context.ProfilePath, token)).Id != context.Settings.Id))
            throw new CliException("profile_changed", "The profile was removed or replaced. Reload before saving.", Exit.Configuration);
        if (create)
            foreach (var directory in Directory.EnumerateDirectories(profilesDirectory))
            {
                var otherPath = Path.Combine(directory, "profile.json");
                if (!File.Exists(otherPath)) continue;
                var other = await ReadProfileAsync(otherPath, token);
                if (other.Id == context.Settings.Id || PathComparer.Equals(NormalizePath(other.DataDirectory ?? directory), NormalizePath(context.DataDirectory)))
                    throw new CliException("data_directory_in_use", "Another profile already uses this data directory or identity.", Exit.Configuration);
            }
        await PrivateFiles.WriteAtomicAsync(context.ProfilePath, JsonSerializer.SerializeToUtf8Bytes(context.Settings, Json.Pretty), !create, token);
    }
}
