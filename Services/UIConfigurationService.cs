
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace DashBoard.Services;

public sealed class UIConfigurationService
{
	private const string CacheKey = "dashboard:ui-configuration";
	private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

	private readonly SqlConnectionFactory _db;
	private readonly IMemoryCache _cache;

	public UIConfigurationService(
		SqlConnectionFactory db,
		IMemoryCache cache)
	{
		_db = db;
		_cache = cache;
	}

	public async Task<UIConfiguration> GetAsync(
		CancellationToken cancellationToken = default)
	{
		if (_cache.TryGetValue(CacheKey, out UIConfiguration? cached)
			&& cached is not null)
		{
			return cached;
		}

		const string sql = """
            SELECT SettingKey, SettingValue
            FROM dash.UIConfiguration
            WHERE SettingKey IN (
                'DimensionLabel',
                'MeasureLabel'
            );
            """;

		await using var connection = await _db.OpenAsync();

		var settings = await connection.QueryAsync<SettingRow>(
			new CommandDefinition(
				sql,
				cancellationToken: cancellationToken));

		var values = settings.ToDictionary(
			x => x.SettingKey,
			x => x.SettingValue,
			StringComparer.OrdinalIgnoreCase);

		var configuration = new UIConfiguration
		{
			DimensionLabel = GetValue(
				values, "DimensionLabel", "Dimensions"),

			MeasureLabel = GetValue(
				values, "MeasureLabel", "Measures")
		};

		_cache.Set(
			CacheKey,
			configuration,
			CacheDuration);

		return configuration;
	}

	private static string GetValue(
		IReadOnlyDictionary<string, string> values,
		string key,
		string fallback)
	{
		return values.TryGetValue(key, out var value)
			&& !string.IsNullOrWhiteSpace(value)
				? value.Trim()
				: fallback;
	}

	private sealed class SettingRow
	{
		public string SettingKey { get; set; } = "";
		public string SettingValue { get; set; } = "";
	}
}

public sealed class UIConfiguration
{
	public string DimensionLabel { get; set; } = "Dimensions";
	public string MeasureLabel { get; set; } = "Measures";
}
