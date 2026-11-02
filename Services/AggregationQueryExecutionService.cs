using Dapper;
using DashBoard;

namespace DashBoard.Services;

public sealed class AggregationQueryExecutionService
{
	private const int CommandTimeoutSeconds = 30;
	private const int MaximumRows = 500;

	private readonly SqlConnectionFactory _connectionFactory;

	public AggregationQueryExecutionService(
		SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	public async Task<IEnumerable<dynamic>> ExecuteAsync(
		GeneratedAggregationSql generated,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(generated);

		if (string.IsNullOrWhiteSpace(generated.Sql))
		{
			throw new ArgumentException(
				"Generated SQL cannot be empty.");
		}

		if (generated.Parameters.TryGetValue(
			"@pageSize",
			out var pageSizeValue))
		{
			if (pageSizeValue is not int pageSize ||
				pageSize < 1 ||
				pageSize > MaximumRows)
			{
				throw new ArgumentException(
					$"Page size must be between 1 and {MaximumRows}.");
			}
		}
		else
		{
			throw new ArgumentException(
				"The generated query must include a page size.");
		}

		await using var connection =
			await _connectionFactory.OpenAsync();

		var command = new CommandDefinition(
			commandText: generated.Sql,
			parameters: generated.Parameters,
			commandTimeout: CommandTimeoutSeconds,
			cancellationToken: cancellationToken);

		return await connection.QueryAsync(command);
	}
}