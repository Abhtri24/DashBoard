using Dapper;
using DashBoard;

namespace DashBoard.Repositories;

public sealed class DashboardLayoutRepository
{
    private readonly SqlConnectionFactory _db;

    public DashboardLayoutRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<dynamic?> GetUserLayoutAsync(
        int userId, string dashboardKey, string department, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string sql = """
            SELECT LayoutJson, UpdatedAt
            FROM dash.UserDashboardLayout
            WHERE UserId = @UserId
              AND DashboardKey = @DashboardKey
              AND DepartmentKey = @DepartmentKey;
            """;

        return await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
            sql,
            new { UserId = userId, DashboardKey = dashboardKey, DepartmentKey = department },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> SaveUserLayoutAsync(
        int userId, string dashboardKey, string department, string layoutJson, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string dashboardSql = """
            SELECT COUNT(1)
            FROM dash.Dashboard
            WHERE DashboardKey = @DashboardKey
              AND DepartmentKey = @DepartmentKey
              AND IsActive = 1;
            """;

        var validDashboard = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            dashboardSql,
            new { DashboardKey = dashboardKey, DepartmentKey = department },
            cancellationToken: cancellationToken));
        if (validDashboard == 0)
            return false;

        const string upsertSql = """
            UPDATE dash.UserDashboardLayout
            SET LayoutJson = @LayoutJson, UpdatedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId
              AND DashboardKey = @DashboardKey
              AND DepartmentKey = @DepartmentKey;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dash.UserDashboardLayout
                    (UserId, DashboardKey, DepartmentKey, LayoutJson)
                VALUES
                    (@UserId, @DashboardKey, @DepartmentKey, @LayoutJson);
            END;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            upsertSql,
            new { UserId = userId, DashboardKey = dashboardKey, DepartmentKey = department, LayoutJson = layoutJson },
            cancellationToken: cancellationToken));
        return true;
    }

    public async Task<dynamic?> GetDepartmentLayoutAsync(
        string dashboardKey, string department, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string sql = """
            SELECT LayoutJson, PublishedByUserId, PublishedAt, UpdatedAt
            FROM dash.DepartmentDashboardLayout
            WHERE DepartmentKey = @DepartmentKey
              AND DashboardKey = @DashboardKey;
            """;

        return await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
            sql,
            new { DepartmentKey = department, DashboardKey = dashboardKey },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> PublishDepartmentLayoutAsync(
        int userId, string dashboardKey, string department, string layoutJson, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string dashboardSql = """
            SELECT COUNT(1)
            FROM dash.Dashboard
            WHERE DashboardKey = @DashboardKey
              AND DepartmentKey = @DepartmentKey
              AND IsActive = 1;
            """;

        var validDashboard = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            dashboardSql,
            new { DashboardKey = dashboardKey, DepartmentKey = department },
            cancellationToken: cancellationToken));
        if (validDashboard == 0)
            return false;

        const string upsertSql = """
            UPDATE dash.DepartmentDashboardLayout
            SET
                LayoutJson = @LayoutJson,
                PublishedByUserId = @UserId,
                PublishedAt = SYSUTCDATETIME(),
                UpdatedAt = SYSUTCDATETIME()
            WHERE DepartmentKey = @DepartmentKey
              AND DashboardKey = @DashboardKey;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dash.DepartmentDashboardLayout
                    (DepartmentKey, DashboardKey, LayoutJson, PublishedByUserId)
                VALUES
                    (@DepartmentKey, @DashboardKey, @LayoutJson, @UserId);
            END;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            upsertSql,
            new { UserId = userId, DepartmentKey = department, DashboardKey = dashboardKey, LayoutJson = layoutJson },
            cancellationToken: cancellationToken));
        return true;
    }

    public async Task DeleteDepartmentLayoutAsync(
        string dashboardKey, string department, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string sql = """
            DELETE FROM dash.DepartmentDashboardLayout
            WHERE DepartmentKey = @DepartmentKey
              AND DashboardKey = @DashboardKey;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { DepartmentKey = department, DashboardKey = dashboardKey },
            cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<dynamic>> GetDashboardPresetsAsync(
        string dashboardKey, string department, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string sql = """
            SELECT PresetId, PresetName, Description, LayoutJson, CreatedAt, UpdatedAt
            FROM dash.DashboardLayoutPreset
            WHERE DepartmentKey = @DepartmentKey
              AND DashboardKey = @DashboardKey
              AND IsActive = 1
            ORDER BY PresetName;
            """;

        return await connection.QueryAsync(new CommandDefinition(
            sql,
            new { DepartmentKey = department, DashboardKey = dashboardKey },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> DashboardExistsAsync(
        string dashboardKey, string department, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string sql = """
            SELECT COUNT(1)
            FROM dash.Dashboard
            WHERE DashboardKey = @DashboardKey
              AND DepartmentKey = @DepartmentKey
              AND IsActive = 1;
            """;

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new { DashboardKey = dashboardKey, DepartmentKey = department },
            cancellationToken: cancellationToken)) > 0;
    }

    public async Task<int> CreateDashboardPresetAsync(
        string dashboardKey, string department, string presetName, string? description,
        string layoutJson, int userId, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string sql = """
            INSERT INTO dash.DashboardLayoutPreset
                (DepartmentKey, DashboardKey, PresetName, Description, LayoutJson, CreatedByUserId)
            OUTPUT INSERTED.PresetId
            VALUES
                (@DepartmentKey, @DashboardKey, @PresetName, @Description, @LayoutJson, @UserId);
            """;

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            DepartmentKey = department,
            DashboardKey = dashboardKey,
            PresetName = presetName,
            Description = description,
            LayoutJson = layoutJson,
            UserId = userId
        }, cancellationToken: cancellationToken));
    }

    public async Task<int> UpdateDashboardPresetAsync(
        string dashboardKey, int presetId, string department, string presetName,
        string? description, string layoutJson, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string sql = """
            UPDATE dash.DashboardLayoutPreset
            SET PresetName = @PresetName, Description = @Description,
                LayoutJson = @LayoutJson, UpdatedAt = SYSUTCDATETIME()
            WHERE PresetId = @PresetId
              AND DepartmentKey = @DepartmentKey
              AND DashboardKey = @DashboardKey
              AND IsActive = 1;
            """;

        return await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            PresetId = presetId,
            DepartmentKey = department,
            DashboardKey = dashboardKey,
            PresetName = presetName,
            Description = description,
            LayoutJson = layoutJson
        }, cancellationToken: cancellationToken));
    }

    public async Task<int> DeleteDashboardPresetAsync(
        string dashboardKey, int presetId, string department, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync();
        const string sql = """
            DELETE FROM dash.DashboardLayoutPreset
            WHERE PresetId = @PresetId
              AND DepartmentKey = @DepartmentKey
              AND DashboardKey = @DashboardKey;
            """;

        return await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            PresetId = presetId,
            DepartmentKey = department,
            DashboardKey = dashboardKey
        }, cancellationToken: cancellationToken));
    }
}

