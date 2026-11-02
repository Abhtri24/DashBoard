
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class AggregationSafetyResult
{
	public bool IsSafe { get; set; }

	public List<string> Warnings { get; set; } = [];

	public List<string> FanOutRelationships { get; set; } = [];

	public List<MeasureSafetyResult> MeasureResults { get; set; } = [];
}

public sealed class MeasureSafetyResult
{
	public string MeasureKey { get; set; } = "";

	public bool IsSafe { get; set; }

	public List<string> Warnings { get; set; } = [];
}

public sealed class DatabaseAggregationSafetyService
{
	private sealed record JoinEdge(
		string LeftAlias,
		string RightAlias,
		DatabaseRelationship Relationship);

	public AggregationSafetyResult Analyze(
		DatabaseRelationshipGraph graph,
		AggregationQueryPlan plan)
	{
		var result = new AggregationSafetyResult();

		if (plan.Tables.Count == 0)
		{
			AddGlobalWarning(result, "The query contains no tables.");
			return result;
		}

		var aliases = new Dictionary<string, AggregationTable>(
			StringComparer.OrdinalIgnoreCase);

		foreach (var table in plan.Tables)
		{
			if (string.IsNullOrWhiteSpace(table.Alias) ||
				!aliases.TryAdd(table.Alias, table))
			{
				AddGlobalWarning(
					result,
					$"Invalid or duplicate table alias: '{table.Alias}'.");
			}
		}

		if (result.Warnings.Count > 0)
			return result;

		var edges = new List<JoinEdge>();

		foreach (var join in plan.Joins)
		{
			if (!aliases.TryGetValue(join.LeftAlias, out var left) ||
				!aliases.TryGetValue(join.RightAlias, out var right))
			{
				AddGlobalWarning(
					result,
					$"Join references an unknown alias: " +
					$"{join.LeftAlias} -> {join.RightAlias}.");

				continue;
			}

			var matches = graph.Relationships
				.Where(r => string.Equals(
					r.ConstraintName,
					join.ConstraintName,
					StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (matches.Count != 1)
			{
				AddGlobalWarning(
					result,
					$"Relationship '{join.ConstraintName}' " +
					"could not be resolved uniquely.");

				continue;
			}

			var relationship = matches[0];

			if (!relationship.CanUseForAutomaticJoins)
			{
				AddGlobalWarning(
					result,
					$"Relationship '{join.ConstraintName}' " +
					"is disabled or untrusted.");

				continue;
			}

			var validDirection =
				Matches(left, relationship.FromSchema, relationship.FromTable) &&
				Matches(right, relationship.ToSchema, relationship.ToTable)
				||
				Matches(left, relationship.ToSchema, relationship.ToTable) &&
				Matches(right, relationship.FromSchema, relationship.FromTable);

			if (!validDirection)
			{
				AddGlobalWarning(
					result,
					$"Aliases '{join.LeftAlias}' and '{join.RightAlias}' " +
					$"do not match relationship '{join.ConstraintName}'.");

				continue;
			}

			edges.Add(new JoinEdge(
				join.LeftAlias,
				join.RightAlias,
				relationship));
		}

		if (result.Warnings.Count > 0)
			return result;

		if (edges.Count != plan.Tables.Count - 1)
		{
			AddGlobalWarning(
				result,
				"The selected joins must form a connected tree. " +
				"Disconnected or cyclic join plans require additional analysis.");

			return result;
		}

		if (!IsConnected(plan.Tables, edges))
		{
			AddGlobalWarning(
				result,
				"Not all selected tables are connected through the selected joins.");

			return result;
		}

		foreach (var measure in plan.Measures)
		{
			var measureResult = AnalyzeMeasure(
				measure,
				aliases,
				edges);

			result.MeasureResults.Add(measureResult);

			foreach (var warning in measureResult.Warnings)
			{
				result.Warnings.Add(
					$"Measure '{measure.Key}': {warning}");
			}
		}

		result.FanOutRelationships = edges
			.Where(e =>
				e.Relationship.FromToCardinality == "OneToMany" ||
				e.Relationship.ToFromCardinality == "OneToMany")
			.Select(e => e.Relationship.ConstraintName)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		result.IsSafe =
			result.Warnings.Count == 0 &&
			result.MeasureResults.All(m => m.IsSafe);

		return result;
	}

	private static MeasureSafetyResult AnalyzeMeasure(
		AggregationMeasure measure,
		Dictionary<string, AggregationTable> aliases,
		List<JoinEdge> edges)
	{
		var result = new MeasureSafetyResult
		{
			MeasureKey = measure.Key,
			IsSafe = true
		};

		if (!aliases.ContainsKey(measure.SourceAlias))
		{
			AddMeasureWarning(
				result,
				$"Source alias '{measure.SourceAlias}' does not exist.");

			return result;
		}

		var aggregation = measure.Aggregation.Trim().ToUpperInvariant();

		var fanOutSensitive = aggregation is
			"SUM" or "COUNT" or "AVG";

		var supported = aggregation is
			"SUM" or "COUNT" or "AVG" or
			"MIN" or "MAX" or "COUNT_DISTINCT";

		if (!supported)
		{
			AddMeasureWarning(
				result,
				$"Unsupported aggregation function '{measure.Aggregation}'.");

			return result;
		}

		if (!fanOutSensitive)
			return result;

		var sourceAlias = measure.SourceAlias;

		var visited = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase)
		{
			sourceAlias
		};

		var queue = new Queue<string>();
		queue.Enqueue(sourceAlias);

		while (queue.Count > 0)
		{
			var currentAlias = queue.Dequeue();

			foreach (var edge in edges)
			{
				string nextAlias;

				if (string.Equals(
					edge.LeftAlias,
					currentAlias,
					StringComparison.OrdinalIgnoreCase))
				{
					nextAlias = edge.RightAlias;
				}
				else if (string.Equals(
					edge.RightAlias,
					currentAlias,
					StringComparison.OrdinalIgnoreCase))
				{
					nextAlias = edge.LeftAlias;
				}
				else
				{
					continue;
				}

				if (!visited.Add(nextAlias))
					continue;

				var cardinality = GetCardinality(
					edge,
					currentAlias,
					nextAlias,
					aliases);

				if (cardinality == "OneToMany")
				{
					AddMeasureWarning(
						result,
						$"Join '{edge.Relationship.ConstraintName}' " +
						"can multiply rows originating from this measure. " +
						"Pre-aggregation or another query strategy is required.");
				}
				else if (cardinality == "Unknown")
				{
					AddMeasureWarning(
						result,
						$"Cardinality of relationship " +
						$"'{edge.Relationship.ConstraintName}' is unknown.");
				}

				queue.Enqueue(nextAlias);
			}
		}

		return result;
	}

	private static string GetCardinality(
		JoinEdge edge,
		string fromAlias,
		string toAlias,
		Dictionary<string, AggregationTable> aliases)
	{
		var fromTable = aliases[fromAlias];

		if (Matches(
			fromTable,
			edge.Relationship.FromSchema,
			edge.Relationship.FromTable))
		{
			return edge.Relationship.FromToCardinality;
		}

		return edge.Relationship.ToFromCardinality;
	}

	private static AggregationTable GetTableForAlias(
		JoinEdge edge,
		string alias)
	{
		// The caller's alias direction is resolved in AnalyzeMeasure.
		// This method is replaced by the alias-aware lookup below.
		throw new NotSupportedException();
	}

	private static bool IsConnected(
		List<AggregationTable> tables,
		List<JoinEdge> edges)
	{
		var visited = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);

		var queue = new Queue<string>();
		queue.Enqueue(tables[0].Alias);
		visited.Add(tables[0].Alias);

		while (queue.Count > 0)
		{
			var current = queue.Dequeue();

			foreach (var edge in edges)
			{
				var next = string.Equals(
					edge.LeftAlias,
					current,
					StringComparison.OrdinalIgnoreCase)
					? edge.RightAlias
					: string.Equals(
						edge.RightAlias,
						current,
						StringComparison.OrdinalIgnoreCase)
						? edge.LeftAlias
						: null;

				if (next != null && visited.Add(next))
					queue.Enqueue(next);
			}
		}

		return visited.Count == tables.Count;
	}

	private static bool Matches(
		AggregationTable table,
		string schema,
		string name)
	{
		return string.Equals(
				   table.Schema,
				   schema,
				   StringComparison.OrdinalIgnoreCase) &&
			   string.Equals(
				   table.Table,
				   name,
				   StringComparison.OrdinalIgnoreCase);
	}

	private static void AddGlobalWarning(
		AggregationSafetyResult result,
		string warning)
	{
		result.Warnings.Add(warning);
		result.IsSafe = false;
	}

	private static void AddMeasureWarning(
		MeasureSafetyResult result,
		string warning)
	{
		result.Warnings.Add(warning);
		result.IsSafe = false;
	}
}
