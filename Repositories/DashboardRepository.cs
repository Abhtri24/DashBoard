using Dapper;

namespace DashBoard.Repositories;

public sealed class DashboardRepository
{
	private readonly SqlConnectionFactory _db;

	public DashboardRepository(SqlConnectionFactory db)
	{
		_db = db;
	}

	public async Task<IEnumerable<dynamic>> GetDashboardsAsync(
		string department,
		CancellationToken cancellationToken)
	{
		await using var connection = await _db.OpenAsync();

		const string sql = """
            SELECT
                DashboardKey,
                DisplayName,
                Description
            FROM dash.Dashboard
            WHERE DepartmentKey = @Department
              AND IsActive = 1
            ORDER BY IsDefault DESC, DisplayName;
            """;

		return await connection.QueryAsync(
			new CommandDefinition(
				sql,
				new { Department = department },
				cancellationToken: cancellationToken));
	}
}