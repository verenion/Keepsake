using System.Diagnostics;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.Services;

public static class Formatting
{
    public static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{n / 1024.0 / 1024:0.#} MB",
        _ => $"{n / 1024.0 / 1024 / 1024:0.##} GB",
    };

    public static string Duration(long? ms) => ms is { } m
        ? TimeSpan.FromMilliseconds(m) is var t && t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : TimeSpan.FromMilliseconds(m).ToString(@"m\:ss")
        : "";

    public static string Taken(MediaRecord r) =>
        r.Taken is { } t ? $"{t:ddd d MMM yyyy HH:mm:ss}  ({DateSources.Describe(r.TakenSource)})" : "no date";

    public static string Camera(MediaRecord r)
    {
        var make = r.Make ?? "";
        var model = r.Model ?? "";
        if (model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) make = "";
        return (make + " " + model).Trim();
    }

    public static string Summary(MediaRecord r)
    {
        var parts = new List<string>();
        if (r.Width is { } w && r.Height is { } h) parts.Add($"{w}×{h}");
        if (r.DurationMs is not null) parts.Add(Duration(r.DurationMs));
        parts.Add(Bytes(r.Size));
        return string.Join("  ·  ", parts);
    }

    public static void OpenExternal(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else Process.Start("xdg-open", [Path.GetDirectoryName(path) ?? path]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    }
}
