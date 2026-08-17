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

    /// <summary>Discord RPC OAuth 토큰 보관 파일입니다. DPAPI로만 기록합니다.</summary>
    public string RpcSecretFile => Path.Combine(Root, "rpc-secrets.dat");
    public string LogFile => Path.Combine(Root, "logs", "onekey.log");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
    }
}
