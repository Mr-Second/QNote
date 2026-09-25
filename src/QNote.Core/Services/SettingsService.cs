using System.Globalization;
using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Key/value settings over the <c>settings</c> table. Raw <see cref="GetAsync"/>/
/// <see cref="SetAsync"/> plus a typed <see cref="AppSettings"/> façade
/// (<see cref="LoadAsync"/>/<see cref="SaveAsync"/>) with an in-memory cache and a
/// change notification for live re-application (Qt parity: SettingsManager).
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static class Keys
    {
        public const string ThemeMode = "themeMode";
        public const string AlwaysOnTop = "alwaysOnTop";
        public const string LaunchAtStartup = "launchAtStartup";
        public const string RememberWindowGeometry = "rememberWindowGeometry";
        public const string StartMinimized = "startMinimized";
        public const string WindowX = "windowX";
        public const string WindowY = "windowY";
        public const string WindowWidth = "windowWidth";
        public const string WindowHeight = "windowHeight";
        public const string ListDensity = "listDensity";
        public const string TimeFormat = "timeFormat";
        public const string NoteSortOrder = "noteSortOrder";
        public const string ConfirmBeforeDelete = "confirmBeforeDelete";
        public const string SearchSortOrder = "searchSortOrder";
    }

    private readonly DbConnectionFactory _factory;
    private readonly ILogger<SettingsService> _log;

    private AppSettings? _cache;

    public SettingsService(DbConnectionFactory factory, ILogger<SettingsService> log)
    {
        _factory = factory;
        _log = log;
    }

    public event Action<AppSettings>? Changed;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM settings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO settings (Key, Value) VALUES ($key, $value) " +
            "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(ct);
        _cache = null; // raw write invalidates the typed snapshot
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (_cache is not null)
            return _cache;

        var rows = new Dictionary<string, string>();
        await using (var conn = _factory.OpenRead())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Key, Value FROM settings;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows[reader.GetString(0)] = reader.GetString(1);
        }

        var defaults = new AppSettings();
        _cache = new AppSettings
        {
            ThemeMode = Get(Keys.ThemeMode) ?? defaults.ThemeMode,
            AlwaysOnTop = GetBool(Keys.AlwaysOnTop, defaults.AlwaysOnTop),
            LaunchAtStartup = GetBool(Keys.LaunchAtStartup, defaults.LaunchAtStartup),
            RememberWindowGeometry = GetBool(Keys.RememberWindowGeometry, defaults.RememberWindowGeometry),
            StartMinimized = GetBool(Keys.StartMinimized, defaults.StartMinimized),
            WindowX = GetInt(Keys.WindowX, defaults.WindowX),
            WindowY = GetInt(Keys.WindowY, defaults.WindowY),
            WindowWidth = GetInt(Keys.WindowWidth, defaults.WindowWidth),
            WindowHeight = GetInt(Keys.WindowHeight, defaults.WindowHeight),
            ListDensity = GetEnum(Keys.ListDensity, defaults.ListDensity),
            TimeFormat = GetEnum(Keys.TimeFormat, defaults.TimeFormat),
            NoteSortOrder = GetEnum(Keys.NoteSortOrder, defaults.NoteSortOrder),
            ConfirmBeforeDelete = GetBool(Keys.ConfirmBeforeDelete, defaults.ConfirmBeforeDelete),
            SearchSortOrder = GetEnum(Keys.SearchSortOrder, defaults.SearchSortOrder),
        };
        return _cache;

        string? Get(string key) => rows.TryGetValue(key, out var v) ? v : null;

        bool GetBool(string key, bool fallback) =>
            bool.TryParse(Get(key), out var v) ? v : fallback;

        int GetInt(string key, int fallback) =>
            int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        T GetEnum<T>(string key, T fallback) where T : struct, Enum
        {
            var raw = Get(key);
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                && Enum.IsDefined((T)(object)n))
                return (T)(object)n;
            return fallback;
        }
    }

    public async Task<AppSettings> ReloadAsync(CancellationToken ct = default)
    {
        _cache = null;
        var snapshot = await LoadAsync(ct);
        Changed?.Invoke(snapshot);
        return snapshot;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        foreach (var (key, value) in Enumerate(settings))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO settings (Key, Value) VALUES ($key, $value) " +
                "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _cache = settings;
        _log.LogDebug("Settings saved.");
        Changed?.Invoke(settings);
    }

    private static IEnumerable<(string Key, string Value)> Enumerate(AppSettings s)
    {
        static string Bool(bool v) => v ? "true" : "false";
        static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

        yield return (Keys.ThemeMode, s.ThemeMode);
        yield return (Keys.AlwaysOnTop, Bool(s.AlwaysOnTop));
        yield return (Keys.LaunchAtStartup, Bool(s.LaunchAtStartup));
        yield return (Keys.RememberWindowGeometry, Bool(s.RememberWindowGeometry));
        yield return (Keys.StartMinimized, Bool(s.StartMinimized));
        yield return (Keys.WindowX, Int(s.WindowX));
        yield return (Keys.WindowY, Int(s.WindowY));
        yield return (Keys.WindowWidth, Int(s.WindowWidth));
        yield return (Keys.WindowHeight, Int(s.WindowHeight));
        yield return (Keys.ListDensity, Int((int)s.ListDensity));
        yield return (Keys.TimeFormat, Int((int)s.TimeFormat));
        yield return (Keys.NoteSortOrder, Int((int)s.NoteSortOrder));
        yield return (Keys.ConfirmBeforeDelete, Bool(s.ConfirmBeforeDelete));
        yield return (Keys.SearchSortOrder, Int((int)s.SearchSortOrder));
    }
}
