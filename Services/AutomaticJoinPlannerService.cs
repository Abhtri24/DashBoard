
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class AutomaticJoinPlannerService
{
	private sealed record PathCandidate(
		AggregationTable Target,
		string ConnectedKey,
		List<DatabaseRelationship> Path);

	private readonly DatabaseRelationshipService _relationships;

	public AutomaticJoinPlannerService(
		DatabaseRelationshipService relationships)
	{
		_relationships = relationships;
	}

	public AutomaticJoinPlan Plan(
		DatabaseRelationshipGraph graph,
		List<AggregationTable> selectedTables)
	{
		var result = new AutomaticJoinPlan();

		if (selectedTables.Count == 0)
		{
			result.Errors.Add("Select at least one table.");
			return result;
		}

		var selectedByTable = new Dictionary<string, AggregationTable>(
			StringComparer.OrdinalIgnoreCase);

		var aliases = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);

		// Validate selected tables and aliases.
		foreach (var table in selectedTables)
		{
			if (string.IsNullOrWhiteSpace(table.Alias) ||
				string.IsNullOrWhiteSpace(table.Schema) ||
				string.IsNullOrWhiteSpace(table.Table))
			{
				result.Errors.Add(
					"Every selected table must have a schema, table name and alias.");

				continue;
			}

			if (!aliases.Add(table.Alias))
			{
				result.Errors.Add(
					$"Duplicate table alias '{table.Alias}'.");

				continue;
			}

			var key = GetTableKey(table.Schema, table.Table);

			if (selectedByTable.ContainsKey(key))
			{
				result.Errors.Add(
					$"Table '{key}' was selected more than once. " +
					"Self-joins are not supported yet.");

				continue;
			}

			var exists = graph.Tables.Any(t =>
				Matches(
					t.SchemaName,
					t.TableName,
					table.Schema,
					table.Table));

			if (!exists)
			{
				result.Errors.Add(
					$"Table '{key}' does not exist in the discovered schema.");

				continue;
			}

			selectedByTable.Add(key, table);
		}

		if (result.Errors.Count > 0)
			return result;

		// Start with the first selected table.
		var firstTable = CloneTable(selectedTables[0]);

		result.Tables.Add(firstTable);

		var includedTables = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase)
		{
			GetTableKey(firstTable.Schema, firstTable.Table)
		};

		var aliasCounter = 1;

		// Connect every remaining selected table.
		while (selectedTables.Any(t =>
			!includedTables.Contains(
				GetTableKey(t.Schema, t.Table))))
		{
			var candidates = new List<PathCandidate>();

			foreach (var target in selectedTables.Where(t =>
				!includedTables.Contains(
					GetTableKey(t.Schema, t.Table))))
			{
				foreach (var connectedKey in includedTables)
				{
					var parts = connectedKey.Split('.', 2);

					var paths = _relationships.FindAutomaticJoinPaths(
						graph,
						target.Schema,
						target.Table,
						parts[0],
						parts[1]);

					foreach (var path in paths.Where(p => p.Count > 0))
					{
						if (!IsValidPath(
							path,
							target.Schema,
							target.Table,
							connectedKey,
							includedTables))
						{
							continue;
						}

						candidates.Add(new PathCandidate(
							target,
							connectedKey,
							path));
					}
				}
			}

			if (candidates.Count == 0)
			{
				result.Errors.Add(
					"No valid foreign-key path connects the remaining " +
					"selected tables to the existing join plan.");

				return result;
			}

			// Prefer the shortest valid path.
			var shortestLength = candidates.Min(c => c.Path.Count);

			var shortestPaths = candidates
				.Where(c => c.Path.Count == shortestLength)
				.ToList();

			// Ambiguity only exists when the same target and
			// connected destination have multiple equally short paths.
			var ambiguousPath = shortestPaths
				.GroupBy(c => new
				{
					TargetKey = GetTableKey(
						c.Target.Schema,
						c.Target.Table),

					c.ConnectedKey
				})
				.FirstOrDefault(group => group.Count() > 1);

			if (ambiguousPath != null)
			{
				result.Errors.Add(
					$"Multiple equally short paths connect " +
					$"'{ambiguousPath.Key.TargetKey}' to " +
					$"'{ambiguousPath.Key.ConnectedKey}'. " +
					"An explicit relationship choice is required.");

				return result;
			}

			// Different target tables may legitimately have paths
			// of the same length. Connect one at a time, then
			// recalculate paths using the expanded join plan.
			var chosen = shortestPaths
				.OrderBy(c => selectedTables.FindIndex(t =>
					Matches(
						t.Schema,
						t.Table,
						c.Target.Schema,
						c.Target.Table)))
				.ThenBy(c => c.ConnectedKey, StringComparer.OrdinalIgnoreCase)
				.First();

			var currentSchema = chosen.Target.Schema;
			var currentTable = chosen.Target.Table;

			var currentAlias = EnsureTableIncluded(
				result,
				includedTables,
				aliases,
				CloneTable(chosen.Target),
				ref aliasCounter);

			foreach (var relationship in chosen.Path)
			{
				var currentIsFromSide = Matches(
					currentSchema,
					currentTable,
					relationship.FromSchema,
					relationship.FromTable);

				var currentIsToSide = Matches(
					currentSchema,
					currentTable,
					relationship.ToSchema,
					relationship.ToTable);

				if (!currentIsFromSide && !currentIsToSide)
				{
					result.Errors.Add(
						$"Join path is inconsistent at relationship " +
						$"'{relationship.ConstraintName}'.");

					return result;
				}

				var nextSchema = currentIsFromSide
					? relationship.ToSchema
					: relationship.FromSchema;

				var nextTable = currentIsFromSide
					? relationship.ToTable
					: relationship.FromTable;

				var nextKey = GetTableKey(nextSchema, nextTable);

				var nextDefinition = selectedByTable.TryGetValue(
					nextKey,
					out var selectedDefinition)
					? CloneTable(selectedDefinition)
					: new AggregationTable
					{
						Schema = nextSchema,
						Table = nextTable
					};

				var nextAlias = EnsureTableIncluded(
					result,
					includedTables,
					aliases,
					nextDefinition,
					ref aliasCounter);

				// Preserve the actual discovered FK column mappings.
				var columns = relationship.Columns
					.OrderBy(c => c.KeyPosition)
					.Select(c => new PlannedJoinColumn
					{
						LeftColumn = currentIsFromSide
							? c.FromColumn
							: c.ToColumn,

						RightColumn = currentIsFromSide
							? c.ToColumn
							: c.FromColumn
					})
					.ToList();

				result.Joins.Add(new PlannedJoin
				{
					LeftAlias = currentAlias,
					RightAlias = nextAlias,
					ConstraintName = relationship.ConstraintName,
					Columns = columns
				});

				currentSchema = nextSchema;
				currentTable = nextTable;
				currentAlias = nextAlias;
			}

			if (!includedTables.Contains(chosen.ConnectedKey))
			{
				result.Errors.Add(
					"The selected join path did not reach its expected destination.");

				return result;
			}
		}

		result.IsValid = true;
		return result;
	}

	private static bool IsValidPath(
		List<DatabaseRelationship> path,
		string startSchema,
		string startTable,
		string expectedEndKey,
		HashSet<string> includedTables)
	{
		var currentSchema = startSchema;
		var currentTable = startTable;

		for (var i = 0; i < path.Count; i++)
		{
			var relationship = path[i];

			var isFromSide = Matches(
				currentSchema,
				currentTable,
				relationship.FromSchema,
				relationship.FromTable);

			var isToSide = Matches(
				currentSchema,
				currentTable,
				relationship.ToSchema,
				relationship.ToTable);

			if (!isFromSide && !isToSide)
				return false;

			currentSchema = isFromSide
				? relationship.ToSchema
				: relationship.FromSchema;

			currentTable = isFromSide
				? relationship.ToTable
				: relationship.FromTable;

			var nextKey = GetTableKey(currentSchema, currentTable);

			if (includedTables.Contains(nextKey))
			{
				// An existing table may only be the final destination.
				return i == path.Count - 1 &&
					   string.Equals(
						   nextKey,
						   expectedEndKey,
						   StringComparison.OrdinalIgnoreCase);
			}
		}

		return string.Equals(
			GetTableKey(currentSchema, currentTable),
			expectedEndKey,
			StringComparison.OrdinalIgnoreCase);
	}

	private static string EnsureTableIncluded(
		AutomaticJoinPlan plan,
		HashSet<string> includedTables,
		HashSet<string> aliases,
		AggregationTable table,
		ref int aliasCounter)
	{
		var key = GetTableKey(table.Schema, table.Table);

		if (includedTables.Contains(key))
		{
			return plan.Tables.First(t =>
				string.Equals(
					GetTableKey(t.Schema, t.Table),
					key,
					StringComparison.OrdinalIgnoreCase)).Alias;
		}

		if (string.IsNullOrWhiteSpace(table.Alias))
		{
			string generatedAlias;

			do
			{
				generatedAlias = $"__join{aliasCounter++}";
			}
			while (!aliases.Add(generatedAlias));

			table.Alias = generatedAlias;
		}
		else if (!aliases.Contains(table.Alias))
		{
			aliases.Add(table.Alias);
		}

		includedTables.Add(key);
		plan.Tables.Add(table);

		return table.Alias;
	}

	private static AggregationTable CloneTable(
		AggregationTable table)
	{
		return new AggregationTable
		{
			Alias = table.Alias,
			Schema = table.Schema,
			Table = table.Table
		};
	}

	private static bool Matches(
		string schema1,
		string table1,
		string schema2,
		string table2)
	{
		return string.Equals(
				   schema1,
				   schema2,
				   StringComparison.OrdinalIgnoreCase) &&
			   string.Equals(
				   table1,
				   table2,
				   StringComparison.OrdinalIgnoreCase);
	}

	private static string GetTableKey(
		string schema,
		string table)
		=> $"{schema}.{table}";
}
