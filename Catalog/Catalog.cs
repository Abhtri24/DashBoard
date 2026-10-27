using System.Text.Json;

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
	private readonly Dictionary<string, DatasetDef> _datasets = new();

	public CatalogService(string folder)
	{
		if (!Directory.Exists(folder))
			throw new DirectoryNotFoundException($"Catalog folder not found: {folder}");

		var files = Directory.GetFiles(folder, "*.json");
		if (files.Length == 0)
			throw new InvalidOperationException($"No catalog files (*.json) found in: {folder}");

		var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

		foreach (var file in files)
		{
			var ds = LoadFile(file, options);

			if (!_datasets.TryAdd(ds.Dataset, ds))
				throw new InvalidOperationException(
					$"Duplicate dataset '{ds.Dataset}' in catalog file: {file}");
		}
	}

	private static DatasetDef LoadFile(string file, JsonSerializerOptions options)
	{
		var text = File.ReadAllText(file);
		if (string.IsNullOrWhiteSpace(text))
			throw new InvalidOperationException($"Catalog file is empty: {file}");

		DatasetDef? ds;
		try
		{
			ds = JsonSerializer.Deserialize<DatasetDef>(text, options);
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException(
				$"Invalid JSON in catalog file: {file}. {ex.Message}", ex);
		}

		if (ds is null)
			throw new InvalidOperationException($"Catalog file contains no dataset: {file}");

		if (string.IsNullOrWhiteSpace(ds.Dataset) || string.IsNullOrWhiteSpace(ds.Source)
			|| ds.Dimensions is null || ds.Measures is null)
			throw new InvalidOperationException(
				$"Catalog file is missing required fields (dataset, source, dimensions, measures): {file}");

		RejectDuplicateKeys(file, "dimension", ds.Dimensions.Select(d => d.Key));
		RejectDuplicateKeys(file, "measure", ds.Measures.Select(m => m.Key));

		return ds;
	}

	private static void RejectDuplicateKeys(string file, string kind, IEnumerable<string> keys)
	{
		var dupe = keys.GroupBy(k => k).FirstOrDefault(g => g.Count() > 1);
		if (dupe is not null)
			throw new InvalidOperationException(
				$"Duplicate {kind} key '{dupe.Key}' in catalog file: {file}");
	}
	public DatasetDef Get(string name)
	{
		if (_datasets.TryGetValue(name, out var ds))
		{
			return ds;
		}
		throw new ArgumentException($"Unknown dataset '{name}'");
	}
}