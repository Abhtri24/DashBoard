
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class AggregationQueryValidator
{
	private static readonly HashSet<string> SupportedAggregations =
		new(StringComparer.OrdinalIgnoreCase)
		{
			"SUM",
			"AVG",
			"COUNT",
			"MIN",
			"MAX",
			"COUNT_DISTINCT"
		};

	private static readonly HashSet<string> SupportedOperators =
		new(StringComparer.OrdinalIgnoreCase)
		{
			"eq",
			"neq",
			"gt",
			"gte",
			"lt",
			"lte",
			"contains",
			"startsWith",
			"endsWith",
			"in",
			"isNull",
			"isNotNull"
		};

	// ---------------------------------------------------------
	// 1. Structural validation
	// ---------------------------------------------------------

	public List<string> Validate(AggregationQueryPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);

		var errors = new List<string>();

		if (plan.Tables.Count == 0)
			errors.Add("At least one table must be selected.");

		var aliases = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);

		foreach (var table in plan.Tables)
		{
			if (string.IsNullOrWhiteSpace(table.Alias))
			{
				errors.Add("Every table must have an alias.");
				continue;
			}

			if (!aliases.Add(table.Alias))
				errors.Add($"Duplicate table alias '{table.Alias}'.");

			if (string.IsNullOrWhiteSpace(table.Schema))
				errors.Add($"Schema is missing for alias '{table.Alias}'.");

			if (string.IsNullOrWhiteSpace(table.Table))
				errors.Add($"Table name is missing for alias '{table.Alias}'.");
		}

		if (plan.Tables.Count > 1 &&
			plan.Joins.Count != plan.Tables.Count - 1)
		{
			errors.Add(
				"The number of joins must equal the number of tables minus one.");
		}

		// Joins
		foreach (var join in plan.Joins)
		{
			if (!aliases.Contains(join.LeftAlias))
				errors.Add($"Unknown join alias '{join.LeftAlias}'.");

			if (!aliases.Contains(join.RightAlias))
				errors.Add($"Unknown join alias '{join.RightAlias}'.");

			if (string.Equals(
				join.LeftAlias,
				join.RightAlias,
				StringComparison.OrdinalIgnoreCase))
			{
				errors.Add("A table cannot be joined to the same alias.");
			}

			if (string.IsNullOrWhiteSpace(join.ConstraintName))
				errors.Add("A join must specify a relationship constraint.");
		}

		var fieldKeys = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);

		// Dimensions
		foreach (var dimension in plan.Dimensions)
		{
			if (!aliases.Contains(dimension.SourceAlias))
			{
				errors.Add(
					$"Unknown dimension source alias '{dimension.SourceAlias}'.");
			}

			if (string.IsNullOrWhiteSpace(dimension.Key))
			{
				errors.Add("Every dimension must have a key.");
			}
			else if (!fieldKeys.Add(dimension.Key))
			{
				errors.Add(
					$"Duplicate output field key '{dimension.Key}'.");
			}

			if (string.IsNullOrWhiteSpace(dimension.Column))
			{
				errors.Add(
					$"Column is missing for dimension '{dimension.Key}'.");
			}
		}

		// Measures
		foreach (var measure in plan.Measures)
		{
			if (string.IsNullOrWhiteSpace(measure.Key))
			{
				errors.Add("Every measure must have a key.");
			}
			else if (!fieldKeys.Add(measure.Key))
			{
				errors.Add(
					$"Duplicate output field key '{measure.Key}'.");
			}

			if (!SupportedAggregations.Contains(measure.Aggregation))
			{
				errors.Add(
					$"Unsupported aggregation '{measure.Aggregation}'.");
			}

			if (measure.Expression == null)
			{
				if (!aliases.Contains(measure.SourceAlias))
				{
					errors.Add(
						$"Unknown measure source alias '{measure.SourceAlias}'.");
				}

				if (string.IsNullOrWhiteSpace(measure.Column))
				{
					errors.Add(
						$"Column is missing for measure '{measure.Key}'.");
				}
			}
			else
			{
				ValidateExpression(
					measure.Expression,
					aliases,
					errors);
			}
		}

		// Filters
		foreach (var filter in plan.Filters)
		{
			if (!aliases.Contains(filter.SourceAlias))
			{
				errors.Add(
					$"Unknown filter source alias '{filter.SourceAlias}'.");
			}

			if (string.IsNullOrWhiteSpace(filter.Column))
				errors.Add("A filter column is required.");

			if (!SupportedOperators.Contains(filter.Operator))
			{
				errors.Add(
					$"Unsupported filter operator '{filter.Operator}'.");
			}
		}

		foreach (var filter in plan.HavingFilters)
		{
			if (!plan.Measures.Any(measure => string.Equals(measure.Key, filter.MeasureKey, StringComparison.OrdinalIgnoreCase)))
				errors.Add($"Aggregate filter references unselected measure '{filter.MeasureKey}'.");
			if (!new[] { "eq", "neq", "gt", "gte", "lt", "lte" }.Contains(filter.Operator, StringComparer.OrdinalIgnoreCase))
				errors.Add($"Unsupported aggregate filter operator '{filter.Operator}'.");
		}

		// Sorting
		foreach (var sort in plan.Sort)
		{
			if (!fieldKeys.Contains(sort.Field))
			{
				errors.Add(
					$"Sort field '{sort.Field}' is not a selected output field.");
			}

			if (!string.Equals(
				sort.Direction,
				"asc",
				StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(
				sort.Direction,
				"desc",
				StringComparison.OrdinalIgnoreCase))
			{
				errors.Add(
					$"Invalid sort direction '{sort.Direction}'.");
			}
		}

		// Pagination
		if (plan.Page < 1)
			errors.Add("Page must be greater than zero.");

		if (plan.PageSize < 1 || plan.PageSize > 500)
			errors.Add("Page size must be between 1 and 500.");

		return errors;
	}

	// ---------------------------------------------------------
	// 2. Database schema validation
	// ---------------------------------------------------------

	public List<string> Validate(
		AggregationQueryPlan plan,
		DatabaseSchema schema)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(schema);

		var errors = Validate(plan);

		if (errors.Count > 0)
			return errors;

		var tablesByAlias = plan.Tables.ToDictionary(
			t => t.Alias,
			StringComparer.OrdinalIgnoreCase);

		var discoveredTables = schema.Tables.ToDictionary(
			t => $"{t.SchemaName}.{t.TableName}",
			StringComparer.OrdinalIgnoreCase);

		// Validate selected physical tables.
		foreach (var table in plan.Tables)
		{
			var key = $"{table.Schema}.{table.Table}";

			if (!discoveredTables.ContainsKey(key))
			{
				errors.Add(
					$"Table '{key}' does not exist in the database.");
			}
		}

		// Validate dimensions.
		foreach (var dimension in plan.Dimensions)
		{
			ValidateColumn(
				dimension.SourceAlias,
				dimension.Column,
				"dimension");
		}

		// Validate measures.
		foreach (var measure in plan.Measures)
		{
			if (measure.Expression == null)
			{
				ValidateColumn(
					measure.SourceAlias,
					measure.Column,
					"measure");
			}
			else
			{
				ValidateExpressionColumns(
					measure.Expression,
					ValidateColumn);
			}
		}

		// Validate filters.
		foreach (var filter in plan.Filters)
		{
			ValidateColumn(
				filter.SourceAlias,
				filter.Column,
				"filter");
		}

		return errors;

		// Local function: validates a physical column against the schema.
		void ValidateColumn(
			string alias,
			string column,
			string fieldType)
		{
			if (!tablesByAlias.TryGetValue(
				alias,
				out var selectedTable))
			{
				return;
			}

			var tableKey =
				$"{selectedTable.Schema}.{selectedTable.Table}";

			if (!discoveredTables.TryGetValue(
				tableKey,
				out var databaseTable))
			{
				return;
			}

			var exists = databaseTable.Columns.Any(c =>
				string.Equals(
					c.ColumnName,
					column,
					StringComparison.OrdinalIgnoreCase));

			if (!exists)
			{
				errors.Add(
					$"Column '{column}' does not exist in table '{tableKey}' " +
					$"for the selected {fieldType}.");
			}
		}
	}

	// ---------------------------------------------------------
	// 3. Expression structure validation
	// ---------------------------------------------------------

	private static void ValidateExpression(
		AggregationExpression expression,
		HashSet<string> aliases,
		List<string> errors)
	{
		if (expression == null)
		{
			errors.Add("Expression cannot be null.");
			return;
		}

		switch (expression.Type.ToUpperInvariant())
		{
			case "COLUMN":

				if (!aliases.Contains(expression.SourceAlias))
				{
					errors.Add(
						$"Unknown expression source alias '{expression.SourceAlias}'.");
				}

				if (string.IsNullOrWhiteSpace(expression.Column))
				{
					errors.Add(
						"Expression column cannot be empty.");
				}

				break;

			case "VALUE":

				if (!expression.Value.HasValue)
				{
					errors.Add(
						"Expression numeric value is missing.");
				}

				break;

			case "MULTIPLY":
			case "ADD":
			case "SUBTRACT":
			case "DIVIDE":

				if (expression.Operands == null ||
					expression.Operands.Count < 2)
				{
					errors.Add(
						$"Expression '{expression.Type}' needs at least two operands.");

					break;
				}

				foreach (var operand in expression.Operands)
				{
					ValidateExpression(
						operand,
						aliases,
						errors);
				}

				break;

			default:

				errors.Add(
					$"Unsupported expression type '{expression.Type}'.");

				break;
		}
	}

	// ---------------------------------------------------------
	// 4. Expression physical-column validation
	// ---------------------------------------------------------

	private static void ValidateExpressionColumns(
		AggregationExpression expression,
		Action<string, string, string> validateColumn)
	{
		if (expression.Type.Equals(
			"COLUMN",
			StringComparison.OrdinalIgnoreCase))
		{
			validateColumn(
				expression.SourceAlias,
				expression.Column,
				"expression");

			return;
		}

		if (expression.Operands == null)
			return;

		foreach (var operand in expression.Operands)
		{
			ValidateExpressionColumns(
				operand,
				validateColumn);
		}
	}
}
