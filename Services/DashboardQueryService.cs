
using Dapper;
using DashBoard.Catalog;
using DashBoard.Models;

namespace DashBoard.Services
{
	public class DashboardQueryService
	{
		private readonly SqlConnectionFactory _db;
		private readonly CatalogService _catalog;

		public DashboardQueryService(
			SqlConnectionFactory db,
			CatalogService catalog)
		{
			_db = db;
			_catalog = catalog;
		}

		private static string GetSqlOperator(string op)
		{
			return op?.ToLowerInvariant() switch
			{
				"eq" => "=",
				"neq" => "<>",
				"gt" => ">",
				"gte" => ">=",
				"lt" => "<",
				"lte" => "<=",
				_ => throw new ArgumentException(
					$"Unsupported filter operator: {op}")
			};
		}

		public async Task<IEnumerable<dynamic>> QueryAsync(
			DashQueryRequest request)
		{
			// 1. Validate the request.
			ArgumentNullException.ThrowIfNull(request);

			if (string.IsNullOrWhiteSpace(request.Dataset))
				throw new ArgumentException("Dataset is required.");

			if (request.Dimensions == null || request.Measures == null)
				throw new ArgumentException(
					"Dimensions and measures cannot be null.");

			if (request.Filters == null)
				throw new ArgumentException("Filters cannot be null.");

			if (!request.Dimensions.Any() && !request.Measures.Any())
				throw new ArgumentException(
					"At least one dimension or measure is required.");

			if (request.Page < 1)
				throw new ArgumentException("Page must be at least 1.");

			if (request.PageSize < 1 || request.PageSize > 200)
				throw new ArgumentException(
					"Page size must be between 1 and 200.");

			if (request.Sort == null)
				throw new ArgumentException("Sort cannot be null.");

			// 2. Resolve the dataset from the catalog.
			var dataset = _catalog.Get(request.Dataset);

			// 3. Resolve requested dimensions and measures.
			var dimensions = request.Dimensions
				.Select(key => dataset.Dimension(key))
				.ToList();

			var measures = request.Measures
				.Select(key => dataset.Measure(key))
				.ToList();

			// 4. Build SELECT.
			var selectParts = new List<string>();

			selectParts.AddRange(
				dimensions.Select(d => $"{d.Column} AS [{d.Key}]"));

			selectParts.AddRange(
				measures.Select(m => $"{m.Sql} AS [{m.Key}]"));

			var selectClause = string.Join(", ", selectParts);

			// 5. Build FROM and GROUP BY.
			var fromClause = $"FROM {dataset.Source}";

			var groupByClause = dimensions.Any()
				? "GROUP BY " +
				  string.Join(", ", dimensions.Select(d => d.Column))
				: string.Empty;

			// 6. Build WHERE conditions and parameters.
			var whereParts = new List<string>();
			var parameters = new DynamicParameters();
			var parameterIndex = 0;

			foreach (var filter in request.Filters)
			{
				var dimension = dataset.Dimension(filter.Dimension);
				var sqlOperator = GetSqlOperator(filter.Operator);

				if (filter.Value == null)
				{
					throw new ArgumentException(
						$"Filter value is required for '{filter.Dimension}'.");
				}

				var paramName = $"p{parameterIndex++}";

				whereParts.Add(
					$"{dimension.Column} {sqlOperator} @{paramName}");

				parameters.Add(paramName, filter.Value);
			}

			var whereClause = whereParts.Any()
				? "WHERE " + string.Join(" AND ", whereParts)
				: string.Empty;

			// 7. Build ORDER BY using only selected fields.
			var selectedFields = dimensions.Select(d => d.Key)
				.Concat(measures.Select(m => m.Key))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			var sortParts = new List<string>();

			foreach (var sort in request.Sort)
			{
				if (!selectedFields.Contains(sort.Field))
					throw new ArgumentException(
						$"Cannot sort by unselected field '{sort.Field}'.");

				var direction = sort.Direction.ToLowerInvariant() switch
				{
					"asc" => "ASC",
					"desc" => "DESC",
					_ => throw new ArgumentException(
						$"Unsupported sort direction: {sort.Direction}")
				};

				sortParts.Add($"[{sort.Field}] {direction}");
			}

			// SQL Server requires ORDER BY for OFFSET/FETCH.
			if (!sortParts.Any())
			{
				var defaultField = dimensions.FirstOrDefault()?.Key
					?? measures.First().Key;

				sortParts.Add($"[{defaultField}] ASC");
			}

			var orderByClause = "ORDER BY " + string.Join(", ", sortParts);

			// 8. Calculate pagination.
			var offset = (request.Page - 1) * request.PageSize;

			parameters.Add("offset", offset);
			parameters.Add("pageSize", request.PageSize);

			// 9. Assemble the SQL.
			var sql = $"""
    SELECT {selectClause}
    {fromClause}
    {whereClause}
    {groupByClause}
    {orderByClause}
    OFFSET @offset ROWS
    FETCH NEXT @pageSize ROWS ONLY
    """;

			// 10. Execute the query.
			await using var connection = await _db.OpenAsync();

			var results = await connection.QueryAsync(sql, parameters);

			return results;
		}
	}
}
