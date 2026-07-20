using System.Text.Json;
using Eslee.OneKey.Core;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed class JsonSettingsStore(ApplicationPaths paths) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SettingsFile))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(paths.SettingsFile);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
            ?? new AppSettings();
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(paths.SettingsFile, settings, Options, cancellationToken);
}

public sealed class JsonSessionStore(ApplicationPaths paths) : ISessionStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<AutomationSession?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SessionFile))
        {
            return null;
        }

        await using var stream = File.OpenRead(paths.SessionFile);
        return await JsonSerializer.DeserializeAsync<AutomationSession>(
            stream,
            Options,
            cancellationToken);
    }

    public Task SaveAsync(AutomationSession session, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(paths.SessionFile, session, Options, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(paths.SessionFile))
        {
            File.Delete(paths.SessionFile);
        }
        return Task.CompletedTask;
    }
}

internal static class AtomicJsonFile
{
    public static async Task WriteAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }
}
