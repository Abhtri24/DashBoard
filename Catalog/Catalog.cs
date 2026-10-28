using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace DashBoard.Catalog;

public record DimensionDef(string Key, string Column, string Label, string Type, string? Sort = null);

public record MeasureDef(string Key, string Label, string Sql, string Format);

public record DatasetDef(
	string Dataset,
	string Source,
	List<DimensionDef> Dimensions,
	List<MeasureDef> Measures)
{
	public DimensionDef Dimension(string key) =>
		Dimensions.FirstOrDefault(d => d.Key == key)
		?? throw new ArgumentException($"Unknown dimension '{key}'");

	public MeasureDef Measure(string key) =>
		Measures.FirstOrDefault(m => m.Key == key)
		?? throw new ArgumentException($"Unknown measure '{key}'");
}

public class CatalogService
{
	private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

	private readonly SqlConnectionFactory _db;
	private readonly IMemoryCache _cache;

	public CatalogService(SqlConnectionFactory db, IMemoryCache cache)
	{
		_db = db;
		_cache = cache;
	}

	public Task<DatasetDef> GetAsync(string name) =>
		_cache.GetOrCreateAsync($"catalog:{name}", entry =>
		{
			entry.AbsoluteExpirationRelativeToNow = CacheFor;
			return LoadAsync(name);
		})!;

	private async Task<DatasetDef> LoadAsync(string name)
	{
		await using var connection = await _db.OpenAsync();

		var dataset = await connection.QuerySingleOrDefaultAsync<DatasetRow>(
			"SELECT SourceObject FROM dash.Dataset WHERE DatasetKey = @name",
			new { name })
			?? throw new ArgumentException($"Unknown dataset '{name}'");

		var columns = (await connection.QueryAsync<ColumnRow>(
			"""
			SELECT ColumnKey, ColumnName, Label, Role, DataType, Agg, Format
			FROM dash.DatasetColumn
			WHERE DatasetKey = @name
			ORDER BY Label
			""",
			new { name })).ToList();

		var dimensions = columns
			.Where(c => c.Role == "dimension")
			.Select(c => new DimensionDef(c.ColumnKey, Quote(c.ColumnName), c.Label, c.DataType))
			.ToList();

		var measures = columns
			.Where(c => c.Role == "measure")
			.Select(c => new MeasureDef(
				c.ColumnKey, c.Label, BuildAggregate(c.Agg!, c.ColumnName), c.Format ?? "number"))
			.ToList();

		return new DatasetDef(name, dataset.SourceObject, dimensions, measures);
	}

	private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

	private static string BuildAggregate(string agg, string column) => agg switch
	{
		"SUM" or "AVG" or "MIN" or "MAX" or "COUNT" => $"{agg}({Quote(column)})",
		"COUNT_DISTINCT" => $"COUNT(DISTINCT {Quote(column)})",
		_ => throw new InvalidOperationException($"Unsupported aggregate '{agg}'.")
	};

	private record DatasetRow(string SourceObject);
	private record ColumnRow(
		string ColumnKey, string ColumnName, string Label,
		string Role, string DataType, string? Agg, string? Format);
}