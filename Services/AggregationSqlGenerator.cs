using System.Text;
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class GeneratedAggregationSql
{
	public string Sql { get; set; } = "";

	public Dictionary<string, object?> Parameters { get; set; } = [];
}

public sealed class AggregationSqlGenerator
{
	public GeneratedAggregationSql Generate(
		AggregationQueryPlan query,
		AutomaticJoinPlan joinPlan,
		DatabaseSchema schema)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentNullException.ThrowIfNull(joinPlan);
		ArgumentNullException.ThrowIfNull(schema);

		if (!joinPlan.IsValid)
		{
			throw new InvalidOperationException(
				"Cannot generate SQL from an invalid join plan: " +
				string.Join("; ", joinPlan.Errors));
		}

		if (query.Dimensions.Count == 0 &&
			query.Measures.Count == 0)
		{
			throw new InvalidOperationException(
				"Select at least one dimension or measure.");
		}

		if (query.Page < 1 || query.PageSize < 1 ||
			query.PageSize > 500)
		{
			throw new InvalidOperationException(
				"Invalid pagination values.");
		}

		ValidateJoinPlan(joinPlan, schema);

		var selectedAliases = query.Tables
			.Select(t => t.Alias)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var planAliases = joinPlan.Tables
			.Select(t => t.Alias)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		foreach (var alias in selectedAliases)
		{
			if (!planAliases.Contains(alias))
			{
				throw new InvalidOperationException(
					$"Selected table alias '{alias}' is missing from the join plan.");
			}
		}

		var outputKeys = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);

		var selectParts = new List<string>();

		// Dimensions
		foreach (var dimension in query.Dimensions)
		{
			ValidateSourceColumn(
				joinPlan,
				schema,
				dimension.SourceAlias,
				dimension.Column);

			if (string.IsNullOrWhiteSpace(dimension.Key) ||
				!outputKeys.Add(dimension.Key))
			{
				throw new InvalidOperationException(
					$"Invalid or duplicate output key '{dimension.Key}'.");
			}

			selectParts.Add(
				$"{ColumnReference(dimension.SourceAlias, dimension.Column)} " +
				$"AS {QuoteIdentifier(dimension.Key)}");
		}

		// Measures
		foreach (var measure in query.Measures)
		{
			ValidateSourceColumn(
				joinPlan,
				schema,
				measure.SourceAlias,
				measure.Column);

			if (string.IsNullOrWhiteSpace(measure.Key) ||
				!outputKeys.Add(measure.Key))
			{
				throw new InvalidOperationException(
					$"Invalid or duplicate output key '{measure.Key}'.");
			}

			var aggregation = measure.Aggregation.Trim().ToUpperInvariant();

			var expression = aggregation switch
			{
				"SUM" => $"SUM({ColumnReference(measure.SourceAlias, measure.Column)})",
				"AVG" => $"AVG({ColumnReference(measure.SourceAlias, measure.Column)})",
				"MIN" => $"MIN({ColumnReference(measure.SourceAlias, measure.Column)})",
				"MAX" => $"MAX({ColumnReference(measure.SourceAlias, measure.Column)})",
				"COUNT" => $"COUNT({ColumnReference(measure.SourceAlias, measure.Column)})",
				"COUNT_DISTINCT" =>
					$"COUNT(DISTINCT {ColumnReference(measure.SourceAlias, measure.Column)})",

				_ => throw new InvalidOperationException(
					$"Unsupported aggregation '{measure.Aggregation}'.")
			};

			selectParts.Add(
				$"{expression} AS {QuoteIdentifier(measure.Key)}");
		}

		var sql = new StringBuilder();

		sql.AppendLine("SELECT");
		sql.AppendLine("    " + string.Join(",\n    ", selectParts));

		// FROM and automatically planned joins
		sql.AppendLine("FROM " + GetTableReference(joinPlan.Tables[0]));

		var joinedAliases = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase)
		{
			joinPlan.Tables[0].Alias
		};

		var remainingJoins = joinPlan.Joins.ToList();

		while (remainingJoins.Count > 0)
		{
			var nextJoin = remainingJoins.FirstOrDefault(join =>
				joinedAliases.Contains(join.LeftAlias) &&
				!joinedAliases.Contains(join.RightAlias));

			var reverseJoin = false;

			if (nextJoin == null)
			{
				nextJoin = remainingJoins.FirstOrDefault(join =>
					joinedAliases.Contains(join.RightAlias) &&
					!joinedAliases.Contains(join.LeftAlias));

				reverseJoin = nextJoin != null;
			}

			if (nextJoin == null)
			{
				throw new InvalidOperationException(
					"The join plan is disconnected or contains an invalid join order.");
			}

			var newAlias = reverseJoin
				? nextJoin.LeftAlias
				: nextJoin.RightAlias;

			var existingAlias = reverseJoin
				? nextJoin.RightAlias
				: nextJoin.LeftAlias;

			var newTable = joinPlan.Tables.FirstOrDefault(t =>
				string.Equals(
					t.Alias,
					newAlias,
					StringComparison.OrdinalIgnoreCase));

			if (newTable == null)
			{
				throw new InvalidOperationException(
					$"Join table alias '{newAlias}' was not found.");
			}

			if (nextJoin.Columns.Count == 0)
			{
				throw new InvalidOperationException(
					$"Join '{nextJoin.ConstraintName}' has no column mappings.");
			}

			var conditions = nextJoin.Columns.Select(column =>
			{
				var existingColumn = reverseJoin
					? column.RightColumn
					: column.LeftColumn;

				var newColumn = reverseJoin
					? column.LeftColumn
					: column.RightColumn;

				return
					$"{ColumnReference(existingAlias, existingColumn)} = " +
					$"{ColumnReference(newAlias, newColumn)}";
			});

			sql.AppendLine(
				$"INNER JOIN {GetTableReference(newTable)} ON " +
				string.Join(" AND ", conditions));

			joinedAliases.Add(newAlias);
			remainingJoins.Remove(nextJoin);
		}

		// Filters
		var parameters = new Dictionary<string, object?>(
			StringComparer.OrdinalIgnoreCase);

		var whereParts = new List<string>();
		var parameterIndex = 0;

		foreach (var filter in query.Filters)
		{
			ValidateSourceColumn(
				joinPlan,
				schema,
				filter.SourceAlias,
				filter.Column);

			var column = ColumnReference(
				filter.SourceAlias,
				filter.Column);

			var operation = filter.Operator.Trim().ToLowerInvariant();

			switch (operation)
			{
				case "isnull":
					whereParts.Add($"{column} IS NULL");
					break;

				case "isnotnull":
					whereParts.Add($"{column} IS NOT NULL");
					break;

				case "eq":
				case "neq":
				case "gt":
				case "gte":
				case "lt":
				case "lte":
					{
						var parameter = $"@p{parameterIndex++}";
						var sqlOperator = operation switch
						{
							"eq" => "=",
							"neq" => "<>",
							"gt" => ">",
							"gte" => ">=",
							"lt" => "<",
							"lte" => "<=",
							_ => throw new InvalidOperationException()
						};

						parameters[parameter] = filter.Value;

						whereParts.Add(
							$"{column} {sqlOperator} {parameter}");

						break;
					}

				case "contains":
				case "startswith":
				case "endswith":
					{
						var parameter = $"@p{parameterIndex++}";
						var value = EscapeLikeValue(filter.Value ?? "");

						var pattern = operation switch
						{
							"contains" => $"%{value}%",
							"startswith" => $"{value}%",
							"endswith" => $"%{value}",
							_ => value
						};

						parameters[parameter] = pattern;

						whereParts.Add(
							$"{column} LIKE {parameter}");

						break;
					}

				case "in":
					{
						var values = (filter.Value ?? "")
							.Split(',', StringSplitOptions.TrimEntries |
										 StringSplitOptions.RemoveEmptyEntries);

						if (values.Length == 0)
						{
							throw new InvalidOperationException(
								"An IN filter must contain at least one value.");
						}

						var valueParameters = new List<string>();

						foreach (var value in values)
						{
							var parameter = $"@p{parameterIndex++}";
							parameters[parameter] = value;
							valueParameters.Add(parameter);
						}

						whereParts.Add(
							$"{column} IN ({string.Join(", ", valueParameters)})");

						break;
					}

				default:
					throw new InvalidOperationException(
						$"Unsupported filter operator '{filter.Operator}'.");
			}
		}

		if (whereParts.Count > 0)
		{
			sql.AppendLine("WHERE " + string.Join(" AND ", whereParts));
		}

		// GROUP BY
		if (query.Dimensions.Count > 0)
		{
			var groupBy = query.Dimensions.Select(d =>
				ColumnReference(d.SourceAlias, d.Column));

			sql.AppendLine("GROUP BY " + string.Join(", ", groupBy));
		}

		// ORDER BY
		var sortParts = new List<string>();

		foreach (var sort in query.Sort)
		{
			if (!outputKeys.Contains(sort.Field))
			{
				throw new InvalidOperationException(
					$"Sort field '{sort.Field}' is not a selected output field.");
			}

			var direction = sort.Direction.Trim().ToUpperInvariant();

			if (direction != "ASC" && direction != "DESC")
			{
				throw new InvalidOperationException(
					$"Invalid sort direction '{sort.Direction}'.");
			}

			sortParts.Add(
				$"{QuoteIdentifier(sort.Field)} {direction}");
		}

		// SQL Server requires ORDER BY when using OFFSET/FETCH.
		if (sortParts.Count == 0)
		{
			if (query.Dimensions.Count > 0)
			{
				sortParts.AddRange(
					query.Dimensions.Select(d =>
						$"{QuoteIdentifier(d.Key)} ASC"));
			}
			else
			{
				sortParts.Add("(SELECT NULL)");
			}
		}

		sql.AppendLine("ORDER BY " + string.Join(", ", sortParts));

		// Pagination
		var offset = checked((query.Page - 1) * query.PageSize);

		parameters["@offset"] = offset;
		parameters["@pageSize"] = query.PageSize;

		sql.AppendLine("OFFSET @offset ROWS");
		sql.AppendLine("FETCH NEXT @pageSize ROWS ONLY;");

		return new GeneratedAggregationSql
		{
			Sql = sql.ToString(),
			Parameters = parameters
		};
	}

	private static void ValidateJoinPlan(
		AutomaticJoinPlan joinPlan,
		DatabaseSchema schema)
	{
		if (joinPlan.Tables.Count == 0)
		{
			throw new InvalidOperationException(
				"The join plan contains no tables.");
		}

		var aliases = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);

		foreach (var table in joinPlan.Tables)
		{
			if (string.IsNullOrWhiteSpace(table.Alias) ||
				!aliases.Add(table.Alias))
			{
				throw new InvalidOperationException(
					"The join plan contains an empty or duplicate alias.");
			}

			FindTable(schema, table.Schema, table.Table);
		}

		if (joinPlan.Joins.Count != joinPlan.Tables.Count - 1)
		{
			throw new InvalidOperationException(
				"The join plan must form a connected tree.");
		}

		foreach (var join in joinPlan.Joins)
		{
			if (!aliases.Contains(join.LeftAlias) ||
				!aliases.Contains(join.RightAlias))
			{
				throw new InvalidOperationException(
					$"Join '{join.ConstraintName}' references an unknown alias.");
			}

			if (string.Equals(
				join.LeftAlias,
				join.RightAlias,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					"A join cannot connect an alias to itself.");
			}

			foreach (var column in join.Columns)
			{
				ValidateSourceColumn(
					joinPlan,
					schema,
					join.LeftAlias,
					column.LeftColumn);

				ValidateSourceColumn(
					joinPlan,
					schema,
					join.RightAlias,
					column.RightColumn);
			}
		}
	}

	private static void ValidateSourceColumn(
		AutomaticJoinPlan joinPlan,
		DatabaseSchema schema,
		string alias,
		string columnName)
	{
		var table = joinPlan.Tables.FirstOrDefault(t =>
			string.Equals(
				t.Alias,
				alias,
				StringComparison.OrdinalIgnoreCase));

		if (table == null)
		{
			throw new InvalidOperationException(
				$"Unknown table alias '{alias}'.");
		}

		var discoveredTable = FindTable(
			schema,
			table.Schema,
			table.Table);

		var columnExists = discoveredTable.Columns.Any(c =>
			string.Equals(
				c.ColumnName,
				columnName,
				StringComparison.OrdinalIgnoreCase));

		if (!columnExists)
		{
			throw new InvalidOperationException(
				$"Column '{columnName}' does not exist in " +
				$"'{table.Schema}.{table.Table}'.");
		}
	}

	private static DatabaseTable FindTable(
		DatabaseSchema schema,
		string schemaName,
		string tableName)
	{
		return schema.Tables.FirstOrDefault(t =>
			string.Equals(
				t.SchemaName,
				schemaName,
				StringComparison.OrdinalIgnoreCase) &&
			string.Equals(
				t.TableName,
				tableName,
				StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException(
				$"Table '{schemaName}.{tableName}' was not found in the discovered schema.");
	}

	private static string GetTableReference(AggregationTable table)
	{
		return $"{QuoteIdentifier(table.Schema)}." +
			   $"{QuoteIdentifier(table.Table)} " +
			   $"AS {QuoteIdentifier(table.Alias)}";
	}

	private static string ColumnReference(
		string alias,
		string column)
	{
		return $"{QuoteIdentifier(alias)}.{QuoteIdentifier(column)}";
	}

	private static string QuoteIdentifier(string identifier)
	{
		if (string.IsNullOrWhiteSpace(identifier))
		{
			throw new InvalidOperationException(
				"SQL identifiers cannot be empty.");
		}

		return "[" + identifier.Replace("]", "]]") + "]";
	}

	private static string EscapeLikeValue(string value)
	{
		return value
			.Replace("[", "[[]")
			.Replace("%", "[%]")
			.Replace("_", "[_]");
	}
}