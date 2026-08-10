using Eslee.OneKey.Core;
using Microsoft.Win32;

namespace Eslee.OneKey.Infrastructure.Windows;

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "eslee OneKey";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("시작 프로그램 실행 경로가 필요합니다.", nameof(executablePath));
            }
            key.SetValue(ValueName, $"\"{executablePath}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
