using System.Text.Json;
using Dapper;
using DashBoard;
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class QueryBuilderService
{
	private readonly SqlConnectionFactory _db;
	private readonly DatabaseSchemaDiscoveryService _schemaDiscovery;
	private readonly DatabaseRelationshipService _relationships;
	private readonly AutomaticJoinPlannerService _joinPlanner;
	private readonly AggregationQueryValidator _validator;
	private readonly DatabaseAggregationSafetyService _safety;
	private readonly AggregationSqlGenerator _sqlGenerator;
	private readonly AggregationQueryExecutionService _executor;

	public QueryBuilderService(
		SqlConnectionFactory db,
		DatabaseSchemaDiscoveryService schemaDiscovery,
		DatabaseRelationshipService relationships,
		AutomaticJoinPlannerService joinPlanner,
		AggregationQueryValidator validator,
		DatabaseAggregationSafetyService safety,
		AggregationSqlGenerator sqlGenerator,
		AggregationQueryExecutionService executor)
	{
		_db = db;
		_schemaDiscovery = schemaDiscovery;
		_relationships = relationships;
		_joinPlanner = joinPlanner;
		_validator = validator;
		_safety = safety;
		_sqlGenerator = sqlGenerator;
		_executor = executor;
	}

	private sealed class TableRow
	{
		public int DatasetTableId { get; set; }
		public string SchemaName { get; set; } = "";
		public string ObjectName { get; set; } = "";
		public string? TableAlias { get; set; }
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

		var referencedKeys = selectedKeys
			.Concat(filters.Select(f => GetString(f, "dimension")))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		const string tablesSql = """
            SELECT
                DatasetTableId,
                SchemaName,
                ObjectName,
                TableAlias
            FROM dash.DatasetTable
            WHERE DatasetKey = @DatasetKey
              AND IsEnabled = 1
            ORDER BY IsPrimary DESC, DatasetTableId;
            """;

		var tables = (await connection.QueryAsync<TableRow>(
			new CommandDefinition(
				tablesSql,
				new { DatasetKey = datasetKey },
				cancellationToken: cancellationToken))).ToList();

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

		var schema = await _schemaDiscovery.DiscoverAsync(
			null,
			cancellationToken);

		var graph = await _relationships.BuildGraphAsync(
			null,
			cancellationToken,
			datasetKey);

		var joinPlan = _joinPlanner.Plan(graph, selectedTables);

		if (!joinPlan.IsValid)
		{
			throw new InvalidOperationException(
				string.Join("; ", joinPlan.Errors));
		}

		var plan = new AggregationQueryPlan
		{
			// Keep only explicitly selected tables here.
			// The join planner may add intermediate tables to joinPlan.
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

		// The validator and safety service must see the complete resolved
		// join plan, including any intermediate tables.
		var validationPlan = new AggregationQueryPlan
		{
			Tables = joinPlan.Tables,

			Joins = joinPlan.Joins.Select(j => new AggregationJoin
			{
				LeftAlias = j.LeftAlias,
				RightAlias = j.RightAlias,
				ConstraintName = j.ConstraintName
			}).ToList(),

			Dimensions = plan.Dimensions,
			Measures = plan.Measures,
			Filters = plan.Filters,
			Sort = plan.Sort,
			Page = plan.Page,
			PageSize = plan.PageSize
		};

		var errors = _validator.Validate(validationPlan, schema);

		if (errors.Count > 0)
		{
			throw new ArgumentException(
				string.Join("; ", errors));
		}

		var safety = _safety.Analyze(graph, validationPlan);

		if (!safety.IsSafe)
		{
			var details = safety.Warnings.Count > 0
				? string.Join("; ", safety.Warnings)
				: "The selected joins may produce incorrect aggregations.";

			throw new InvalidOperationException(
				$"Query failed aggregation-safety validation. {details}");
		}

		var generated = _sqlGenerator.Generate(
			plan,
			joinPlan,
			schema);

		return await _executor.ExecuteAsync(
			generated,
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

	private static object? ConvertFilterValue(
		string? value,
		string dataType,
		string operation)
	{
		if (operation.Equals("isNull", StringComparison.OrdinalIgnoreCase) ||
			operation.Equals("isNotNull", StringComparison.OrdinalIgnoreCase))
			return null;

		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("A filter value is required.");

		var type = dataType.Trim().ToLowerInvariant();

		try
		{
			if (operation.Equals("in", StringComparison.OrdinalIgnoreCase))
			{
				var values = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
				if (values.Length == 0)
					throw new ArgumentException("An IN filter needs at least one value.");

				return values.Select(item => ConvertSingleFilterValue(item, type)).ToArray();
			}

			return ConvertSingleFilterValue(value, type);
		}
		catch (FormatException)
		{
			throw new ArgumentException($"'{value}' is not a valid {dataType} filter value.");
		}
		catch (OverflowException)
		{
			throw new ArgumentException($"'{value}' is outside the supported range for {dataType}.");
		}
	}

	private static object ConvertSingleFilterValue(string value, string type)
	{
		if (type.Contains("bigint")) return long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type.Contains("smallint")) return short.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type.Contains("tinyint")) return byte.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type.Contains("int")) return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money"))
			return decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type.Contains("float")) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type.Contains("real")) return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type.Contains("bit")) return bool.Parse(value);
		if (type.Contains("date") || type.Contains("time"))
			return DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		return value;
	}

	private static bool IsPhysicalColumn(ColumnRow column)
	{
		return string.Equals(
			column.SourceType ?? "COLUMN",
			"COLUMN",
			StringComparison.OrdinalIgnoreCase);
	}

	private static AggregationTable ToAggregationTable(TableRow table)
	{
		return new AggregationTable
		{
			Alias = GetTableAlias(table),
			Schema = table.SchemaName,
			Table = table.ObjectName
		};
	}

	private static string GetTableAlias(TableRow table)
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