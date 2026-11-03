
using Dapper;
using DashBoard;

namespace DashBoard.Services;

public sealed class DatabaseRelationshipGraph
{
	public List<DatabaseTable> Tables { get; set; } = [];
	public List<DatabaseRelationship> Relationships { get; set; } = [];
}

public sealed class DatabaseRelationship
{
	public string ConstraintName { get; set; } = "";
	public string RelationshipSource { get; set; } = "FOREIGN_KEY";

	public string FromSchema { get; set; } = "";
	public string FromTable { get; set; } = "";

	public string ToSchema { get; set; } = "";
	public string ToTable { get; set; } = "";

	public List<DatabaseRelationshipColumn> Columns { get; set; } = [];

	public bool IsEnabled { get; set; }
	public bool IsTrusted { get; set; }

	public bool IsFromSideUnique { get; set; }
	public bool IsToSideUnique { get; set; }

	public string FromToCardinality { get; set; } = "Unknown";
	public string ToFromCardinality { get; set; } = "Unknown";

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
	private readonly SqlConnectionFactory _db;

	public DatabaseRelationshipService(
		DatabaseSchemaDiscoveryService discovery,
		SqlConnectionFactory db)
	{
		_discovery = discovery;
		_db = db;
	}

	private sealed class DatasetRelationshipRow
	{
		public int RelationshipId { get; set; }
		public string FromColumn { get; set; } = "";
		public string ToColumn { get; set; } = "";
		public string Cardinality { get; set; } = "";
		public string FromSchema { get; set; } = "";
		public string FromTable { get; set; } = "";
		public string ToSchema { get; set; } = "";
		public string ToTable { get; set; } = "";
	}

	public async Task<DatabaseRelationshipGraph> BuildGraphAsync(
		string? schemaName = null,
		CancellationToken cancellationToken = default,
		string? datasetKey = null)
	{
		var schema = await _discovery.DiscoverAsync(
			schemaName,
			cancellationToken);

		var relationships = string.IsNullOrWhiteSpace(datasetKey)
			? schema.ForeignKeys
				.Select(fk =>
				{
					var fromTable = FindTable(
						schema.Tables, fk.ParentSchema, fk.ParentTable);

					var toTable = FindTable(
						schema.Tables, fk.ReferencedSchema, fk.ReferencedTable);

					var columns = fk.Columns
						.OrderBy(c => c.KeyPosition)
						.Select(c => new DatabaseRelationshipColumn
						{
							FromColumn = c.ParentColumn,
							ToColumn = c.ReferencedColumn,
							KeyPosition = c.KeyPosition
						})
						.ToList();

					var fromUnique = fromTable != null &&
						HasExactUniqueKey(
							fromTable,
							columns.Select(c => c.FromColumn).ToList());

					var toUnique = toTable != null &&
						HasExactUniqueKey(
							toTable,
							columns.Select(c => c.ToColumn).ToList());

					return new DatabaseRelationship
					{
						ConstraintName = fk.ConstraintName,
						RelationshipSource = "FOREIGN_KEY",
						FromSchema = fk.ParentSchema,
						FromTable = fk.ParentTable,
						ToSchema = fk.ReferencedSchema,
						ToTable = fk.ReferencedTable,
						Columns = columns,
						IsEnabled = !fk.IsDisabled,
						IsTrusted = !fk.IsNotTrusted,
						IsFromSideUnique = fromUnique,
						IsToSideUnique = toUnique,
						FromToCardinality = GetCardinality(fromUnique, toUnique),
						ToFromCardinality = GetCardinality(toUnique, fromUnique)
					};
				})
				.ToList()
			: [];

		if (!string.IsNullOrWhiteSpace(datasetKey))
		{
			await using var connection = await _db.OpenAsync();

			const string sql = """
                SELECT
                    dr.RelationshipId,
                    dr.FromColumn,
                    dr.ToColumn,
                    dr.Cardinality,
                    ft.SchemaName AS FromSchema,
                    ft.ObjectName AS FromTable,
                    tt.SchemaName AS ToSchema,
                    tt.ObjectName AS ToTable
                FROM dash.DatasetRelationship dr
                INNER JOIN dash.DatasetTable ft
                    ON ft.DatasetTableId = dr.FromDatasetTableId
                INNER JOIN dash.DatasetTable tt
                    ON tt.DatasetTableId = dr.ToDatasetTableId
                WHERE dr.DatasetKey = @DatasetKey
                  AND dr.IsApproved = 1
                  AND dr.IsEnabled = 1
                  AND ft.DatasetKey = dr.DatasetKey
                  AND tt.DatasetKey = dr.DatasetKey
                  AND ft.IsEnabled = 1
                  AND tt.IsEnabled = 1;
                """;

			var configuredRelationships =
				await connection.QueryAsync<DatasetRelationshipRow>(
					new CommandDefinition(
						sql,
						new { DatasetKey = datasetKey },
						cancellationToken: cancellationToken));

			foreach (var item in configuredRelationships)
			{
				var cardinality = NormalizeCardinality(
					item.Cardinality);

				if (cardinality == "Unknown")
					continue;

				var fromTable = FindTable(
					schema.Tables,
					item.FromSchema,
					item.FromTable);

				var toTable = FindTable(
					schema.Tables,
					item.ToSchema,
					item.ToTable);

				if (fromTable == null || toTable == null)
					continue;

				// Validate both configured columns against
				// the discovered physical database schema.
				var fromColumnExists = fromTable.Columns.Any(c =>
					string.Equals(
						c.ColumnName,
						item.FromColumn,
						StringComparison.OrdinalIgnoreCase));

				var toColumnExists = toTable.Columns.Any(c =>
					string.Equals(
						c.ColumnName,
						item.ToColumn,
						StringComparison.OrdinalIgnoreCase));

				if (!fromColumnExists || !toColumnExists)
					continue;

				var relationship = new DatabaseRelationship
				{
					ConstraintName = $"DATASET:{item.RelationshipId}",
					RelationshipSource = "DATASET",

					FromSchema = item.FromSchema,
					FromTable = item.FromTable,
					ToSchema = item.ToSchema,
					ToTable = item.ToTable,

					Columns =
					[
						new DatabaseRelationshipColumn
						{
							FromColumn = item.FromColumn,
							ToColumn = item.ToColumn,
							KeyPosition = 1
						}
					],

					IsEnabled = true,
					IsTrusted = true,

					IsFromSideUnique = cardinality is
						"OneToOne",

					IsToSideUnique = cardinality is
						"OneToOne" or "ManyToOne",

					FromToCardinality = cardinality,
					ToFromCardinality = ReverseCardinality(cardinality)
				};

				// Physical foreign keys take precedence when
				// the same relationship is already discovered.
				var alreadyExists = relationships.Any(existing =>
					SameRelationship(existing, relationship));

				if (!alreadyExists)
					relationships.Add(relationship);
			}
		}

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
			Matches(
				t.SchemaName,
				t.TableName,
				schema,
				table));
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

		var primaryKeyColumns = table.PrimaryKeys
			.OrderBy(k => k.KeyPosition)
			.Select(k => k.ColumnName)
			.ToList();

		if (HasExactColumnSet(primaryKeyColumns, requested))
			return true;

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

	private static string NormalizeCardinality(string cardinality)
	{
		return cardinality.Trim().ToUpperInvariant() switch
		{
			"ONE_TO_ONE" => "OneToOne",
			"ONE_TO_MANY" => "OneToMany",
			"MANY_TO_ONE" => "ManyToOne",
			"MANY_TO_MANY" => "ManyToMany",
			_ => "Unknown"
		};
	}

	private static string ReverseCardinality(string cardinality)
	{
		return cardinality switch
		{
			"OneToOne" => "OneToOne",
			"OneToMany" => "ManyToOne",
			"ManyToOne" => "OneToMany",
			"ManyToMany" => "ManyToMany",
			_ => "Unknown"
		};
	}

	private static bool SameRelationship(
		DatabaseRelationship a,
		DatabaseRelationship b)
	{
		if (Matches(
			a.FromSchema,
			a.FromTable,
			b.FromSchema,
			b.FromTable)
			&&
			Matches(
				a.ToSchema,
				a.ToTable,
				b.ToSchema,
				b.ToTable)
			&&
			SameColumns(a.Columns, b.Columns))
		{
			return true;
		}

		// Also detect the same relationship in reverse direction.
		return Matches(
			a.FromSchema,
			a.FromTable,
			b.ToSchema,
			b.ToTable)
			&&
			Matches(
				a.ToSchema,
				a.ToTable,
				b.FromSchema,
				b.FromTable)
			&&
			SameColumnsReversed(a.Columns, b.Columns);
	}

	private static bool SameColumns(
		List<DatabaseRelationshipColumn> a,
		List<DatabaseRelationshipColumn> b)
	{
		if (a.Count != b.Count)
			return false;

		return a.All(ac => b.Any(bc =>
			string.Equals(
				ac.FromColumn,
				bc.FromColumn,
				StringComparison.OrdinalIgnoreCase)
			&&
			string.Equals(
				ac.ToColumn,
				bc.ToColumn,
				StringComparison.OrdinalIgnoreCase)));
	}

	private static bool SameColumnsReversed(
		List<DatabaseRelationshipColumn> a,
		List<DatabaseRelationshipColumn> b)
	{
		if (a.Count != b.Count)
			return false;

		return a.All(ac => b.Any(bc =>
			string.Equals(
				ac.FromColumn,
				bc.ToColumn,
				StringComparison.OrdinalIgnoreCase)
			&&
			string.Equals(
				ac.ToColumn,
				bc.FromColumn,
				StringComparison.OrdinalIgnoreCase)));
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
				   StringComparison.OrdinalIgnoreCase)
			   &&
			   string.Equals(
				   table1,
				   table2,
				   StringComparison.OrdinalIgnoreCase);
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
		if (Matches(
			currentSchema,
			currentTable,
			targetSchema,
			targetTable))
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
