namespace Eslee.OneKey.Infrastructure.Windows;

public sealed class ApplicationPaths
{
    public ApplicationPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "eslee OneKey");
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string SessionFile => Path.Combine(Root, "session.json");
    public string SecretFile => Path.Combine(Root, "secrets.dat");
    public string LogFile => Path.Combine(Root, "logs", "onekey.log");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
    }
}
