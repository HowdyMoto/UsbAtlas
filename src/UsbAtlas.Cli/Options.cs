namespace UsbAtlas.Cli;

// A failure the user can act on: bad arguments, a target that matches nothing or too much, a file that
// won't load. Exit code 3, with the message on stderr.
internal sealed class CliException(string message) : Exception(message);

// The command, its positional values, and --options. Flags take no value; the rest take the next argument
// or one given as --name=value.
internal sealed class Options
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "json", "demo", "redact", "ports", "errors", "help", "verbose", "raw", "devices" };
    private static readonly HashSet<string> Valued = new(StringComparer.OrdinalIgnoreCase) { "format", "input", "out", "min", "for", "since", "max" };
    private readonly Dictionary<string, string> named = new(StringComparer.OrdinalIgnoreCase);
    internal string Command { get; private set; } = "";
    internal List<string> Positional { get; } = [];

    internal static Options Parse(IReadOnlyList<string> args)
    {
        var o = new Options();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "/?") { o.named["help"] = ""; continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg == "--")
            {
                if (o.Command.Length == 0) o.Command = arg.ToLowerInvariant(); else o.Positional.Add(arg);
                continue;
            }
            var (name, value) = arg[2..].Split('=', 2) is [var n, var v] ? (n, v) : (arg[2..], (string?)null);
            if (Flags.Contains(name))
            {
                if (value != null) throw new CliException($"--{name} takes no value.");
                o.named[name] = "";
            }
            else if (Valued.Contains(name))
            {
                if (value == null)
                {
                    if (i + 1 >= args.Count) throw new CliException($"--{name} needs a value.");
                    value = args[++i];
                }
                o.named[name] = value;
            }
            else throw new CliException($"Unknown option --{name}. Run usbatlas-cli help.");
        }
        if (o.named.TryGetValue("format", out var format) && format is not ("json" or "text"))
            throw new CliException("--format is text or json.");
        return o;
    }

    internal bool Has(string name) => named.ContainsKey(name);
    internal string? Get(string name) => named.GetValueOrDefault(name);
    internal bool Json => Has("json") || Get("format") == "json";

    // 90, 90s, 5m, 2h or 1d.
    internal TimeSpan Duration(string name, TimeSpan fallback)
    {
        if (Get(name) is not string text) return fallback;
        return ParseDuration(text) ?? throw new CliException($"--{name} takes a duration such as 30s, 5m, 2h or 1d.");
    }
    internal static TimeSpan? ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant();
        bool suffixed = text.Length > 0 && char.IsLetter(text[^1]);
        char unit = suffixed ? text[^1] : 's';
        string number = suffixed ? text[..^1] : text;
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0 || double.IsInfinity(value)) return null;
        double seconds = unit switch { 's' => value, 'm' => value * 60, 'h' => value * 3600, 'd' => value * 86400, _ => -1 };
        return seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds / 2 ? null : TimeSpan.FromSeconds(seconds);
    }
    internal int Int(string name, int fallback, int min, int max)
    {
        if (Get(name) is not string text) return fallback;
        return int.TryParse(text, out var value) && value >= min && value <= max ? value : throw new CliException($"--{name} takes a whole number from {min} to {max}.");
    }
}
