namespace Sentinel.Core.Sysmon;

/// <summary>
/// Splits Windows-style source paths regardless of the host OS. Sysmon XML
/// paths use backslashes even when the engine runs on Linux.
/// </summary>
public static class WindowsPath
{
    public static string? FileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var separator = path.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? path : path[(separator + 1)..];
    }

    public static string? Extension(string? path)
    {
        var name = FileName(path);
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..];
    }
}
