using System.CommandLine;

namespace Meshline.Cli;

internal static class CliParser
{
    public static ParseResult Parse(string[] args, Func<Invocation, CancellationToken, Task<int>> execute, Invocation? defaults = null)
    {
        var root = new RootCommand(defaults is null
            ? "Meshline reference client. Account, contacts, conversations, groups and channels."
            : "Meshline interactive session. Use cancel or Ctrl+C to cancel a command; exit closes the session. Account setup and key management must run outside interactive mode.");
        var config = new Option<string>("--config") { Description = "Application configuration JSON path; defaults to config.json next to the application.", Recursive = true, DefaultValueFactory = _ => defaults?.ConfigPath ?? Configuration.DefaultPath };
        var profile = new Option<string>("--profile") { Description = "Local account profile; defaults to the application's defaultProfile. For account create/import, names a new profile and must not already exist.", Recursive = true };
        var jsonOption = new Option<bool>("--json") { Description = "Write structured JSON results (NDJSON for watch).", Recursive = true };
        var timeout = new Option<double>("--timeout") { Description = "Operation deadline in seconds; 0 disables it (watch defaults to no deadline).", Recursive = true, DefaultValueFactory = _ => defaults?.TimeoutSeconds ?? 60 };
        if (defaults is null) { root.Options.Add(config); root.Options.Add(profile); root.Options.Add(jsonOption); }
        root.Options.Add(timeout);
        var nodes = new Dictionary<string, Command> { [""] = root };
        foreach (var spec in Commands.Catalog.Where(spec => defaults is null || Interactive.Supports(spec.Path)))
        {
            var parts = spec.Path.Split(' ');
            var path = "";
            var node = root as Command;
            foreach (var part in parts)
            {
                var parent = node;
                path = path.Length == 0 ? part : path + " " + part;
                if (!nodes.TryGetValue(path, out node))
                {
                    node = new Command(part, path == spec.Path ? spec.Description : $"Manage {part}.");
                    nodes[path] = node;
                    parent.Subcommands.Add(node);
                }
            }
            var options = spec.Options.ToDictionary(name => name, name => new Option<string>("--" + name)
            {
                Description = spec.OptionDescriptions.GetValueOrDefault(name) ?? name.Replace('-', ' '),
                Required = spec.RequiredOptions.Contains(name)
            });
            var flags = spec.Flags.ToDictionary(name => name, name => new Option<bool>("--" + name) { Description = spec.OptionDescriptions.GetValueOrDefault(name) ?? name.Replace('-', ' ') });
            var arguments = spec.Arguments.ToDictionary(name => name, name => new Argument<string>(name));
            foreach (var option in options.Values) node.Options.Add(option);
            foreach (var flag in flags.Values) node.Options.Add(flag);
            foreach (var argument in arguments.Values) node.Arguments.Add(argument);
            node.SetAction(async (parsed, token) =>
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, option) in options) if (parsed.GetValue(option) is { } value) values[name] = value;
                foreach (var (name, flag) in flags) if (parsed.GetValue(flag)) values[name] = "true";
                foreach (var (name, argument) in arguments) if (parsed.GetValue(argument) is { } value) values[name] = value;
                var seconds = parsed.GetValue(timeout);
                var explicitTimeout = parsed.GetResult(timeout) is { Implicit: false };
                if (!explicitTimeout && spec.Path is "watch" or "daemon run") seconds = 0;
                if (seconds < 0 || !double.IsFinite(seconds) || seconds > 86400) throw new CliException("invalid_timeout", "Timeout must be between 0 and 86400 seconds.", Exit.Usage);
                var invocation = new Invocation(spec.Path, values, defaults?.ConfigPath ?? Path.GetFullPath(parsed.GetValue(config)!), defaults?.Profile ?? parsed.GetValue(profile), defaults?.Json ?? parsed.GetValue(jsonOption), seconds);
                return await execute(invocation, token);
            });
        }
        return root.Parse(args);
    }
}
