namespace PGPMint.Cli;

internal sealed record CommandLine(string Command, IReadOnlyDictionary<string, string> Options)
{
    internal string? Get(string name) => Options.GetValueOrDefault(name);
    internal bool Has(string name) => Options.ContainsKey(name);
    internal int Number(string name, int fallback) => Get(name) is { } value ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    internal static CommandLine Parse(string[] args)
    {
        string command = args.FirstOrDefault() ?? "help";
        if (command is "--help" or "-h") command = "help";
        string[] allowed = command switch
        {
            "devices" => ["json", "backend"],
            "verify" => ["backend", "device", "workgroup"],
            "benchmark" => ["backend", "device", "workgroup", "seconds", "rounds", "pattern", "mode", "timestamps"],
            "search" => ["backend", "device", "pattern", "data-dir", "seconds", "password-stdin"],
            "export" => ["key", "name", "email", "output", "data-dir", "password-stdin"],
            "help" or "metal-source" => [],
            _ => throw new ArgumentException("Unknown command. Run pgpmint --help.")
        };
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected a named option.");
            string name = args[i][2..];
            if (!allowed.Contains(name)) throw new ArgumentException($"Unsupported option --{name}.");
            string value = "true";
            if (name is not ("json" or "password-stdin"))
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for --{name}.");
                value = args[i];
            }
            if (!options.TryAdd(name, value)) throw new ArgumentException($"Duplicate option --{name}.");
        }
        foreach (string name in new[] { "seconds", "rounds", "timestamps", "workgroup" })
            if (options.TryGetValue(name, out string? value) &&
                (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) || number <= 0))
                throw new ArgumentException($"--{name} requires a positive integer.");
        if (options.TryGetValue("backend", out string? backend) && backend is not ("auto" or "vulkan" or "opencl" or "metal"))
            throw new ArgumentException("Backend must be auto, vulkan, opencl or metal.");
        if (options.TryGetValue("workgroup", out string? group) && group is not ("64" or "128" or "256"))
            throw new ArgumentException("Workgroup must be 64, 128 or 256.");
        if (options.TryGetValue("mode", out string? mode) && mode is not ("find-one" or "enumerate-all"))
            throw new ArgumentException("Mode must be find-one or enumerate-all.");
        if (command == "search" && !options.ContainsKey("pattern")) throw new ArgumentException("Search requires --pattern.");
        if (command == "export" && (!options.ContainsKey("key") || !options.ContainsKey("name") || !options.ContainsKey("output")))
            throw new ArgumentException("Export requires --key, --name and --output.");
        return new(command, options);
    }
}
