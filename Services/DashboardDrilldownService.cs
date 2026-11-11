
using Dapper;
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class DashboardDrilldownService
{
	private const int PageSize = 50;

	private readonly SqlConnectionFactory _db;
	private readonly DatasetMetadataProvider _datasetMetadata;
	private readonly DatabaseSchemaDiscoveryService _schemaDiscovery;
	private readonly DatabaseRelationshipService _relationships;
	private readonly AutomaticJoinPlannerService _joinPlanner;

		private sealed class DatasetColumnRow
	{
		public string ColumnKey { get; set; } = "";
		public string ColumnName { get; set; } = "";
		public string Label { get; set; } = "";
		public string Role { get; set; } = "";
		public string SourceType { get; set; } = "COLUMN";
		public int? DatasetTableId { get; set; }
		public string? SourceColumn { get; set; }
	}

	public DashboardDrilldownService(
		SqlConnectionFactory db,
		DatasetMetadataProvider datasetMetadata,
		DatabaseSchemaDiscoveryService schemaDiscovery,
		DatabaseRelationshipService relationships,
		AutomaticJoinPlannerService joinPlanner)
	{
		_db = db;
		_datasetMetadata = datasetMetadata;
		_schemaDiscovery = schemaDiscovery;
		_relationships = relationships;
		_joinPlanner = joinPlanner;
	}

	public async Task<object> QueryAsync(DashDrilldownRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (string.IsNullOrWhiteSpace(request.Dataset))
			throw new ArgumentException("Dataset is required.");

		if (string.IsNullOrWhiteSpace(request.Dimension))
			throw new ArgumentException("The selected chart dimension is required.");

		if (request.Filters is null)
			throw new ArgumentException("Filters cannot be null.");

		if (request.Filters.Count > 20)
			throw new ArgumentException("A maximum of 20 filters is allowed.");

		if (request.Page < 1 || request.Page > int.MaxValue / PageSize)
			throw new ArgumentException("Invalid page number.");

		await using var connection = await _db.OpenAsync();

		// Load enabled physical tables for this dataset.
		const string tablesSql = """
            SELECT
                DatasetTableId,
                SchemaName,
                ObjectName,
                TableAlias,
            FROM dash.DatasetTable
            WHERE DatasetKey = @Dataset
              AND IsEnabled = 1
            ORDER BY IsPrimary DESC, DatasetTableId;
            """;

		var datasetTables = (await connection.QueryAsync<DatasetTableRow>(
			tablesSql,
			new { request.Dataset })).ToList();

		if (datasetTables.Count == 0)
			throw new ArgumentException(
				$"Dataset '{request.Dataset}' has no enabled tables.");

		// Load column metadata, including its physical table mapping.
		const string columnsSql = """
            SELECT
                ColumnKey,
                ColumnName,
                Label,
                Role,
                SourceType,
                DatasetTableId,
                SourceColumn
            FROM dash.DatasetColumn
            WHERE DatasetKey = @Dataset;
            """;

		var datasetColumns = (await connection.QueryAsync<DatasetColumnRow>(
			columnsSql,
			new { request.Dataset })).ToList();

		var columnsByKey = datasetColumns.ToDictionary(
			x => x.ColumnKey,
			StringComparer.OrdinalIgnoreCase);

		if (!columnsByKey.TryGetValue(
				request.Dimension,
				out var selectedDimension))
		{
			throw new ArgumentException(
				$"Unknown dimension '{request.Dimension}'.");
		}

		if (!string.Equals(
				selectedDimension.Role,
				"DIMENSION",
				StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException(
				$"'{request.Dimension}' is not a dimension.");
		}

		// Only physical columns can be returned as raw drilldown rows.
		var outputColumns = datasetColumns
			.Where(c =>
				(string.Equals(c.Role, "DIMENSION",
					StringComparison.OrdinalIgnoreCase) ||
				 string.Equals(c.Role, "MEASURE",
					StringComparison.OrdinalIgnoreCase)) &&
				string.Equals(c.SourceType, "COLUMN",
					StringComparison.OrdinalIgnoreCase) &&
				c.DatasetTableId.HasValue)
			.GroupBy(c => c.ColumnKey, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.ToList();

		if (outputColumns.Count == 0)
			throw new ArgumentException(
				"No physical columns are available for drilldown.");

		// Validate filters and determine all required physical tables.
		var filterColumns = new List<DatasetColumnRow>();

		foreach (var filter in request.Filters)
		{
			if (filter is null)
				throw new ArgumentException("A filter cannot be empty.");

			if (!columnsByKey.TryGetValue(
					filter.Dimension,
					out var filterColumn) ||
				!string.Equals(
					filterColumn.Role,
					"DIMENSION",
					StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException(
					$"Unknown filter dimension '{filter.Dimension}'.");
			}

			if (!filterColumn.DatasetTableId.HasValue ||
				!string.Equals(
					filterColumn.SourceType,
					"COLUMN",
					StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException(
					$"Filter dimension '{filter.Dimension}' has no physical column.");
			}

			filterColumns.Add(filterColumn);
		}

		if (!selectedDimension.DatasetTableId.HasValue ||
			!string.Equals(
				selectedDimension.SourceType,
				"COLUMN",
				StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException(
				$"Dimension '{request.Dimension}' has no physical column.");
		}

		var requiredTableIds = outputColumns
			.Select(c => c.DatasetTableId!.Value)
			.Concat(filterColumns.Select(c => c.DatasetTableId!.Value))
			.Append(selectedDimension.DatasetTableId.Value)
			.ToHashSet();

		var requiredTables = datasetTables
			.Where(t => requiredTableIds.Contains(t.DatasetTableId))
			.Select(t => new AggregationTable
			{
				Alias = string.IsNullOrWhiteSpace(t.TableAlias)
					? $"t{t.DatasetTableId}"
					: t.TableAlias!,
				Schema = t.SchemaName,
				Table = t.ObjectName
			})
			.ToList();

		if (requiredTables.Count == 0)
			throw new ArgumentException(
				"No physical tables could be resolved for drilldown.");

		var aliasByTableId = datasetTables.ToDictionary(
			t => t.DatasetTableId,
			t => string.IsNullOrWhiteSpace(t.TableAlias)
				? $"t{t.DatasetTableId}"
				: t.TableAlias!);

		// Discover physical schema and plan approved dataset joins.
		var schema = await _schemaDiscovery.DiscoverAsync();

		var graph = await _relationships.BuildGraphAsync(
			null,
			datasetKey: request.Dataset);

		var joinPlan = _joinPlanner.Plan(graph, requiredTables);

		if (!joinPlan.IsValid)
			throw new InvalidOperationException(
				string.Join("; ", joinPlan.Errors));

		// Validate every selected column against its actual source table.
		foreach (var column in outputColumns
			.Concat(filterColumns)
			.Append(selectedDimension))
		{
			var table = datasetTables.FirstOrDefault(t =>
				t.DatasetTableId == column.DatasetTableId);

			if (table is null)
				throw new InvalidOperationException(
					$"Table metadata is missing for '{column.ColumnKey}'.");

			var physicalTable = schema.Tables.FirstOrDefault(t =>
				string.Equals(t.SchemaName, table.SchemaName,
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(t.TableName, table.ObjectName,
					StringComparison.OrdinalIgnoreCase));

			var sourceColumn = column.SourceColumn ?? column.ColumnName;

			if (physicalTable is null ||
				!physicalTable.Columns.Any(c =>
					string.Equals(c.ColumnName, sourceColumn,
						StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException(
					$"Physical column '{table.SchemaName}.{table.ObjectName}.{sourceColumn}' " +
					$"for dataset column '{column.ColumnKey}' was not found.");
			}
		}

		// Build SELECT using the correct source alias for every column.
		var selectParts = outputColumns.Select(column =>
		{
			var alias = aliasByTableId[column.DatasetTableId!.Value];
			var sourceColumn = column.SourceColumn ?? column.ColumnName;

			return $"{ColumnReference(alias, sourceColumn)} " +
				   $"AS {QuoteIdentifier(column.ColumnKey)}";
		}).ToList();

		// Build FROM and joins using the existing planner output.
		var sql = new System.Text.StringBuilder();

		sql.AppendLine("SELECT");
		sql.AppendLine("    " + string.Join(",\n    ", selectParts));
		sql.AppendLine("FROM " + GetTableReference(joinPlan.Tables[0]));

		var joinedAliases = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase)
		{
			joinPlan.Tables[0].Alias
		};

		var remainingJoins = joinPlan.Joins.ToList();

		while (remainingJoins.Count > 0)
		{
			var nextJoin = remainingJoins.FirstOrDefault(j =>
				joinedAliases.Contains(j.LeftAlias) &&
				!joinedAliases.Contains(j.RightAlias));

			var reverse = false;

			if (nextJoin is null)
			{
				nextJoin = remainingJoins.FirstOrDefault(j =>
					joinedAliases.Contains(j.RightAlias) &&
					!joinedAliases.Contains(j.LeftAlias));

				reverse = nextJoin is not null;
			}

			if (nextJoin is null)
				throw new InvalidOperationException(
					"The generated join plan is disconnected.");

			var existingAlias = reverse
				? nextJoin.RightAlias
				: nextJoin.LeftAlias;

			var newAlias = reverse
				? nextJoin.LeftAlias
				: nextJoin.RightAlias;

			var newTable = joinPlan.Tables.FirstOrDefault(t =>
				string.Equals(t.Alias, newAlias,
					StringComparison.OrdinalIgnoreCase));

			if (newTable is null)
				throw new InvalidOperationException(
					$"Join table '{newAlias}' was not found.");

			if (nextJoin.Columns.Count == 0)
				throw new InvalidOperationException(
					$"Join '{nextJoin.ConstraintName}' has no column mappings.");

			var conditions = nextJoin.Columns.Select(c =>
			{
				var existingColumn = reverse
					? c.RightColumn
					: c.LeftColumn;

				var newColumn = reverse
					? c.LeftColumn
					: c.RightColumn;

				return $"{ColumnReference(existingAlias, existingColumn)} = " +
					   $"{ColumnReference(newAlias, newColumn)}";
			});

			sql.AppendLine(
				$"INNER JOIN {GetTableReference(newTable)} ON " +
				string.Join(" AND ", conditions));

			joinedAliases.Add(newAlias);
			remainingJoins.Remove(nextJoin);
		}

		// Apply the clicked chart value and remaining filters.
		var whereParts = new List<string>();
		var parameters = new DynamicParameters();
		var parameterIndex = 0;

		AddFilter(
			selectedDimension,
			"eq",
			request.Value,
			aliasByTableId,
			whereParts,
			parameters,
			ref parameterIndex);

		foreach (var filter in request.Filters)
		{
			var column = columnsByKey[filter.Dimension];

			AddFilter(
				column,
				filter.Operator,
				filter.Value,
				aliasByTableId,
				whereParts,
				parameters,
				ref parameterIndex);
		}

		if (whereParts.Count > 0)
			sql.AppendLine("WHERE " + string.Join(" AND ", whereParts));

		// Stable ordering: use the primary keys of the joined tables.
		var orderParts = new List<string>();

		foreach (var joinedTable in joinPlan.Tables)
		{
			var physicalTable = schema.Tables.First(t =>
				string.Equals(t.SchemaName, joinedTable.Schema,
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(t.TableName, joinedTable.Table,
					StringComparison.OrdinalIgnoreCase));

			foreach (var key in physicalTable.PrimaryKeys
				.OrderBy(k => k.KeyPosition))
			{
				orderParts.Add(
					$"{ColumnReference(joinedTable.Alias, key.ColumnName)} ASC");
			}
		}

		if (orderParts.Count == 0)
		{
			var dimensionAlias =
				aliasByTableId[selectedDimension.DatasetTableId!.Value];

			orderParts.Add(
				$"{ColumnReference(dimensionAlias, selectedDimension.SourceColumn ?? selectedDimension.ColumnName)} ASC");
		}

		sql.AppendLine("ORDER BY " + string.Join(", ", orderParts));

		var offset = checked((request.Page - 1) * PageSize);

		parameters.Add("offset", offset);
		parameters.Add("take", PageSize + 1);

		sql.AppendLine("OFFSET @offset ROWS");
		sql.AppendLine("FETCH NEXT @take ROWS ONLY;");

		var fetched = (await connection.QueryAsync(
			sql.ToString(),
			parameters)).ToList();

		var hasMore = fetched.Count > PageSize;
		var rows = fetched.Take(PageSize).ToList();

		return new
		{
			columns = outputColumns.Select(c => new
			{
				key = c.ColumnKey,
				label = c.Label
			}),
			rows,
			page = request.Page,
			pageSize = PageSize,
			hasMore
		};
	}

	private static void AddFilter(
		DatasetColumnRow column,
		string op,
		string? value,
		Dictionary<int, string> aliasByTableId,
		List<string> whereParts,
		DynamicParameters parameters,
		ref int parameterIndex)
	{
		var alias = aliasByTableId[column.DatasetTableId!.Value];
		var columnSql = ColumnReference(
			alias,
			column.SourceColumn ?? column.ColumnName);

		var operation = op?.Trim().ToLowerInvariant();

		if (value is null)
		{
			var nullOperator = operation switch
			{
				"eq" => "IS NULL",
				"neq" => "IS NOT NULL",
				_ => throw new ArgumentException(
					$"Null is only supported with equals/does-not-equal for '{column.ColumnKey}'.")
			};

			whereParts.Add($"{columnSql} {nullOperator}");
			return;
		}

		var sqlOperator = operation switch
		{
			"eq" => "=",
			"neq" => "<>",
			"gt" => ">",
			"gte" => ">=",
			"lt" => "<",
			"lte" => "<=",
			_ => throw new ArgumentException(
				$"Unsupported filter operator: {op}")
		};

		var parameterName = $"p{parameterIndex++}";

		whereParts.Add($"{columnSql} {sqlOperator} @{parameterName}");
		parameters.Add(parameterName, value);
	}

	private static string GetTableReference(AggregationTable table)
	{
		return $"{QuoteIdentifier(table.Schema)}." +
			   $"{QuoteIdentifier(table.Table)} AS " +
			   $"{QuoteIdentifier(table.Alias)}";
	}

	private static string ColumnReference(string alias, string column)
	{
		return $"{QuoteIdentifier(alias)}.{QuoteIdentifier(column)}";
	}

	private static string QuoteIdentifier(string identifier)
	{
		if (string.IsNullOrWhiteSpace(identifier))
			throw new ArgumentException("SQL identifier cannot be empty.");

		return "[" + identifier.Replace("]", "]]") + "]";
	}
}
