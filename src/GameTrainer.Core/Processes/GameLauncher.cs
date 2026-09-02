using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameTrainer.Core.Processes;

public sealed record GameInstallation(
    string GameName,
    string ExecutablePath,
    string InstallDirectory,
    string Platform,
    string? PlatformAppId = null)
{
    public bool IsValid => File.Exists(ExecutablePath);
}

public sealed class GameLauncher
{
    private static readonly Regex AcfPairRegex = new("\\\"(?<key>[^\\\"]+)\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled);

    public GameInstallation? FindInstallation(string gameName, string executableName)
    {
        return FindSteamInstallation(gameName, executableName)
               ?? FindRegistryInstallation(gameName, executableName);
    }

    public Process? Launch(GameInstallation installation)
    {
        if (!installation.IsValid)
            throw new FileNotFoundException("O executável detectado do jogo não existe mais.", installation.ExecutablePath);

        if (installation.Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(installation.PlatformAppId))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{installation.PlatformAppId}",
                UseShellExecute = true
            });
            return null;
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = installation.ExecutablePath,
            WorkingDirectory = installation.InstallDirectory,
            UseShellExecute = true
        });
    }

    private static GameInstallation? FindSteamInstallation(string gameName, string executableName)
    {
        foreach (var steamRoot in GetSteamRoots())
        {
            var libraryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
            foreach (var root in ReadSteamLibraryFolders(steamRoot))
                libraryRoots.Add(root);

            foreach (var libraryRoot in libraryRoots)
            {
                var steamApps = Path.Combine(libraryRoot, "steamapps");
                if (!Directory.Exists(steamApps))
                    continue;

                string[] manifests;
                try
                {
                    manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var manifest in manifests)
                {
                    Dictionary<string, string> values;
                    try
                    {
                        values = ParseAcf(File.ReadAllText(manifest));
                    }
                    catch
                    {
                        continue;
                    }

                    if (!values.TryGetValue("name", out var name)
                        || !name.Equals(gameName, StringComparison.OrdinalIgnoreCase)
                        || !values.TryGetValue("installdir", out var installDirName))
                        continue;

                    var installDirectory = Path.Combine(steamApps, "common", installDirName);
                    var executablePath = FindExecutable(installDirectory, executableName);
                    if (executablePath is null)
                        continue;

                    var fileName = Path.GetFileNameWithoutExtension(manifest);
                    var appId = fileName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase)
                        ? fileName["appmanifest_".Length..]
                        : null;

                    return new GameInstallation(gameName, executablePath, installDirectory, "Steam", appId);
                }
            }
        }

        return null;
    }

    private static GameInstallation? FindRegistryInstallation(string gameName, string executableName)
    {
        var roots = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32)
        };

        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                    continue;

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    var displayName = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)
                        || !displayName.Contains(gameName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var installLocation = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        var executablePath = FindExecutable(installLocation, executableName);
                        if (executablePath is not null)
                            return new GameInstallation(gameName, executablePath, installLocation, "Windows");
                    }

                    var displayIcon = key.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(displayIcon))
                    {
                        var iconPath = displayIcon.Split(',')[0].Trim(' ', '"');
                        if (File.Exists(iconPath)
                            && Path.GetFileName(iconPath).Equals(executableName, StringComparison.OrdinalIgnoreCase))
                        {
                            return new GameInstallation(gameName, iconPath, Path.GetDirectoryName(iconPath)!, "Windows");
                        }
                    }
                }
            }
            catch
            {
                // Registro é apenas uma fonte de descoberta; falhas aqui não impedem outras tentativas.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
                roots.Add(Path.GetFullPath(steamPath));
        }
        catch
        {
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var defaultSteam = Path.Combine(programFilesX86, "Steam");
        if (Directory.Exists(defaultSteam))
            roots.Add(defaultSteam);

        return roots;
    }

    private static IEnumerable<string> ReadSteamLibraryFolders(string steamRoot)
    {
        var file = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file))
            yield break;

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
        {
            var rawPath = match.Groups["path"].Value.Replace("\\\\", "\\");
            if (Directory.Exists(rawPath))
                yield return rawPath;
        }
    }

    private static Dictionary<string, string> ParseAcf(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AcfPairRegex.Matches(text))
            values[match.Groups["key"].Value] = match.Groups["value"].Value;
        return values;
    }

    private static string? FindExecutable(string installDirectory, string executableName)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            return null;

        var direct = Path.Combine(installDirectory, executableName);
        if (File.Exists(direct))
            return direct;

        try
        {
            return Directory.EnumerateFiles(installDirectory, executableName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
