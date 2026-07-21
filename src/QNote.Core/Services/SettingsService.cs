using Microsoft.Extensions.Logging;
using QNote.Data;

namespace QNote.Services;

/// <summary>
/// Key/value settings over the <c>settings</c> table. Implemented for real in the
/// skeleton (it is trivial and a foundation others build on); a typed façade over
/// the full 23-key set arrives with the settings task.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly DbConnectionFactory _factory;
    private readonly ILogger<SettingsService> _log;

    public SettingsService(DbConnectionFactory factory, ILogger<SettingsService> log)
    {
        _factory = factory;
        _log = log;
    }

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
    }
}
