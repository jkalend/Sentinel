using System.Globalization;

namespace Sentinel.Core.Cli;

/// <summary>
/// Shared command-line option parsing for the Sentinel executables. Options are
/// declared per command so a misspelled or misvalued flag fails instead of
/// silently changing behavior (for example, dropping a collector token).
/// </summary>
public static class CliOptions
{
    public static Dictionary<string, string?> Parse(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            var key = arg[2..];
            if (key.Length == 0)
            {
                throw new ArgumentException("Empty option name.");
            }

            if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++index];
            }
            else
            {
                options[key] = null;
            }
        }

        return options;
    }

    /// <param name="allowed">
    /// Option name to whether it takes a value. A <c>false</c> entry is a flag and
    /// must not be followed by a value.
    /// </param>
    public static void Validate(
        string command,
        IReadOnlyDictionary<string, string?> options,
        IReadOnlyDictionary<string, bool> allowed)
    {
        foreach (var (key, value) in options)
        {
            if (!allowed.TryGetValue(key, out var takesValue))
            {
                throw new ArgumentException(
                    $"unknown option '--{key}' for '{command}'; run 'help' for usage.");
            }

            if (takesValue && value is null)
            {
                throw new ArgumentException($"--{key} requires a value.");
            }

            if (!takesValue && value is not null)
            {
                throw new ArgumentException($"--{key} does not take a value.");
            }
        }
    }

    public static string? Get(IReadOnlyDictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var value) ? value : null;

    public static int Integer(IReadOnlyDictionary<string, string?> options, string key, int defaultValue)
    {
        var text = Get(options, key);
        if (text is null)
        {
            return defaultValue;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"--{key} must be an integer.");
    }

    public static long Long(IReadOnlyDictionary<string, string?> options, string key, long defaultValue)
    {
        var text = Get(options, key);
        if (text is null)
        {
            return defaultValue;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"--{key} must be an integer.");
    }
}
