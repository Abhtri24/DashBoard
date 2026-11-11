using Dapper;
using System.Data;

namespace DashBoard.Services;

public sealed class DatasetMetadataProvider
{
    private const string EnabledTablesSql = """
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

    public async Task<List<DatasetTableMetadata>> LoadEnabledTablesAsync(
        IDbConnection connection,
        string datasetKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(datasetKey))
            throw new ArgumentException("Dataset is required.", nameof(datasetKey));

        var rows = await connection.QueryAsync<DatasetTableMetadata>(
            new CommandDefinition(
                EnabledTablesSql,
                new { DatasetKey = datasetKey },
                cancellationToken: cancellationToken));

        return rows.ToList();
    }
}

public sealed class DatasetTableMetadata
{
    public int DatasetTableId { get; set; }
    public string SchemaName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string? TableAlias { get; set; }
}