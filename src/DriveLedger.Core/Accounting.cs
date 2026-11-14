using System.Globalization;
namespace DriveLedger.Core;

public static class Accounting
{
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\') throw new ArgumentException("Absolute drive path required.");
        return char.ToUpperInvariant(path[0]) + path[1..].TrimEnd('\\') + (path.Length == 3 ? "\\" : "");
    }
    public static string[] NormalizePaths(IEnumerable<string> paths) => paths.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal).ToArray();
    public static string? CanonicalPath(IEnumerable<string> paths) => NormalizePaths(paths).FirstOrDefault();
    public static bool IsWithin(string path, string folder) => path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    public static IEnumerable<string> Ancestors(string? path)
    {
        if (path is null) yield break; var current = NormalizePath(path);
        while (current.Length > 3) { var slash = current.LastIndexOf('\\'); current = slash == 2 ? current[..3] : current[..slash]; yield return current; }
    }
    public static string CsvCell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    public static string CsvRow(params object?[] values) => string.Join(",", values.Select(CsvCell));
    public static string DisplayBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"]; double number = bytes; var unit = 0;
        while (Math.Abs(number) >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
        return number.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
