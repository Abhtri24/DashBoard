
using System.Text.Json;
using Microsoft.Data.SqlClient;
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

	public sealed record Result(int StatusCode, object? Data)
	{
		public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

		public static Result Ok(object? data) => new(200, data);
		public static Result BadRequest(string error) =>
			new(400, new { error });
		public static Result Forbidden(string error) =>
			new(403, new { error });
		public static Result NotFound(string error) =>
			new(404, new { error });
		public static Result Conflict(string error) =>
			new(409, new { error });
	}

	private async Task<Result?> CheckAccessAsync(
		int? userId,
		string? department,
		CancellationToken cancellationToken)
	{
		if (!userId.HasValue)
			return Result.BadRequest("Select a demo user first.");

		if (string.IsNullOrWhiteSpace(department))
			return Result.BadRequest("Department is required.");

		if (!await _departments.HasDepartmentAccessAsync(
			userId.Value, department, cancellationToken))
		{
			return Result.Forbidden(
				"You do not have access to this department.");
		}

		return null;
	}

	private async Task<Result?> CheckAdminAccessAsync(
		int userId,
		string department,
		CancellationToken cancellationToken)
	{
		if (!await _departments.HasDepartmentAdminAccessAsync(
			userId, department, cancellationToken))
		{
			return Result.Forbidden(
				"Only department admins can manage layout presets.");
		}

		return null;
	}

	private static Result? ValidateLayout(JsonElement layout)
	{
		if (layout.ValueKind != JsonValueKind.Array)
			return Result.BadRequest(
				"Layout must be a JSON array of widgets.");

		if (layout.GetRawText().Length > MaxLayoutLength)
			return Result.BadRequest(
				"Layout is too large (maximum 1 MB).");

		return null;
	}

	private static string? NormalizeDescription(string? description) =>
		string.IsNullOrWhiteSpace(description)
			? null
			: description.Trim();

	public async Task<Result> GetUserLayoutAsync(
		int? userId,
		string dashboardKey,
		string department,
		CancellationToken cancellationToken)
	{
		if (!userId.HasValue)
			return Result.BadRequest("Select a demo user first.");

		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		if (string.IsNullOrWhiteSpace(department) ||
			string.IsNullOrWhiteSpace(dashboardKey))
		{
			return Result.BadRequest(
				"Dashboard and department are required.");
		}

		var layout = await _layouts.GetUserLayoutAsync(
			userId.Value, dashboardKey, department, cancellationToken);

		return Result.Ok(layout);
	}

	public async Task<Result> SaveUserLayoutAsync(
		int? userId,
		string dashboardKey,
		string department,
		JsonElement layout,
		CancellationToken cancellationToken)
	{
		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		if (string.IsNullOrWhiteSpace(department) ||
			string.IsNullOrWhiteSpace(dashboardKey))
		{
			return Result.BadRequest(
				"Dashboard and department are required.");
		}

		var validation = ValidateLayout(layout);
		if (validation is not null)
			return validation;

		var saved = await _layouts.SaveUserLayoutAsync(
			userId!.Value,
			dashboardKey,
			department,
			layout.GetRawText(),
			cancellationToken);

		if (!saved)
			return Result.NotFound(
				"Dashboard not found for this department.");

		return Result.Ok(new { saved = true });
	}

	public async Task<Result> GetDepartmentLayoutAsync(
		int? userId,
		string dashboardKey,
		string department,
		CancellationToken cancellationToken)
	{
		if (!userId.HasValue)
			return Result.BadRequest("Select a demo user first.");

		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		if (string.IsNullOrWhiteSpace(dashboardKey))
			return Result.BadRequest("Dashboard is required.");

		var layout = await _layouts.GetDepartmentLayoutAsync(
			dashboardKey, department, cancellationToken);

		return Result.Ok(layout);
	}

	public async Task<Result> PublishDepartmentLayoutAsync(
		int? userId,
		string dashboardKey,
		string department,
		JsonElement layout,
		CancellationToken cancellationToken)
	{
		if (!userId.HasValue)
			return Result.BadRequest("Select a demo user first.");

		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		if (!await _departments.HasDepartmentAdminAccessAsync(
			userId.Value, department, cancellationToken))
		{
			return Result.Forbidden(
				"Only department admins can publish department layouts.");
		}

		if (string.IsNullOrWhiteSpace(dashboardKey))
			return Result.BadRequest("Dashboard is required.");

		var validation = ValidateLayout(layout);
		if (validation is not null)
			return validation;

		var published = await _layouts.PublishDepartmentLayoutAsync(
			userId.Value,
			dashboardKey,
			department,
			layout.GetRawText(),
			cancellationToken);

		if (!published)
			return Result.NotFound(
				"Dashboard not found for this department.");

		return Result.Ok(new { published = true });
	}

	public async Task<Result> DeleteDepartmentLayoutAsync(
		int? userId,
		string dashboardKey,
		string department,
		CancellationToken cancellationToken)
	{
		if (!userId.HasValue)
			return Result.BadRequest("Select a demo user first.");

		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		if (!await _departments.HasDepartmentAdminAccessAsync(
			userId.Value, department, cancellationToken))
		{
			return Result.Forbidden(
				"Only department admins can remove department layouts.");
		}

		if (string.IsNullOrWhiteSpace(dashboardKey))
			return Result.BadRequest("Dashboard is required.");

		await _layouts.DeleteDepartmentLayoutAsync(
			dashboardKey, department, cancellationToken);

		return Result.Ok(new { deleted = true });
	}

	public async Task<Result> GetDashboardPresetsAsync(
		int? userId,
		string dashboardKey,
		string department,
		CancellationToken cancellationToken)
	{
		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		if (string.IsNullOrWhiteSpace(dashboardKey))
			return Result.BadRequest("Dashboard is required.");

		var presets = await _layouts.GetDashboardPresetsAsync(
			dashboardKey, department, cancellationToken);

		return Result.Ok(presets);
	}

	public async Task<Result> CreateDashboardPresetAsync(
		int? userId,
		string dashboardKey,
		string department,
		string presetName,
		string? description,
		JsonElement layout,
		CancellationToken cancellationToken)
	{
		if (!userId.HasValue)
			return Result.BadRequest("Select a demo user first.");

		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		var adminAccess = await CheckAdminAccessAsync(
			userId.Value, department, cancellationToken);

		if (adminAccess is not null)
			return adminAccess;

		if (string.IsNullOrWhiteSpace(dashboardKey) ||
			string.IsNullOrWhiteSpace(presetName) ||
			presetName.Trim().Length > 100)
		{
			return Result.BadRequest(
				"A preset name of up to 100 characters is required.");
		}

		var validation = ValidateLayout(layout);
		if (validation is not null)
			return validation;

		if (!await _layouts.DashboardExistsAsync(
			dashboardKey, department, cancellationToken))
		{
			return Result.NotFound(
				"Dashboard not found for this department.");
		}

		try
		{
			var id = await _layouts.CreateDashboardPresetAsync(
				dashboardKey,
				department,
				presetName.Trim(),
				NormalizeDescription(description),
				layout.GetRawText(),
				userId.Value,
				cancellationToken);

			return Result.Ok(new { presetId = id, saved = true });
		}
		catch (SqlException ex) when (ex.Number is 2601 or 2627)
		{
			return Result.Conflict(
				"A preset with that name already exists for this dashboard.");
		}
	}

	public async Task<Result> UpdateDashboardPresetAsync(
		int? userId,
		string dashboardKey,
		int presetId,
		string department,
		string presetName,
		string? description,
		JsonElement layout,
		CancellationToken cancellationToken)
	{
		if (!userId.HasValue)
			return Result.BadRequest("Select a demo user first.");

		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		var adminAccess = await CheckAdminAccessAsync(
			userId.Value, department, cancellationToken);

		if (adminAccess is not null)
			return adminAccess;

		if (string.IsNullOrWhiteSpace(presetName) ||
			presetName.Trim().Length > 100)
		{
			return Result.BadRequest(
				"A preset name of up to 100 characters is required.");
		}

		var validation = ValidateLayout(layout);
		if (validation is not null)
			return validation;

		try
		{
			var changed = await _layouts.UpdateDashboardPresetAsync(
				dashboardKey,
				presetId,
				department,
				presetName.Trim(),
				NormalizeDescription(description),
				layout.GetRawText(),
				cancellationToken);

			return changed == 0
				? Result.NotFound("Preset not found.")
				: Result.Ok(new { saved = true });
		}
		catch (SqlException ex) when (ex.Number is 2601 or 2627)
		{
			return Result.Conflict(
				"A preset with that name already exists for this dashboard.");
		}
	}

	public async Task<Result> DeleteDashboardPresetAsync(
		int? userId,
		string dashboardKey,
		int presetId,
		string department,
		CancellationToken cancellationToken)
	{
		if (!userId.HasValue)
			return Result.BadRequest("Select a demo user first.");

		var access = await CheckAccessAsync(
			userId, department, cancellationToken);

		if (access is not null)
			return access;

		var adminAccess = await CheckAdminAccessAsync(
			userId.Value, department, cancellationToken);

		if (adminAccess is not null)
			return adminAccess;

		var deleted = await _layouts.DeleteDashboardPresetAsync(
			dashboardKey, presetId, department, cancellationToken);

		return Result.Ok(new { deleted = deleted > 0 });
	}
}
