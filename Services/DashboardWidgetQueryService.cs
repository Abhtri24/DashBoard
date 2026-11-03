
using System.Text.Json;
using Dapper;
using DashBoard;
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class DashboardWidgetQueryService
{
	private readonly SqlConnectionFactory _db;
	private readonly DatabaseSchemaDiscoveryService _schemaDiscovery;
	private readonly DatabaseRelationshipService _relationships;
	private readonly AutomaticJoinPlannerService _joinPlanner;
	private readonly AggregationQueryValidator _validator;
	private readonly DatabaseAggregationSafetyService _safety;
	private readonly AggregationSqlGenerator _sqlGenerator;
	private readonly AggregationQueryExecutionService _executor;

	public DashboardWidgetQueryService(
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

	private sealed class DatasetTableRow
	{
		public int DatasetTableId { get; set; }
		public string SchemaName { get; set; } = "";
		public string ObjectName { get; set; } = "";
		public string? TableAlias { get; set; }
		public bool IsPrimary { get; set; }
	}

	private sealed class DatasetColumnRow
	{
		public string ColumnKey { get; set; } = "";
		public string ColumnName { get; set; } = "";
		public string Label { get; set; } = "";
		public string Role { get; set; } = "";
		public string DataType { get; set; } = "";
		public string? Agg { get; set; }
		public string? Format { get; set; }
		public string SourceType { get; set; } = "COLUMN";
		public int? DatasetTableId { get; set; }
		public string? SourceColumn { get; set; }
		public string? ExpressionDefinition { get; set; }
	}

	public async Task<IEnumerable<dynamic>> ExecuteAsync(
		string dashboardKey,
		string widgetKey,
		string department,
		int page,
		int pageSize,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(dashboardKey))
			throw new ArgumentException("Dashboard key is required.");

		if (string.IsNullOrWhiteSpace(widgetKey))
			throw new ArgumentException("Widget key is required.");

		if (string.IsNullOrWhiteSpace(department))
			throw new ArgumentException("Department is required.");

		page = Math.Max(1, page);
		pageSize = Math.Clamp(pageSize, 1, 500);

		await using var connection = await _db.OpenAsync();

		// Resolve the widget through an active dashboard
		// and the department's permitted dataset.
		const string widgetSql = """
            SELECT
                w.WidgetId,
                w.WidgetKey,
                w.Title,
                w.WidgetType,
                w.Position,
                w.DatasetKey
            FROM dash.DashboardWidget w
            INNER JOIN dash.Dashboard d
                ON d.DashboardId = w.DashboardId
            INNER JOIN dash.DatasetAccess da
                ON da.DatasetKey = w.DatasetKey
               AND da.Department = @Department
            WHERE d.DashboardKey = @DashboardKey
              AND d.DepartmentKey = @Department
              AND d.IsActive = 1
              AND w.WidgetKey = @WidgetKey
              AND w.IsEnabled = 1
              AND w.DatasetKey IS NOT NULL;
            """;

		var widget = await connection.QuerySingleOrDefaultAsync<DashboardWidgetConfiguration>(
			new CommandDefinition(
				widgetSql,
				new
				{
					DashboardKey = dashboardKey,
					WidgetKey = widgetKey,
					Department = department
				},
				cancellationToken: cancellationToken));

		if (widget is null)
			throw new InvalidOperationException(
				"Widget not found or not accessible.");

		// Load configured measures, dimensions and filters.
		const string measuresSql = """
            SELECT
                WidgetId,
                ColumnKey,
                Position,
                AggregationOverride,
                LabelOverride
            FROM dash.WidgetMeasure
            WHERE WidgetId = @WidgetId
            ORDER BY Position;
            """;

		const string dimensionsSql = """
            SELECT
                WidgetId,
                ColumnKey,
                Position,
                LabelOverride
            FROM dash.WidgetDimension
            WHERE WidgetId = @WidgetId
            ORDER BY Position;
            """;

		const string filtersSql = """
            SELECT
                WidgetId,
                ColumnKey,
                Operator,
                FilterValue,
                Position
            FROM dash.WidgetFilter
            WHERE WidgetId = @WidgetId
              AND IsEnabled = 1
            ORDER BY Position;
            """;

		widget.Measures = (await connection.QueryAsync<WidgetMeasureConfiguration>(
			new CommandDefinition(
				measuresSql,
				new { widget.WidgetId },
				cancellationToken: cancellationToken))).ToList();

		widget.Dimensions = (await connection.QueryAsync<WidgetDimensionConfiguration>(
			new CommandDefinition(
				dimensionsSql,
				new { widget.WidgetId },
				cancellationToken: cancellationToken))).ToList();

		widget.Filters = (await connection.QueryAsync<WidgetFilterConfiguration>(
			new CommandDefinition(
				filtersSql,
				new { widget.WidgetId },
				cancellationToken: cancellationToken))).ToList();

		if (widget.Measures.Count == 0 && widget.Dimensions.Count == 0)
			throw new ArgumentException("Widget has no configured fields.");

		// Load enabled physical tables belonging to the dataset.
		const string tablesSql = """
            SELECT
                DatasetTableId,
                SchemaName,
                ObjectName,
                TableAlias,
                IsPrimary
            FROM dash.DatasetTable
            WHERE DatasetKey = @DatasetKey
              AND IsEnabled = 1
            ORDER BY IsPrimary DESC, DatasetTableId;
            """;

		var datasetTables = (await connection.QueryAsync<DatasetTableRow>(
			new CommandDefinition(
				tablesSql,
				new { widget.DatasetKey },
				cancellationToken: cancellationToken))).ToList();

		if (datasetTables.Count == 0)
			throw new InvalidOperationException(
				$"Dataset '{widget.DatasetKey}' has no enabled tables.");

		// Load dataset column metadata, including calculated expressions.
		const string columnsSql = """
            SELECT
                ColumnKey,
                ColumnName,
                Label,
                Role,
                DataType,
                Agg,
                Format,
                SourceType,
                DatasetTableId,
                SourceColumn,
                ExpressionDefinition
            FROM dash.DatasetColumn
            WHERE DatasetKey = @DatasetKey;
            """;

		var datasetColumns = (await connection.QueryAsync<DatasetColumnRow>(
			new CommandDefinition(
				columnsSql,
				new { widget.DatasetKey },
				cancellationToken: cancellationToken))).ToList();

		var columnsByKey = datasetColumns.ToDictionary(
			x => x.ColumnKey,
			StringComparer.OrdinalIgnoreCase);

		// Collect every column referenced by this widget.
		var selectedColumns = widget.Dimensions
			.Select(x => x.ColumnKey)
			.Concat(widget.Measures.Select(x => x.ColumnKey))
			.Concat(widget.Filters.Select(x => x.ColumnKey))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (var key in selectedColumns)
		{
			if (!columnsByKey.ContainsKey(key))
				throw new InvalidOperationException(
					$"Unknown dataset column '{key}'.");
		}

		var primaryTable = datasetTables.FirstOrDefault(x => x.IsPrimary)
			?? datasetTables[0];

		// Determine which physical tables are needed.
		var referencedTableIds = selectedColumns
			.Select(key => columnsByKey[key].DatasetTableId)
			.Where(id => id.HasValue)
			.Select(id => id!.Value)
			.ToHashSet();


		var selectedTables = datasetTables
			.Where(x => referencedTableIds.Contains(x.DatasetTableId))
			.Select(x => new AggregationTable
			{
				Alias = string.IsNullOrWhiteSpace(x.TableAlias)
					? $"t{x.DatasetTableId}"
					: x.TableAlias!,
				Schema = x.SchemaName,
				Table = x.ObjectName
			})
			.ToList();

		var aliasByTableId = datasetTables.ToDictionary(
			x => x.DatasetTableId,
			x => string.IsNullOrWhiteSpace(x.TableAlias)
				? $"t{x.DatasetTableId}"
				: x.TableAlias!);

		// Discover the physical schema and construct the dataset-specific
		// relationship graph. Only approved dataset relationships are used.
		var schema = await _schemaDiscovery.DiscoverAsync(
			null,
			cancellationToken);

		var graph = await _relationships.BuildGraphAsync(
			null,
			cancellationToken,
			widget.DatasetKey);

		var joinPlan = _joinPlanner.Plan(graph, selectedTables);

		if (!joinPlan.IsValid)
			throw new InvalidOperationException(
				string.Join("; ", joinPlan.Errors));

		var plan = new AggregationQueryPlan
		{
			Tables = joinPlan.Tables,
			Joins = joinPlan.Joins.Select(j => new AggregationJoin
			{
				LeftAlias = j.LeftAlias,
				RightAlias = j.RightAlias,
				ConstraintName = j.ConstraintName
			}).ToList(),
			Page = page,
			PageSize = pageSize
		};

		// Dimensions.
		foreach (var dimension in widget.Dimensions.OrderBy(x => x.Position))
		{
			var column = columnsByKey[dimension.ColumnKey];

			if (!column.DatasetTableId.HasValue)
				throw new InvalidOperationException(
					$"Dimension '{column.ColumnKey}' has no source table.");

			plan.Dimensions.Add(new AggregationDimension
			{
				Key = column.ColumnKey,
				SourceAlias = aliasByTableId[column.DatasetTableId.Value],
				Column = column.SourceColumn ?? column.ColumnName
			});
		}

		// Measures, including calculated expressions.
		foreach (var measure in widget.Measures.OrderBy(x => x.Position))
		{
			var column = columnsByKey[measure.ColumnKey];

			if (!column.DatasetTableId.HasValue)
				throw new InvalidOperationException(
					$"Measure '{column.ColumnKey}' has no source table.");

			var aggregationMeasure = new AggregationMeasure
			{
				Key = column.ColumnKey,
				SourceAlias = aliasByTableId[column.DatasetTableId.Value],
				Aggregation = measure.AggregationOverride
					?? column.Agg
					?? "SUM"
			};

			if (string.Equals(
				column.SourceType,
				"EXPRESSION",
				StringComparison.OrdinalIgnoreCase))
			{
				if (string.IsNullOrWhiteSpace(column.ExpressionDefinition))
					throw new InvalidOperationException(
						$"Expression definition is missing for '{column.ColumnKey}'.");

				aggregationMeasure.Expression = ParseExpression(
					column.ExpressionDefinition,
					column.DatasetTableId.Value,
					datasetColumns,
					aliasByTableId);
			}
			else if (string.Equals(
				column.SourceType,
				"COLUMN",
				StringComparison.OrdinalIgnoreCase))
			{
				aggregationMeasure.Column =
					column.SourceColumn ?? column.ColumnName;
			}
			else
			{
				throw new InvalidOperationException(
					$"Unsupported source type '{column.SourceType}' " +
					$"for measure '{column.ColumnKey}'.");
			}

			plan.Measures.Add(aggregationMeasure);
		}

		// Filters.
		foreach (var filter in widget.Filters.OrderBy(x => x.Position))
		{
			var column = columnsByKey[filter.ColumnKey];

			if (!column.DatasetTableId.HasValue)
				throw new InvalidOperationException(
					$"Filter '{column.ColumnKey}' has no source table.");

			plan.Filters.Add(new AggregationFilter
			{
				SourceAlias = aliasByTableId[column.DatasetTableId.Value],
				Column = column.SourceColumn ?? column.ColumnName,
				Operator = filter.Operator,
				Value = filter.FilterValue
			});
		}

		// Validate the query against the discovered physical schema.
		var validationErrors = _validator.Validate(plan, schema);

		if (validationErrors.Count > 0)
			throw new InvalidOperationException(
				string.Join("; ", validationErrors));

		// Reject aggregations that could produce incorrect results
		// because of unsafe joins.
		var safety = _safety.Analyze(graph, plan);

		if (!safety.IsSafe)
			throw new InvalidOperationException(
				"Unsafe widget aggregation: " +
				string.Join("; ", safety.Warnings));

		var generated = _sqlGenerator.Generate(
			plan,
			joinPlan,
			schema);

		return await _executor.ExecuteAsync(
			generated,
			cancellationToken);
	}

	private static AggregationExpression ParseExpression(
		string json,
		int expectedTableId,
		List<DatasetColumnRow> datasetColumns,
		Dictionary<int, string> aliasByTableId)
	{
		using var document = JsonDocument.Parse(json);

		return ParseExpressionNode(
			document.RootElement,
			expectedTableId,
			datasetColumns,
			aliasByTableId);
	}


	private static AggregationExpression ParseExpressionNode(
		JsonElement element,
		int expectedTableId,
		List<DatasetColumnRow> datasetColumns,
		Dictionary<int, string> aliasByTableId)
	{
		if (element.ValueKind != JsonValueKind.Object)
			throw new InvalidOperationException(
				"Every expression node must be a JSON object.");

		// Physical column reference.
		if (element.TryGetProperty("column", out var columnElement))
		{
			if (columnElement.ValueKind != JsonValueKind.String)
				throw new InvalidOperationException(
					"Expression column must be a string.");

			var columnName = columnElement.GetString();

			if (string.IsNullOrWhiteSpace(columnName))
				throw new InvalidOperationException(
					"Expression column cannot be empty.");

			if (!aliasByTableId.TryGetValue(expectedTableId, out var alias))
				throw new InvalidOperationException(
					$"No table alias found for DatasetTableId '{expectedTableId}'.");

			return new AggregationExpression
			{
				Type = "COLUMN",
				SourceAlias = alias,
				Column = columnName
			};
		}

		// Numeric literal.
		if (element.TryGetProperty("value", out var valueElement))
		{
			if (!valueElement.TryGetDecimal(out var value))
				throw new InvalidOperationException(
					"Expression values must be valid numeric literals.");

			return new AggregationExpression
			{
				Type = "VALUE",
				Value = value
			};
		}

		// Arithmetic operation.
		if (!element.TryGetProperty("operator", out var operatorElement) ||
			operatorElement.ValueKind != JsonValueKind.String)
		{
			throw new InvalidOperationException(
				"Expression node must contain column, value, or operator.");
		}

		var operation = operatorElement
			.GetString()?
			.ToUpperInvariant();

		if (operation is not (
			"MULTIPLY" or
			"ADD" or
			"SUBTRACT" or
			"DIVIDE"))
		{
			throw new InvalidOperationException(
				$"Unsupported expression operator '{operation}'.");
		}

		if (!element.TryGetProperty("operands", out var operandsElement) ||
			operandsElement.ValueKind != JsonValueKind.Array)
		{
			throw new InvalidOperationException(
				$"Expression '{operation}' must contain an operands array.");
		}

		var operands = operandsElement
			.EnumerateArray()
			.Select(x => ParseExpressionNode(
				x,
				expectedTableId,
				datasetColumns,
				aliasByTableId))
			.ToList();

		if (operands.Count < 2)
			throw new InvalidOperationException(
				$"Expression '{operation}' requires at least two operands.");

		return new AggregationExpression
		{
			Type = operation!,
			Operands = operands
		};
	}

}
