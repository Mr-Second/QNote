using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace QNote.Services;

/// <summary>
/// Unpackaged (portable) <see cref="IStartupTaskService"/> over the
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key — the WinRT
/// <see cref="StartupTaskService"/> requires package identity, so the
/// composition root picks this implementation for unpackaged runs. Mapping
/// into the shared <see cref="StartupTaskStatus"/> enum is pragmatic: value
/// present = <see cref="StartupTaskStatus.Enabled"/>, absent =
/// <see cref="StartupTaskStatus.Disabled"/> (the policy states do not exist
/// for Run keys), any registry failure =
/// <see cref="StartupTaskStatus.Unavailable"/>. All failures are expected
/// outcomes: logged + returned as values, never thrown.
/// </summary>
public sealed class RegistryStartupTaskService : IStartupTaskService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Run value name — matches the app / manifest DisplayName.</summary>
    private const string ValueName = "QNote";

    private readonly ILogger<RegistryStartupTaskService> _log;

    public RegistryStartupTaskService(ILogger<RegistryStartupTaskService> log) => _log = log;

    public Task<StartupTaskStatus> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return Task.FromResult(key?.GetValue(ValueName) is null
                ? StartupTaskStatus.Disabled
                : StartupTaskStatus.Enabled);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Startup Run-key state read failed.");
            return Task.FromResult(StartupTaskStatus.Unavailable);
        }
    }

    public Task<StartupTaskStatus> RequestEnableAsync(CancellationToken ct = default)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is null)
            {
                _log.LogError("Startup Run value cannot be written: exe path unavailable.");
                return Task.FromResult(StartupTaskStatus.Unavailable);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                key.SetValue(ValueName, Quote(exePath));
            }
            _log.LogInformation("Startup Run value enabled: {Path}", exePath);
            return Task.FromResult(StartupTaskStatus.Enabled);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Startup Run-key enable failed.");
            return Task.FromResult(StartupTaskStatus.Unavailable);
        }
    }

    public Task<StartupTaskStatus> DisableAsync(CancellationToken ct = default)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            _log.LogInformation("Startup Run value disabled.");
            return Task.FromResult(StartupTaskStatus.Disabled);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Startup Run-key disable failed.");
            return Task.FromResult(StartupTaskStatus.Unavailable);
        }
    }

    /// <summary>
    /// Self-heal (portable folders move): rewrite the Run value with the
    /// CURRENT exe path while startup is enabled, so relocating the portable
    /// directory never leaves a dangling launch entry. No-op when disabled;
    /// best-effort — registry failures are logged, never thrown, and never
    /// block startup.
    /// </summary>
    public void HealRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string current)
                return; // disabled (or unreadable) — nothing to heal

            var exePath = Environment.ProcessPath;
            if (exePath is null)
                return;

            var desired = Quote(exePath);
            if (current == desired)
                return;

            key.SetValue(ValueName, desired);
            _log.LogInformation("Startup Run value self-healed: {Old} -> {New}", current, desired);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup Run-value self-heal failed.");
        }
    }

    /// <summary>Run values need quotes when the path contains spaces; always quoting is the robust convention.</summary>
    private static string Quote(string path) => $"\"{path}\"";
}
