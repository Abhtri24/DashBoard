using System.Text.Json;
using DashBoard.Repositories;

namespace DashBoard.Services;

public sealed class DashboardLayoutService
{
    private const int MaxLayoutLength = 1_000_000;

    private readonly DepartmentRepository _departments;
    private readonly DashboardLayoutRepository _layouts;

    public DashboardLayoutService(
        DepartmentRepository departments,
        DashboardLayoutRepository layouts)
    {
        _departments = departments;
        _layouts = layouts;
    }

    public async Task<DashboardLayoutResult> GetUserLayoutAsync(
        int? userId, string dashboardKey, string? department, CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAccessAsync(userId, department, cancellationToken);
        if (access is not null) return access;
        if (string.IsNullOrWhiteSpace(dashboardKey))
            return DashboardLayoutResult.BadRequest("Dashboard and department are required.");

        var layout = await _layouts.GetUserLayoutAsync(
            userId!.Value, dashboardKey, department!, cancellationToken);
        return DashboardLayoutResult.Ok(layout);
    }

    public async Task<DashboardLayoutResult> SaveUserLayoutAsync(
        int? userId, string dashboardKey, string? department, JsonElement layout,
        CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAccessAsync(userId, department, cancellationToken);
        if (access is not null) return access;
        if (string.IsNullOrWhiteSpace(dashboardKey))
            return DashboardLayoutResult.BadRequest("Dashboard and department are required.");

        var layoutJson = ValidateLayout(layout);
        if (layoutJson.Error is not null) return DashboardLayoutResult.BadRequest(layoutJson.Error);

        var saved = await _layouts.SaveUserLayoutAsync(
            userId!.Value, dashboardKey, department!, layoutJson.Json!, cancellationToken);
        return saved
            ? DashboardLayoutResult.Ok(new { saved = true })
            : DashboardLayoutResult.NotFound("Dashboard not found for this department.");
    }

    public async Task<DashboardLayoutResult> GetDepartmentLayoutAsync(
        int? userId, string dashboardKey, string? department, CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAccessAsync(userId, department, cancellationToken);
        if (access is not null) return access;
        if (string.IsNullOrWhiteSpace(dashboardKey))
            return DashboardLayoutResult.BadRequest("Dashboard is required.");

        var layout = await _layouts.GetDepartmentLayoutAsync(
            dashboardKey, department!, cancellationToken);
        return DashboardLayoutResult.Ok(layout);
    }

    public async Task<DashboardLayoutResult> PublishDepartmentLayoutAsync(
        int? userId, string dashboardKey, string? department, JsonElement layout,
        CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAdminAccessAsync(
            userId, department, "Only department admins can publish department layouts.", cancellationToken);
        if (access is not null) return access;
        if (string.IsNullOrWhiteSpace(dashboardKey))
            return DashboardLayoutResult.BadRequest("Dashboard is required.");

        var layoutJson = ValidateLayout(layout);
        if (layoutJson.Error is not null) return DashboardLayoutResult.BadRequest(layoutJson.Error);

        var published = await _layouts.PublishDepartmentLayoutAsync(
            userId!.Value, dashboardKey, department!, layoutJson.Json!, cancellationToken);
        return published
            ? DashboardLayoutResult.Ok(new { published = true })
            : DashboardLayoutResult.NotFound("Dashboard not found for this department.");
    }

    public async Task<DashboardLayoutResult> DeleteDepartmentLayoutAsync(
        int? userId, string dashboardKey, string? department, CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAdminAccessAsync(
            userId, department, "Only department admins can remove department layouts.", cancellationToken);
        if (access is not null) return access;
        if (string.IsNullOrWhiteSpace(dashboardKey))
            return DashboardLayoutResult.BadRequest("Dashboard is required.");

        await _layouts.DeleteDepartmentLayoutAsync(dashboardKey, department!, cancellationToken);
        return DashboardLayoutResult.Ok(new { deleted = true });
    }

    public async Task<DashboardLayoutResult> GetDashboardPresetsAsync(
        int? userId, string dashboardKey, string? department, CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAccessAsync(userId, department, cancellationToken);
        if (access is not null) return access;
        if (string.IsNullOrWhiteSpace(dashboardKey))
            return DashboardLayoutResult.BadRequest("Dashboard is required.");

        var presets = await _layouts.GetDashboardPresetsAsync(
            dashboardKey, department!, cancellationToken);
        return DashboardLayoutResult.Ok(presets);
    }

    public async Task<DashboardLayoutResult> CreateDashboardPresetAsync(
        int? userId, string dashboardKey, string? department, string? presetName,
        string? description, JsonElement layout, CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAdminAccessAsync(
            userId, department, "Only department admins can manage layout presets.", cancellationToken);
        if (access is not null) return access;

        if (string.IsNullOrWhiteSpace(dashboardKey) ||
            string.IsNullOrWhiteSpace(presetName) || presetName.Trim().Length > 100)
            return DashboardLayoutResult.BadRequest("A preset name of up to 100 characters is required.");

        var layoutJson = ValidateLayout(layout);
        if (layoutJson.Error is not null) return DashboardLayoutResult.BadRequest(layoutJson.Error);

        if (!await _layouts.DashboardExistsAsync(dashboardKey, department!, cancellationToken))
            return DashboardLayoutResult.NotFound("Dashboard not found for this department.");

        try
        {
            var id = await _layouts.CreateDashboardPresetAsync(
                dashboardKey, department!, presetName.Trim(),
                string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                layoutJson.Json!, userId!.Value, cancellationToken);
            return DashboardLayoutResult.Ok(new { presetId = id, saved = true });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return DashboardLayoutResult.Conflict(
                "A preset with that name already exists for this dashboard.");
        }
    }

    public async Task<DashboardLayoutResult> UpdateDashboardPresetAsync(
        int? userId, string dashboardKey, int presetId, string? department,
        string? presetName, string? description, JsonElement layout,
        CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAdminAccessAsync(
            userId, department, "Only department admins can manage layout presets.", cancellationToken);
        if (access is not null) return access;

        if (string.IsNullOrWhiteSpace(presetName) || presetName.Trim().Length > 100)
            return DashboardLayoutResult.BadRequest("A preset name of up to 100 characters is required.");

        var layoutJson = ValidateLayout(layout);
        if (layoutJson.Error is not null) return DashboardLayoutResult.BadRequest(layoutJson.Error);

        try
        {
            var changed = await _layouts.UpdateDashboardPresetAsync(
                dashboardKey, presetId, department!, presetName.Trim(),
                string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                layoutJson.Json!, cancellationToken);
            return changed == 0
                ? DashboardLayoutResult.NotFound("Preset not found.")
                : DashboardLayoutResult.Ok(new { saved = true });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return DashboardLayoutResult.Conflict(
                "A preset with that name already exists for this dashboard.");
        }
    }

    public async Task<DashboardLayoutResult> DeleteDashboardPresetAsync(
        int? userId, string dashboardKey, int presetId, string? department,
        CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAdminAccessAsync(
            userId, department, "Only department admins can manage layout presets.", cancellationToken);
        if (access is not null) return access;

        var deleted = await _layouts.DeleteDashboardPresetAsync(
            dashboardKey, presetId, department!, cancellationToken);
        return DashboardLayoutResult.Ok(new { deleted = deleted > 0 });
    }

    private async Task<DashboardLayoutResult?> CheckDepartmentAccessAsync(
        int? userId, string? department, CancellationToken cancellationToken)
    {
        if (!userId.HasValue)
            return DashboardLayoutResult.BadRequest("Select a demo user first.");
        if (string.IsNullOrWhiteSpace(department))
            return DashboardLayoutResult.BadRequest("Department is required.");
        if (!await _departments.HasDepartmentAccessAsync(userId.Value, department, cancellationToken))
            return DashboardLayoutResult.Forbidden("You do not have access to this department.");

        return null;
    }

    private async Task<DashboardLayoutResult?> CheckDepartmentAdminAccessAsync(
        int? userId, string? department, string adminError, CancellationToken cancellationToken)
    {
        var access = await CheckDepartmentAccessAsync(userId, department, cancellationToken);
        if (access is not null) return access;
        if (!await _departments.HasDepartmentAdminAccessAsync(userId!.Value, department!, cancellationToken))
            return DashboardLayoutResult.Forbidden(adminError);

        return null;
    }

    private static (string? Json, string? Error) ValidateLayout(JsonElement layout)
    {
        if (layout.ValueKind != JsonValueKind.Array)
            return (null, "Layout must be a JSON array of widgets.");

        var json = layout.GetRawText();
        return json.Length > MaxLayoutLength
            ? (null, "Layout is too large (maximum 1 MB).")
            : (json, null);
    }
}

public sealed record DashboardLayoutResult(int StatusCode, object? Value, string? Error)
{
    public static DashboardLayoutResult Ok(object? value) => new(200, value, null);
    public static DashboardLayoutResult BadRequest(string error) => new(400, null, error);
    public static DashboardLayoutResult Forbidden(string error) => new(403, null, error);
    public static DashboardLayoutResult NotFound(string error) => new(404, null, error);
    public static DashboardLayoutResult Conflict(string error) => new(409, null, error);
}
