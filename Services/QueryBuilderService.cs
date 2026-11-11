using System.Text.Json;
using Dapper;
using DashBoard;
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class QueryBuilderService
{
	private readonly SqlConnectionFactory _db;
	private readonly DatasetMetadataProvider _datasetMetadata;
	private readonly AggregationQueryPreparationService _preparation;
	private readonly AggregationQueryExecutionService _executor;
	public QueryBuilderService(
		SqlConnectionFactory db,
		DatasetMetadataProvider datasetMetadata,
		AggregationQueryPreparationService preparation,
		AggregationQueryExecutionService executor)
	{
		_db = db;
		_datasetMetadata = datasetMetadata;
		_preparation = preparation;
		_executor = executor;
	}
	private sealed class ColumnRow
	{
		public string ColumnKey { get; set; } = "";
		public string ColumnName { get; set; } = "";
		public string Label { get; set; } = "";
		public string Role { get; set; } = "";
		public string DataType { get; set; } = "";
		public string? Agg { get; set; }
		public string? SourceType { get; set; }
		public int? DatasetTableId { get; set; }
		public string? SourceColumn { get; set; }
	}

	public async Task<IEnumerable<dynamic>> GetDatasetsAsync(
		string department,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(department))
			throw new ArgumentException("Department is required.");

		await using var connection = await _db.OpenAsync();

		const string sql = """
            SELECT DISTINCT
                   d.DatasetKey AS [key],
                   d.DisplayName AS [label]
            FROM dash.Dataset d
            INNER JOIN dash.DatasetAccess da
                ON da.DatasetKey = d.DatasetKey
            WHERE da.Department = @Department
            ORDER BY d.DisplayName;
            """;

		return await connection.QueryAsync(
			new CommandDefinition(
				sql,
				new { Department = department.Trim() },
				cancellationToken: cancellationToken));
	}

	public async Task<object> GetMetadataAsync(
		string department,
		string datasetKey,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(department))
			throw new ArgumentException("Department is required.");

		if (string.IsNullOrWhiteSpace(datasetKey))
			throw new ArgumentException("Dataset is required.");

		await using var connection = await _db.OpenAsync();

		await EnsureDatasetAccessAsync(
			connection,
			department,
			datasetKey,
			cancellationToken);

		const string sql = """
            SELECT
                c.ColumnKey,
                c.ColumnName,
                c.Label,
                c.Role,
                c.DataType,
                c.Agg,
                c.SourceType,
                c.DatasetTableId,
                c.SourceColumn
            FROM dash.DatasetColumn c
            WHERE c.DatasetKey = @DatasetKey
              AND (
                    UPPER(COALESCE(c.SourceType, 'COLUMN')) <> 'COLUMN'
                    OR EXISTS
                    (
                        SELECT 1
                        FROM dash.DatasetTable t
                        WHERE t.DatasetTableId = c.DatasetTableId
                          AND t.DatasetKey = c.DatasetKey
                          AND t.IsEnabled = 1
                    )
              )
            ORDER BY c.Label;
            """;

		var columns = (await connection.QueryAsync<ColumnRow>(
			new CommandDefinition(
				sql,
				new { DatasetKey = datasetKey },
				cancellationToken: cancellationToken))).ToList();

		// Query Builder currently supports physical columns only.
		var usableColumns = columns
			.Where(c => string.Equals(
				c.SourceType ?? "COLUMN",
				"COLUMN",
				StringComparison.OrdinalIgnoreCase))
			.ToList();

		return new
		{
			dimensions = usableColumns
				.Where(c => string.Equals(
					c.Role,
					"dimension",
					StringComparison.OrdinalIgnoreCase))
				.Select(c => new
				{
					key = c.ColumnKey,
					label = c.Label,
					type = c.DataType
				}),

			measures = usableColumns
				.Where(c => string.Equals(
					c.Role,
					"measure",
					StringComparison.OrdinalIgnoreCase))
				.Select(c => new
				{
					key = c.ColumnKey,
					label = c.Label,
					type = c.DataType,
					aggregation = c.Agg ?? "SUM"
				})
		};
	}

	public async Task<IEnumerable<dynamic>> ExecuteAsync(
		string department,
		JsonElement request,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(department))
			throw new ArgumentException("Department is required.");

		var datasetKey = GetString(request, "dataset");

		if (string.IsNullOrWhiteSpace(datasetKey))
			throw new ArgumentException("Dataset is required.");

		var dimensions = GetStringArray(request, "dimensions");
		var measures = GetStringArray(request, "measures");

		if (dimensions.Count == 0 && measures.Count == 0)
			throw new ArgumentException("Select at least one field.");

		var page = GetInt(request, "page", 1);
		var pageSize = GetInt(request, "pageSize", 200);

		if (page < 1 || pageSize < 1 || pageSize > 500)
			throw new ArgumentException("Invalid pagination values.");

		await using var connection = await _db.OpenAsync();

		await EnsureDatasetAccessAsync(
			connection,
			department,
			datasetKey,
			cancellationToken);

		const string columnsSql = """
            SELECT
                ColumnKey,
                ColumnName,
                Label,
                Role,
                DataType,
                Agg,
                SourceType,
                DatasetTableId,
                SourceColumn
            FROM dash.DatasetColumn
            WHERE DatasetKey = @DatasetKey;
            """;

		var columns = (await connection.QueryAsync<ColumnRow>(
			new CommandDefinition(
				columnsSql,
				new { DatasetKey = datasetKey },
				cancellationToken: cancellationToken))).ToList();

		var columnsByKey = columns.ToDictionary(
			c => c.ColumnKey,
			StringComparer.OrdinalIgnoreCase);

		var selectedKeys = dimensions
			.Concat(measures)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (var key in selectedKeys)
		{
			if (!columnsByKey.TryGetValue(key, out var column))
				throw new ArgumentException($"Unknown field '{key}'.");

			if (!IsPhysicalColumn(column))
				throw new ArgumentException(
					$"Field '{key}' is not supported by Query Builder.");

			if (!column.DatasetTableId.HasValue)
				throw new ArgumentException(
					$"Field '{key}' has no source table.");

			if (dimensions.Contains(key, StringComparer.OrdinalIgnoreCase) &&
				!string.Equals(
					column.Role,
					"dimension",
					StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException($"'{key}' is not a dimension.");
			}

			if (measures.Contains(key, StringComparer.OrdinalIgnoreCase) &&
				!string.Equals(
					column.Role,
					"measure",
					StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException($"'{key}' is not a measure.");
			}
		}

		var filters = request.TryGetProperty("filters", out var filterArray) &&
					  filterArray.ValueKind == JsonValueKind.Array
			? filterArray.EnumerateArray().ToList()
			: [];

		foreach (var filter in filters)
		{
			var key = GetString(filter, "dimension");

			if (!columnsByKey.TryGetValue(key, out var column) ||
				!IsPhysicalColumn(column) ||
				(!string.Equals(column.Role, "dimension", StringComparison.OrdinalIgnoreCase) &&
				 !string.Equals(column.Role, "measure", StringComparison.OrdinalIgnoreCase)))
			{
				throw new ArgumentException(
					$"Invalid filter field '{key}'.");
			}

			if (string.IsNullOrWhiteSpace(GetString(filter, "operator")))
				throw new ArgumentException("A filter operator is required.");
		}

		var havingFilters = request.TryGetProperty("havingFilters", out var havingArray) &&
							havingArray.ValueKind == JsonValueKind.Array
			? havingArray.EnumerateArray().ToList()
			: [];

		foreach (var filter in havingFilters)
		{
			var key = GetString(filter, "measure");
			if (!measures.Contains(key, StringComparer.OrdinalIgnoreCase) ||
				!columnsByKey.TryGetValue(key, out var column) ||
				!string.Equals(column.Role, "measure", StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException($"Aggregate filter must reference a selected measure: '{key}'.");
			if (string.IsNullOrWhiteSpace(GetString(filter, "operator")))
				throw new ArgumentException("An aggregate filter operator is required.");
		}

		var referencedKeys = selectedKeys
			.Concat(filters.Select(f => GetString(f, "dimension")))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		var tables = await _datasetMetadata.LoadEnabledTablesAsync(
			connection,
			datasetKey,
			cancellationToken);

		var tablesById = tables.ToDictionary(t => t.DatasetTableId);

		// Fail clearly instead of allowing a KeyNotFoundException when a
		// selected column points to a disabled or unregistered table.
		foreach (var key in referencedKeys)
		{
			var tableId = columnsByKey[key].DatasetTableId!.Value;

			if (!tablesById.ContainsKey(tableId))
			{
				throw new ArgumentException(
					$"The source table for field '{key}' is disabled or unavailable.");
			}
		}

		var selectedTableIds = referencedKeys
			.Select(key => columnsByKey[key].DatasetTableId!.Value)
			.Distinct()
			.ToList();

		var selectedTables = tables
			.Where(t => selectedTableIds.Contains(t.DatasetTableId))
			.Select(ToAggregationTable)
			.ToList();

		var aliasById = tables.ToDictionary(
	t => t.DatasetTableId,
	GetTableAlias);

		var plan = new AggregationQueryPlan
		{
			// Only explicitly selected tables are included here.
			// The preparation service resolves intermediate join tables.
			Tables = selectedTables,
			Page = page,
			PageSize = pageSize
		};

		foreach (var key in dimensions)
		{
			var column = columnsByKey[key];
			var table = tablesById[column.DatasetTableId!.Value];

			plan.Dimensions.Add(new AggregationDimension
			{
				Key = key,
				SourceAlias = aliasById[table.DatasetTableId],
				Column = column.SourceColumn ?? column.ColumnName
			});
		}

		foreach (var key in measures)
		{
			var column = columnsByKey[key];
			var table = tablesById[column.DatasetTableId!.Value];

			plan.Measures.Add(new AggregationMeasure
			{
				Key = key,
				SourceAlias = aliasById[table.DatasetTableId],
				Column = column.SourceColumn ?? column.ColumnName,
				Aggregation = column.Agg ?? "SUM"
			});
		}

		foreach (var filter in filters)
		{
			var key = GetString(filter, "dimension");
			var column = columnsByKey[key];
			var table = tablesById[column.DatasetTableId!.Value];

			plan.Filters.Add(new AggregationFilter
			{
				SourceAlias = aliasById[table.DatasetTableId],
				Column = column.SourceColumn ?? column.ColumnName,
				Operator = GetString(filter, "operator"),
				Value = GetString(filter, "value"),
				DataType = column.DataType
			});
		}

		foreach (var filter in havingFilters)
		{
			var key = GetString(filter, "measure");
			var column = columnsByKey[key];

			plan.HavingFilters.Add(new AggregationHavingFilter
			{
				MeasureKey = key,
				Operator = GetString(filter, "operator"),
				Value = GetString(filter, "value"),
				DataType = column.DataType
			});
		}

		if (request.TryGetProperty("sort", out var sortArray) &&
			sortArray.ValueKind == JsonValueKind.Array)
		{
			foreach (var sort in sortArray.EnumerateArray())
			{
				plan.Sort.Add(new AggregationSort
				{
					Field = GetString(sort, "field"),
					Direction = GetString(sort, "direction")
				});
			}
		}

		var preparation = await _preparation.PrepareAsync(
			plan,
			cancellationToken,
			datasetKey);

		switch (preparation.Failure)
		{
			case AggregationQueryPreparationFailure.JoinPlan:
				throw new InvalidOperationException(
					string.Join("; ", preparation.Errors));

			case AggregationQueryPreparationFailure.Validation:
				throw new ArgumentException(
					string.Join("; ", preparation.Errors));

			case AggregationQueryPreparationFailure.Safety:
				var details = preparation.Safety?.Warnings.Count > 0
					? string.Join("; ", preparation.Safety.Warnings)
					: "The selected joins may produce incorrect aggregations.";

				throw new InvalidOperationException(
					$"Query failed aggregation-safety validation. {details}");
		}

		if (preparation.Generated is null)
		{
			throw new InvalidOperationException(
				"Query preparation did not produce SQL.");
		}

		return await _executor.ExecuteAsync(
			preparation.Generated,
			cancellationToken);
	}


	private static async Task EnsureDatasetAccessAsync(
		System.Data.IDbConnection connection,
		string department,
		string datasetKey,
		CancellationToken cancellationToken)
	{
		const string sql = """
            SELECT COUNT(1)
            FROM dash.DatasetAccess
            WHERE DatasetKey = @DatasetKey
              AND Department = @Department;
            """;

		var hasAccess = await connection.ExecuteScalarAsync<int>(
			new CommandDefinition(
				sql,
				new
				{
					DatasetKey = datasetKey,
					Department = department.Trim()
				},
				cancellationToken: cancellationToken));

		if (hasAccess == 0)
			throw new UnauthorizedAccessException("Dataset not accessible.");
	}

	private static bool IsPhysicalColumn(ColumnRow column)
	{
		return string.Equals(
			column.SourceType ?? "COLUMN",
			"COLUMN",
			StringComparison.OrdinalIgnoreCase);
	}

	private static AggregationTable ToAggregationTable(DatasetTableMetadata table)
	{
		return new AggregationTable
		{
			Alias = GetTableAlias(table),
			Schema = table.SchemaName,
			Table = table.ObjectName
		};
	}

	private static string GetTableAlias(DatasetTableMetadata table)
	{
		return string.IsNullOrWhiteSpace(table.TableAlias)
			? $"t{table.DatasetTableId}"
			: table.TableAlias!;
	}

	private static string GetString(JsonElement element, string name)
	{
		if (element.ValueKind != JsonValueKind.Object ||
			!element.TryGetProperty(name, out var value))
		{
			return "";
		}

		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString() ?? "",
			JsonValueKind.Number => value.ToString(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => ""
		};
	}

	private static List<string> GetStringArray(
		JsonElement element,
		string name)
	{
		if (!element.TryGetProperty(name, out var value) ||
			value.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		return value.EnumerateArray()
			.Where(x => x.ValueKind == JsonValueKind.String)
			.Select(x => x.GetString()!)
			.Where(x => !string.IsNullOrWhiteSpace(x))
			.ToList();
	}

	private static int GetInt(
		JsonElement element,
		string name,
		int fallback)
	{
		return element.TryGetProperty(name, out var value) &&
			   value.TryGetInt32(out var result)
			? result
			: fallback;
	}
}