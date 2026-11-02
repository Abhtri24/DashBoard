
namespace DashBoard.Services;

public sealed class DatabaseRelationshipGraph
{
	public List<DatabaseTable> Tables { get; set; } = [];
	public List<DatabaseRelationship> Relationships { get; set; } = [];
}

public sealed class DatabaseRelationship
{
	public string ConstraintName { get; set; } = "";

	// FK side (child table)
	public string FromSchema { get; set; } = "";
	public string FromTable { get; set; } = "";

	// Referenced side (parent table)
	public string ToSchema { get; set; } = "";
	public string ToTable { get; set; } = "";

	public List<DatabaseRelationshipColumn> Columns { get; set; } = [];

	public bool IsEnabled { get; set; }
	public bool IsTrusted { get; set; }

	public bool IsFromSideUnique { get; set; }
	public bool IsToSideUnique { get; set; }

	// Cardinality when traversing From -> To.
	public string FromToCardinality { get; set; } = "Unknown";

	// Cardinality when traversing To -> From.
	public string ToFromCardinality { get; set; } = "Unknown";

	// Only trusted, enabled relationships are eligible
	// for automatic join planning by default.
	public bool CanUseForAutomaticJoins =>
	IsEnabled && IsTrusted;
}

public sealed class DatabaseRelationshipColumn
{
	public string FromColumn { get; set; } = "";
	public string ToColumn { get; set; } = "";
	public int KeyPosition { get; set; }
}

public sealed class DatabaseRelationshipService
{
	private readonly DatabaseSchemaDiscoveryService _discovery;

	public DatabaseRelationshipService(
		DatabaseSchemaDiscoveryService discovery)
	{
		_discovery = discovery;
	}

	public async Task<DatabaseRelationshipGraph> BuildGraphAsync(
		string? schemaName = null,
		CancellationToken cancellationToken = default)
	{
		var schema = await _discovery.DiscoverAsync(
			schemaName,
			cancellationToken);

		var relationships = schema.ForeignKeys
			.Select(fk =>
			{
				var fromTable = FindTable(
					schema.Tables,
					fk.ParentSchema,
					fk.ParentTable);

				var toTable = FindTable(
					schema.Tables,
					fk.ReferencedSchema,
					fk.ReferencedTable);

				var columns = fk.Columns
					.OrderBy(c => c.KeyPosition)
					.Select(c => new DatabaseRelationshipColumn
					{
						FromColumn = c.ParentColumn,
						ToColumn = c.ReferencedColumn,
						KeyPosition = c.KeyPosition
					})
					.ToList();

				var fromColumns = columns
					.Select(c => c.FromColumn)
					.ToList();

				var toColumns = columns
					.Select(c => c.ToColumn)
					.ToList();

				var fromUnique = fromTable != null &&
					HasExactUniqueKey(fromTable, fromColumns);

				var toUnique = toTable != null &&
					HasExactUniqueKey(toTable, toColumns);

				var forwardCardinality =
					GetCardinality(fromUnique, toUnique);

				var reverseCardinality =
					GetCardinality(toUnique, fromUnique);

				return new DatabaseRelationship
				{
					ConstraintName = fk.ConstraintName,

					FromSchema = fk.ParentSchema,
					FromTable = fk.ParentTable,

					ToSchema = fk.ReferencedSchema,
					ToTable = fk.ReferencedTable,

					Columns = columns,

					IsEnabled = !fk.IsDisabled,
					IsTrusted = !fk.IsNotTrusted,

					IsFromSideUnique = fromUnique,
					IsToSideUnique = toUnique,

					FromToCardinality = forwardCardinality,
					ToFromCardinality = reverseCardinality
				};
			})
			.ToList();

		return new DatabaseRelationshipGraph
		{
			Tables = schema.Tables,
			Relationships = relationships
		};
	}

	private static DatabaseTable? FindTable(
		List<DatabaseTable> tables,
		string schema,
		string table)
	{
		return tables.FirstOrDefault(t =>
			string.Equals(
				t.SchemaName,
				schema,
				StringComparison.OrdinalIgnoreCase) &&
			string.Equals(
				t.TableName,
				table,
				StringComparison.OrdinalIgnoreCase));
	}

	private static bool HasExactUniqueKey(
		DatabaseTable table,
		List<string> columns)
	{
		if (columns.Count == 0)
			return false;

		var requested = new HashSet<string>(
			columns,
			StringComparer.OrdinalIgnoreCase);

		// Primary key
		var primaryKeyColumns = table.PrimaryKeys
			.OrderBy(k => k.KeyPosition)
			.Select(k => k.ColumnName)
			.ToList();

		if (HasExactColumnSet(primaryKeyColumns, requested))
			return true;

		// Additional unique constraints and indexes
		foreach (var uniqueKey in table.UniqueKeys)
		{
			var uniqueColumns = uniqueKey.Columns
				.OrderBy(c => c.KeyPosition)
				.Select(c => c.ColumnName)
				.ToList();

			if (HasExactColumnSet(uniqueColumns, requested))
				return true;
		}

		return false;
	}

	private static bool HasExactColumnSet(
		List<string> keyColumns,
		HashSet<string> requestedColumns)
	{
		if (keyColumns.Count != requestedColumns.Count)
			return false;

		return keyColumns.All(requestedColumns.Contains);
	}

	private static string GetCardinality(
		bool fromSideUnique,
		bool toSideUnique)
	{
		if (fromSideUnique && toSideUnique)
			return "OneToOne";

		if (fromSideUnique && !toSideUnique)
			return "OneToMany";

		if (!fromSideUnique && toSideUnique)
			return "ManyToOne";

		return "Unknown";
	}

	public List<List<DatabaseRelationship>> FindJoinPaths(
		DatabaseRelationshipGraph graph,
		string fromSchema,
		string fromTable,
		string toSchema,
		string toTable,
		int maxDepth = 6)
	{
		if (maxDepth < 1)
			throw new ArgumentOutOfRangeException(nameof(maxDepth));

		var paths = new List<List<DatabaseRelationship>>();

		var visited = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase)
		{
			GetTableKey(fromSchema, fromTable)
		};

		Search(
			graph,
			fromSchema,
			fromTable,
			toSchema,
			toTable,
			maxDepth,
			visited,
			[],
			paths);

		return paths;
	}


	public List<List<DatabaseRelationship>> FindAutomaticJoinPaths(
		DatabaseRelationshipGraph graph,
		string fromSchema,
		string fromTable,
		string toSchema,
		string toTable,
		int maxDepth = 6)
	{
		var eligibleGraph = new DatabaseRelationshipGraph
		{
			Tables = graph.Tables,
			Relationships = graph.Relationships
				.Where(r => r.CanUseForAutomaticJoins)
				.ToList()
		};

		return FindJoinPaths(
			eligibleGraph,
			fromSchema,
			fromTable,
			toSchema,
			toTable,
			maxDepth);
	}


	private static void Search(
		DatabaseRelationshipGraph graph,
		string currentSchema,
		string currentTable,
		string targetSchema,
		string targetTable,
		int remainingDepth,
		HashSet<string> visited,
		List<DatabaseRelationship> currentPath,
		List<List<DatabaseRelationship>> paths)
	{
		if (string.Equals(
				currentSchema,
				targetSchema,
				StringComparison.OrdinalIgnoreCase) &&
			string.Equals(
				currentTable,
				targetTable,
				StringComparison.OrdinalIgnoreCase))
		{
			paths.Add([.. currentPath]);
			return;
		}

		if (remainingDepth == 0)
			return;

		var currentKey = GetTableKey(
			currentSchema,
			currentTable);

		foreach (var relationship in graph.Relationships)
		{
			string nextSchema;
			string nextTable;

			if (string.Equals(
					GetTableKey(
						relationship.FromSchema,
						relationship.FromTable),
					currentKey,
					StringComparison.OrdinalIgnoreCase))
			{
				nextSchema = relationship.ToSchema;
				nextTable = relationship.ToTable;
			}
			else if (string.Equals(
						 GetTableKey(
							 relationship.ToSchema,
							 relationship.ToTable),
						 currentKey,
						 StringComparison.OrdinalIgnoreCase))
			{
				nextSchema = relationship.FromSchema;
				nextTable = relationship.FromTable;
			}
			else
			{
				continue;
			}

			var nextKey = GetTableKey(nextSchema, nextTable);

			if (!visited.Add(nextKey))
				continue;

			currentPath.Add(relationship);

			Search(
				graph,
				nextSchema,
				nextTable,
				targetSchema,
				targetTable,
				remainingDepth - 1,
				visited,
				currentPath,
				paths);

			currentPath.RemoveAt(currentPath.Count - 1);
			visited.Remove(nextKey);
		}
	}

	private static string GetTableKey(
		string schema,
		string table)
		=> $"{schema}.{table}";
}
