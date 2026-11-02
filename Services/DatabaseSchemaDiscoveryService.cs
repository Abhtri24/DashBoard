
using Dapper;
using DashBoard;

namespace DashBoard.Services;

public sealed class DatabaseSchema
{
	public List<DatabaseTable> Tables { get; set; } = [];
	public List<DatabaseForeignKey> ForeignKeys { get; set; } = [];
}

public sealed class DatabaseTable
{
	public string SchemaName { get; set; } = "";
	public string TableName { get; set; } = "";

	public List<DatabaseColumn> Columns { get; set; } = [];
	public List<DatabasePrimaryKey> PrimaryKeys { get; set; } = [];
	public List<DatabaseUniqueKey> UniqueKeys { get; set; } = [];
}

public sealed class DatabaseColumn
{
	public int ColumnId { get; set; }
	public string ColumnName { get; set; } = "";
	public string DataType { get; set; } = "";
	public bool IsNullable { get; set; }
	public bool IsIdentity { get; set; }
}

public sealed class DatabasePrimaryKey
{
	public string ColumnName { get; set; } = "";
	public int KeyPosition { get; set; }
}

public sealed class DatabaseUniqueKey
{
	public string IndexName { get; set; } = "";
	public bool IsUniqueConstraint { get; set; }

	public List<DatabaseUniqueKeyColumn> Columns { get; set; } = [];
}

public sealed class DatabaseUniqueKeyColumn
{
	public string ColumnName { get; set; } = "";
	public int KeyPosition { get; set; }
}

public sealed class DatabaseForeignKey
{
	public string ConstraintName { get; set; } = "";

	public string ParentSchema { get; set; } = "";
	public string ParentTable { get; set; } = "";

	public string ReferencedSchema { get; set; } = "";
	public string ReferencedTable { get; set; } = "";

	public bool IsDisabled { get; set; }
	public bool IsNotTrusted { get; set; }

	public List<DatabaseForeignKeyColumn> Columns { get; set; } = [];
}

public sealed class DatabaseForeignKeyColumn
{
	public string ParentColumn { get; set; } = "";
	public string ReferencedColumn { get; set; } = "";
	public int KeyPosition { get; set; }
}

public sealed class DatabaseSchemaDiscoveryService
{
	private readonly SqlConnectionFactory _connectionFactory;

	public DatabaseSchemaDiscoveryService(
		SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	public async Task<DatabaseSchema> DiscoverAsync(
		string? schemaName = null,
		CancellationToken cancellationToken = default)
	{
		using var connection = await _connectionFactory.OpenAsync();

		var parameters = new { SchemaName = schemaName };

		const string tableSql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName
            FROM sys.tables t
            INNER JOIN sys.schemas s
                ON s.schema_id = t.schema_id
            WHERE (@SchemaName IS NULL OR s.name = @SchemaName)
            ORDER BY s.name, t.name;
            """;

		const string columnSql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                c.column_id AS ColumnId,
                c.name AS ColumnName,
                ty.name AS DataType,
                c.is_nullable AS IsNullable,
                c.is_identity AS IsIdentity
            FROM sys.tables t
            INNER JOIN sys.schemas s
                ON s.schema_id = t.schema_id
            INNER JOIN sys.columns c
                ON c.object_id = t.object_id
            INNER JOIN sys.types ty
                ON ty.user_type_id = c.user_type_id
            WHERE (@SchemaName IS NULL OR s.name = @SchemaName)
            ORDER BY s.name, t.name, c.column_id;
            """;

		const string primaryKeySql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                c.name AS ColumnName,
                ic.key_ordinal AS KeyPosition
            FROM sys.tables t
            INNER JOIN sys.schemas s
                ON s.schema_id = t.schema_id
            INNER JOIN sys.indexes i
                ON i.object_id = t.object_id
                AND i.is_primary_key = 1
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id
                AND ic.index_id = i.index_id
            INNER JOIN sys.columns c
                ON c.object_id = ic.object_id
                AND c.column_id = ic.column_id
            WHERE (@SchemaName IS NULL OR s.name = @SchemaName)
              AND ic.key_ordinal > 0
            ORDER BY s.name, t.name, ic.key_ordinal;
            """;

		const string uniqueKeySql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                i.name AS IndexName,
                i.is_unique_constraint AS IsUniqueConstraint,
                c.name AS ColumnName,
                ic.key_ordinal AS KeyPosition
            FROM sys.tables t
            INNER JOIN sys.schemas s
                ON s.schema_id = t.schema_id
            INNER JOIN sys.indexes i
                ON i.object_id = t.object_id
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id
                AND ic.index_id = i.index_id
            INNER JOIN sys.columns c
                ON c.object_id = t.object_id
                AND c.column_id = ic.column_id
            WHERE i.is_unique = 1
              AND i.is_primary_key = 0
              AND i.is_disabled = 0
              AND i.is_hypothetical = 0
              AND i.has_filter = 0
              AND ic.key_ordinal > 0
              AND (@SchemaName IS NULL OR s.name = @SchemaName)
            ORDER BY
                s.name,
                t.name,
                i.name,
                ic.key_ordinal;
            """;

		const string foreignKeySql = """
            SELECT
                fk.name AS ConstraintName,

                ps.name AS ParentSchema,
                pt.name AS ParentTable,
                pc.name AS ParentColumn,

                rs.name AS ReferencedSchema,
                rt.name AS ReferencedTable,
                rc.name AS ReferencedColumn,

                fk.is_disabled AS IsDisabled,
                fk.is_not_trusted AS IsNotTrusted,

                fkc.constraint_column_id AS KeyPosition

            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc
                ON fkc.constraint_object_id = fk.object_id

            INNER JOIN sys.tables pt
                ON pt.object_id = fk.parent_object_id
            INNER JOIN sys.schemas ps
                ON ps.schema_id = pt.schema_id
            INNER JOIN sys.columns pc
                ON pc.object_id = pt.object_id
                AND pc.column_id = fkc.parent_column_id

            INNER JOIN sys.tables rt
                ON rt.object_id = fk.referenced_object_id
            INNER JOIN sys.schemas rs
                ON rs.schema_id = rt.schema_id
            INNER JOIN sys.columns rc
                ON rc.object_id = rt.object_id
                AND rc.column_id = fkc.referenced_column_id

            WHERE (
                @SchemaName IS NULL
                OR ps.name = @SchemaName
                OR rs.name = @SchemaName
            )

            ORDER BY
                fk.name,
                fkc.constraint_column_id;
            """;

		var tables = (await connection.QueryAsync<DatabaseTable>(
			new CommandDefinition(
				tableSql,
				parameters,
				cancellationToken: cancellationToken)))
			.ToList();

		var columns = await connection.QueryAsync<DatabaseColumnRow>(
			new CommandDefinition(
				columnSql,
				parameters,
				cancellationToken: cancellationToken));

		var primaryKeys = await connection.QueryAsync<DatabasePrimaryKeyRow>(
			new CommandDefinition(
				primaryKeySql,
				parameters,
				cancellationToken: cancellationToken));

		var uniqueKeyRows = await connection.QueryAsync<DatabaseUniqueKeyRow>(
			new CommandDefinition(
				uniqueKeySql,
				parameters,
				cancellationToken: cancellationToken));

		var foreignKeyRows = await connection.QueryAsync<DatabaseForeignKeyRow>(
			new CommandDefinition(
				foreignKeySql,
				parameters,
				cancellationToken: cancellationToken));

		foreach (var table in tables)
		{
			table.Columns = columns
				.Where(c =>
					c.SchemaName == table.SchemaName &&
					c.TableName == table.TableName)
				.Select(c => new DatabaseColumn
				{
					ColumnId = c.ColumnId,
					ColumnName = c.ColumnName,
					DataType = c.DataType,
					IsNullable = c.IsNullable,
					IsIdentity = c.IsIdentity
				})
				.ToList();

			table.PrimaryKeys = primaryKeys
				.Where(k =>
					k.SchemaName == table.SchemaName &&
					k.TableName == table.TableName)
				.Select(k => new DatabasePrimaryKey
				{
					ColumnName = k.ColumnName,
					KeyPosition = k.KeyPosition
				})
				.ToList();

			table.UniqueKeys = uniqueKeyRows
				.Where(k =>
					k.SchemaName == table.SchemaName &&
					k.TableName == table.TableName)
				.GroupBy(k => new
				{
					k.IndexName,
					k.IsUniqueConstraint
				})
				.Select(group => new DatabaseUniqueKey
				{
					IndexName = group.Key.IndexName,
					IsUniqueConstraint = group.Key.IsUniqueConstraint,

					Columns = group
						.OrderBy(k => k.KeyPosition)
						.Select(k => new DatabaseUniqueKeyColumn
						{
							ColumnName = k.ColumnName,
							KeyPosition = k.KeyPosition
						})
						.ToList()
				})
				.ToList();
		}

		var foreignKeys = foreignKeyRows
			.GroupBy(f => new
			{
				f.ConstraintName,
				f.ParentSchema,
				f.ParentTable,
				f.ReferencedSchema,
				f.ReferencedTable,
				f.IsDisabled,
				f.IsNotTrusted
			})
			.Select(group => new DatabaseForeignKey
			{
				ConstraintName = group.Key.ConstraintName,

				ParentSchema = group.Key.ParentSchema,
				ParentTable = group.Key.ParentTable,

				ReferencedSchema = group.Key.ReferencedSchema,
				ReferencedTable = group.Key.ReferencedTable,

				IsDisabled = group.Key.IsDisabled,
				IsNotTrusted = group.Key.IsNotTrusted,

				Columns = group
					.OrderBy(f => f.KeyPosition)
					.Select(f => new DatabaseForeignKeyColumn
					{
						ParentColumn = f.ParentColumn,
						ReferencedColumn = f.ReferencedColumn,
						KeyPosition = f.KeyPosition
					})
					.ToList()
			})
			.ToList();

		return new DatabaseSchema
		{
			Tables = tables,
			ForeignKeys = foreignKeys
		};
	}

	private sealed class DatabaseColumnRow
	{
		public string SchemaName { get; set; } = "";
		public string TableName { get; set; } = "";
		public int ColumnId { get; set; }
		public string ColumnName { get; set; } = "";
		public string DataType { get; set; } = "";
		public bool IsNullable { get; set; }
		public bool IsIdentity { get; set; }
	}

	private sealed class DatabasePrimaryKeyRow
	{
		public string SchemaName { get; set; } = "";
		public string TableName { get; set; } = "";
		public string ColumnName { get; set; } = "";
		public int KeyPosition { get; set; }
	}

	private sealed class DatabaseUniqueKeyRow
	{
		public string SchemaName { get; set; } = "";
		public string TableName { get; set; } = "";
		public string IndexName { get; set; } = "";
		public bool IsUniqueConstraint { get; set; }
		public string ColumnName { get; set; } = "";
		public int KeyPosition { get; set; }
	}

	private sealed class DatabaseForeignKeyRow
	{
		public string ConstraintName { get; set; } = "";

		public string ParentSchema { get; set; } = "";
		public string ParentTable { get; set; } = "";
		public string ParentColumn { get; set; } = "";

		public string ReferencedSchema { get; set; } = "";
		public string ReferencedTable { get; set; } = "";
		public string ReferencedColumn { get; set; } = "";

		public bool IsDisabled { get; set; }
		public bool IsNotTrusted { get; set; }

		public int KeyPosition { get; set; }
	}
}
