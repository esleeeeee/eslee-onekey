using System.Text.RegularExpressions;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed partial class FileAppLogger : IAppLogger
{
    private readonly ApplicationPaths _paths;
    private readonly object _sync = new();

    public FileAppLogger(ApplicationPaths paths)
    {
        _paths = paths;
        _paths.EnsureDirectories();
    }

    public void Info(string eventName, string message) => Write("INFO", eventName, message);
    public void Warning(string eventName, string message) => Write("WARN", eventName, message);

    public void Error(string eventName, Exception exception, string message) =>
        Write(
            "ERROR",
            eventName,
            $"{message} ({exception.GetType().Name}: {exception.Message}, " +
            $"HResult=0x{exception.HResult:X8}){Environment.NewLine}{exception}");

    private void Write(string level, string eventName, string message)
    {
        var sanitized = BearerPattern().Replace(message, "Bearer [REDACTED]");
        sanitized = AuthorizationPattern().Replace(sanitized, "Authorization: [REDACTED]");
        lock (_sync)
        {
            File.AppendAllText(
                _paths.LogFile,
                $"{DateTimeOffset.UtcNow:O} | {level} | {eventName} | {sanitized}{Environment.NewLine}");
        }
    }

    [GeneratedRegex("Bearer\\s+[^\\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("Authorization\\s*:[^\\r\\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationPattern();
}
