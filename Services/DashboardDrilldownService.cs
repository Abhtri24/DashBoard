using Dapper;
using DashBoard.Catalog;
using DashBoard.Models;

namespace DashBoard.Services;

public sealed class DashboardDrilldownService
{
    private const int MaxPageSize = 200;

    private readonly SqlConnectionFactory _db;
    private readonly CatalogService _catalog;

    public DashboardDrilldownService(SqlConnectionFactory db, CatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    private static string GetSqlOperator(string op) =>
        op?.ToLowerInvariant() switch
        {
            "eq" => "=",
            "neq" => "<>",
            "gt" => ">",
            "gte" => ">=",
            "lt" => "<",
            "lte" => "<=",
            _ => throw new ArgumentException($"Unsupported filter operator: {op}")
        };

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
        if (request.PageSize < 1 || request.PageSize > MaxPageSize)
            throw new ArgumentException($"Page size must be between 1 and {MaxPageSize}.");

        var dataset = await _catalog.GetAsync(request.Dataset);
        var selectedDimension = dataset.Dimension(request.Dimension);

        // Return the catalog-approved dataset fields, not arbitrary client-supplied SQL.
        var columns = dataset.Dimensions
            .Select(d => new { d.Key, d.Label, Sql = $"{d.Column} AS [{d.Key}]" })
            .Concat(dataset.Measures.Select(m => new
            {
                m.Key,
                m.Label,
                Sql = $"{m.SourceColumn} AS [{m.Key}]"
            }))
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (columns.Count == 0)
            throw new ArgumentException("No catalog columns are available for this dataset.");

        var whereParts = new List<string>();
        var parameters = new DynamicParameters();
        var parameterIndex = 0;

        AddFilter(selectedDimension.Key, "eq", request.Value, selectedDimension.Column,
            whereParts, parameters, ref parameterIndex);

        foreach (var filter in request.Filters)
        {
            if (filter is null)
                throw new ArgumentException("A filter cannot be empty.");

            var dimension = dataset.Dimension(filter.Dimension);
            AddFilter(dimension.Key, filter.Operator, filter.Value, dimension.Column,
                whereParts, parameters, ref parameterIndex);
        }

        parameters.Add("take", request.PageSize + 1);
        var sql = $"""
            SELECT TOP (@take) {string.Join(", ", columns.Select(c => c.Sql))}
            FROM {dataset.Source}
            WHERE {string.Join(" AND ", whereParts)}
            ORDER BY {selectedDimension.Column} ASC
            """;

        await using var connection = await _db.OpenAsync();
        var fetched = (await connection.QueryAsync(sql, parameters)).ToList();
        var hasMore = fetched.Count > request.PageSize;
        var rows = fetched.Take(request.PageSize).ToList();

        return new
        {
            columns = columns.Select(c => new { c.Key, c.Label }),
            rows,
            hasMore
        };
    }

    private static void AddFilter(
        string dimensionKey,
        string op,
        string? value,
        string columnSql,
        List<string> whereParts,
        DynamicParameters parameters,
        ref int parameterIndex)
    {
        if (value is null)
        {
            var nullOperator = op?.ToLowerInvariant() switch
            {
                "eq" => "IS NULL",
                "neq" => "IS NOT NULL",
                _ => throw new ArgumentException(
                    $"Null is only supported with equals/does-not-equal for '{dimensionKey}'.")
            };
            whereParts.Add($"{columnSql} {nullOperator}");
            return;
        }

        var sqlOperator = GetSqlOperator(op);
        var parameterName = $"p{parameterIndex++}";
        whereParts.Add($"{columnSql} {sqlOperator} @{parameterName}");
        parameters.Add(parameterName, value);
    }
}
