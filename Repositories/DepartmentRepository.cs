using Dapper;
using DashBoard;

namespace DashBoard.Repositories;

public class DepartmentRepository
{
    private readonly SqlConnectionFactory _db;

    public DepartmentRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<bool> HasDepartmentAccessAsync(
        int userId,
        string department,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();

        const string sql = """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM org.UserDepartment ud
                INNER JOIN dash.Department d
                    ON d.DepartmentKey = ud.DepartmentKey
                WHERE ud.UserId = @UserId
                  AND ud.DepartmentKey = @Department
                  AND d.IsActive = 1
            ) THEN 1 ELSE 0 END;
            """;

        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                new { UserId = userId, Department = department },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> HasDepartmentAdminAccessAsync(
        int userId,
        string department,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();

        const string sql = """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM org.UserDepartment ud
                INNER JOIN dash.Department d
                    ON d.DepartmentKey = ud.DepartmentKey
                WHERE ud.UserId = @UserId
                  AND ud.DepartmentKey = @Department
                  AND ud.DepartmentRole = N'Admin'
                  AND d.IsActive = 1
            ) THEN 1 ELSE 0 END;
            """;

        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                new { UserId = userId, Department = department },
                cancellationToken: cancellationToken));
    }
}
