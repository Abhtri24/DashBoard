
using Dapper;

namespace DashBoard.Services;

public sealed class DashboardConfigurationService
{
	private readonly SqlConnectionFactory _db;

	public DashboardConfigurationService(SqlConnectionFactory db)
	{
		_db = db;
	}

	public async Task<DashboardConfiguration?> GetDashboardAsync(
		string dashboardKey,
		string departmentKey,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(dashboardKey) ||
			string.IsNullOrWhiteSpace(departmentKey))
		{
			throw new ArgumentException(
				"Dashboard key and department key are required.");
		}

		await using var connection = await _db.OpenAsync();

		const string dashboardSql = """
            SELECT
                DashboardId,
                DashboardKey,
                DepartmentKey,
                DisplayName,
                Description
            FROM dash.Dashboard
            WHERE DashboardKey = @DashboardKey
              AND DepartmentKey = @DepartmentKey
              AND IsActive = 1;
            """;

		var dashboard = await connection.QuerySingleOrDefaultAsync<
			DashboardConfiguration>(
				new CommandDefinition(
					dashboardSql,
					new { DashboardKey = dashboardKey, DepartmentKey = departmentKey },
					cancellationToken: cancellationToken));

		if (dashboard is null)
			return null;

		const string widgetsSql = """
            SELECT
                w.WidgetId,
                w.WidgetKey,
                w.Title,
                w.WidgetType,
                w.Position,
                w.DatasetKey,
                w.GridColumnSpan,
                w.GridRowSpan,
                w.Height
            FROM dash.DashboardWidget w
            INNER JOIN dash.DatasetAccess da
                ON da.DatasetKey = w.DatasetKey
               AND da.Department = @DepartmentKey
            INNER JOIN dash.Dataset d
                ON d.DatasetKey = w.DatasetKey
            WHERE w.DashboardId = @DashboardId
              AND w.IsEnabled = 1
              AND w.DatasetKey IS NOT NULL
            ORDER BY w.Position;
            """;

		var widgets = (await connection.QueryAsync<DashboardWidgetConfiguration>(
			new CommandDefinition(
				widgetsSql,
				new
				{
					dashboard.DashboardId,
					DepartmentKey = departmentKey
				},
				cancellationToken: cancellationToken))).ToList();

		if (widgets.Count == 0)
		{
			dashboard.Widgets = [];
			return dashboard;
		}

		var widgetIds = widgets.Select(w => w.WidgetId).ToArray();

		const string measuresSql = """
            SELECT
                WidgetId,
                ColumnKey,
                Position,
                AggregationOverride,
                LabelOverride
            FROM dash.WidgetMeasure
            WHERE WidgetId IN @WidgetIds
            ORDER BY WidgetId, Position;
            """;

		const string dimensionsSql = """
            SELECT
                WidgetId,
                ColumnKey,
                Position,
                LabelOverride
            FROM dash.WidgetDimension
            WHERE WidgetId IN @WidgetIds
            ORDER BY WidgetId, Position;
            """;

		const string filtersSql = """
            SELECT
                WidgetId,
                ColumnKey,
                Operator,
                FilterValue,
                Position
            FROM dash.WidgetFilter
            WHERE WidgetId IN @WidgetIds
              AND IsEnabled = 1
            ORDER BY WidgetId, Position;
            """;

		var measures = await connection.QueryAsync<WidgetMeasureConfiguration>(
			new CommandDefinition(
				measuresSql,
				new { WidgetIds = widgetIds },
				cancellationToken: cancellationToken));

		var dimensions = await connection.QueryAsync<WidgetDimensionConfiguration>(
			new CommandDefinition(
				dimensionsSql,
				new { WidgetIds = widgetIds },
				cancellationToken: cancellationToken));

		var filters = await connection.QueryAsync<WidgetFilterConfiguration>(
			new CommandDefinition(
				filtersSql,
				new { WidgetIds = widgetIds },
				cancellationToken: cancellationToken));

		var measuresByWidget = measures.GroupBy(x => x.WidgetId)
			.ToDictionary(g => g.Key, g => g.ToList());

		var dimensionsByWidget = dimensions.GroupBy(x => x.WidgetId)
			.ToDictionary(g => g.Key, g => g.ToList());

		var filtersByWidget = filters.GroupBy(x => x.WidgetId)
			.ToDictionary(g => g.Key, g => g.ToList());

		foreach (var widget in widgets)
		{
			widget.Measures = measuresByWidget.GetValueOrDefault(
				widget.WidgetId, []);

			widget.Dimensions = dimensionsByWidget.GetValueOrDefault(
				widget.WidgetId, []);

			widget.Filters = filtersByWidget.GetValueOrDefault(
				widget.WidgetId, []);
		}

		dashboard.Widgets = widgets;

		return dashboard;
	}
}

public sealed class DashboardConfiguration
{
	public int DashboardId { get; set; }
	public string DashboardKey { get; set; } = "";
	public string DepartmentKey { get; set; } = "";
	public string DisplayName { get; set; } = "";
	public string? Description { get; set; }

	public List<DashboardWidgetConfiguration> Widgets { get; set; } = [];
}

public sealed class DashboardWidgetConfiguration
{
	public int WidgetId { get; set; }
	public string WidgetKey { get; set; } = "";
	public string Title { get; set; } = "";
	public string WidgetType { get; set; } = "";
	public int Position { get; set; }
	public string DatasetKey { get; set; } = "";
	public int GridColumnSpan { get; set; }
	public int GridRowSpan { get; set; }
	public int? Height { get; set; }

	public List<WidgetMeasureConfiguration> Measures { get; set; } = [];
	public List<WidgetDimensionConfiguration> Dimensions { get; set; } = [];
	public List<WidgetFilterConfiguration> Filters { get; set; } = [];
}

public sealed class WidgetMeasureConfiguration
{
	public int WidgetId { get; set; }
	public string ColumnKey { get; set; } = "";
	public int Position { get; set; }
	public string? AggregationOverride { get; set; }
	public string? LabelOverride { get; set; }
}

public sealed class WidgetDimensionConfiguration
{
	public int WidgetId { get; set; }
	public string ColumnKey { get; set; } = "";
	public int Position { get; set; }
	public string? LabelOverride { get; set; }
}

public sealed class WidgetFilterConfiguration
{
	public int WidgetId { get; set; }
	public string ColumnKey { get; set; } = "";
	public string Operator { get; set; } = "";
	public string FilterValue { get; set; } = "";
	public int Position { get; set; }
}
