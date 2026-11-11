using System.Text.Json;
using Dapper;
using DashBoard;
using DashBoard.Catalog;
using DashBoard.Models;
using DashBoard.Services;
using DashBoard.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/dash")]
public class DashController : ControllerBase
{
	private readonly SqlConnectionFactory _db;
	private readonly DepartmentRepository _departmentRepository;
	private readonly CatalogService _catalog;
	private readonly DashboardQueryService _queryService;
	private readonly DashboardOverviewService _overviewService;
	private readonly DashboardDrilldownService _drilldownService;

	private readonly DatabaseSchemaDiscoveryService _schemaDiscovery;

	private readonly DatabaseRelationshipService _relationshipService;

	private readonly DatabaseAggregationSafetyService _aggregationSafetyService;

	private readonly AggregationQueryValidator _queryValidator;

	private readonly AutomaticJoinPlannerService _joinPlanner;

	private readonly AggregationQueryPreparationService _aggregationQueryPreparationService;

	private readonly AggregationQueryExecutionService _executionService;

	private readonly DashboardConfigurationService _dashboardConfigurationService;

	private readonly DashboardWidgetQueryService _widgetQueryService;
	private readonly QueryBuilderService _queryBuilderService;

	private readonly UIConfigurationService _uiConfigurationService;

	private readonly IMemoryCache _cache;

	private readonly PdfExportService _pdfExportService;

	private readonly DashboardRepository _dashboardRepository;

	private readonly DashboardLayoutService _dashboardLayoutService;

	public DashController(
		SqlConnectionFactory db,
		DepartmentRepository departmentRepository,
		CatalogService catalog,
		DashboardQueryService queryService,
		DashboardOverviewService overviewService,
		DashboardDrilldownService drilldownService,
		DatabaseSchemaDiscoveryService schemaDiscovery,
		DatabaseRelationshipService relationshipService,
		DatabaseAggregationSafetyService aggregationSafetyService,
		AggregationQueryValidator queryValidator,
		AutomaticJoinPlannerService joinPlanner,
		AggregationQueryExecutionService executionService,
		AggregationQueryPreparationService aggregationQueryPreparationService,
		DashboardConfigurationService dashboardConfigurationService,
		DashboardWidgetQueryService widgetQueryService,
		QueryBuilderService queryBuilderService,
		UIConfigurationService uiConfigurationService,
		IMemoryCache cache,
		PdfExportService pdfExportService,
		DashboardRepository dashboardRepository,
		DashboardLayoutService dashboardLayoutService)
	{
		_db = db;
		_departmentRepository = departmentRepository;
		_catalog = catalog;
		_queryService = queryService;
		_overviewService = overviewService;
		_drilldownService = drilldownService;
		_schemaDiscovery = schemaDiscovery;
		_relationshipService = relationshipService;
		_aggregationSafetyService = aggregationSafetyService;
		_queryValidator = queryValidator;
		_joinPlanner = joinPlanner;
		_executionService = executionService;
		_aggregationQueryPreparationService = aggregationQueryPreparationService;
		_dashboardConfigurationService = dashboardConfigurationService;
		_widgetQueryService = widgetQueryService;
		_queryBuilderService = queryBuilderService;
		_uiConfigurationService = uiConfigurationService;
		_cache = cache;
		_pdfExportService = pdfExportService;
		_dashboardRepository = dashboardRepository;
		_dashboardLayoutService = dashboardLayoutService;
	}
	private IActionResult FromServiceResult(DashboardLayoutService.Result result)
	{
		return StatusCode(result.StatusCode, result.Data);
	}
	private sealed record CachedWidgetExport(
	int UserId,
	string Department,
	Guid TabId,
	string Title,
	List<string> Columns,
	List<Dictionary<string, object?>> Rows);

	[HttpGet("ui-configuration")]
	public async Task<IActionResult> GetUIConfiguration(
	CancellationToken cancellationToken)
	{
		var configuration = await _uiConfigurationService.GetAsync(
			cancellationToken);

		return Ok(new
		{
			dimensionLabel = configuration.DimensionLabel,
			measureLabel = configuration.MeasureLabel
		});
	}


	private const string DepartmentSessionKey = "ActiveDepartment";
	private const string DemoUserSessionKey = "ActiveDemoUserId";
	private const string DashboardTabHeader = "X-Dashboard-Tab";

	private string GetTabSessionKey(string key)
	{
		var tabId = Request.Headers[DashboardTabHeader].ToString();
		if (!Guid.TryParse(tabId, out var parsedTabId))
			throw new InvalidOperationException($"A valid {DashboardTabHeader} header is required.");

		return $"{key}:{parsedTabId:N}";
	}

	private int? GetActiveDemoUserId() =>
		HttpContext.Session.GetInt32(GetTabSessionKey(DemoUserSessionKey));

	private string? GetActiveDepartment() =>
		HttpContext.Session.GetString(GetTabSessionKey(DepartmentSessionKey));

	private async Task<IActionResult?> CheckDepartmentAccessAsync(
		string? department,
		CancellationToken cancellationToken)
	{
		var userId = GetActiveDemoUserId();
		if (!userId.HasValue)
			return BadRequest(new { error = "Select a demo user first." });

		if (string.IsNullOrWhiteSpace(department))
			return BadRequest(new { error = "Department is required." });

		if (!await _departmentRepository.HasDepartmentAccessAsync(userId.Value, department, cancellationToken))
			return StatusCode(403, new { error = "You do not have access to this department." });

		return null;
	}



	[HttpPost("demo-user")]
	public async Task<IActionResult> SetDemoUser(
		[FromBody] DemoUserRequest request,
		CancellationToken cancellationToken)
	{
		if (request.UserId <= 0)
			return BadRequest(new { error = "Select a valid demo user." });

		var defaultDepartment = await _departmentRepository.GetDefaultDepartmentAsync(
			request.UserId,
			cancellationToken);

		if (string.IsNullOrWhiteSpace(defaultDepartment))
		{
			return BadRequest(new
			{
				error = "The selected demo user does not exist, is inactive, or has no active department membership."
			});
		}

		HttpContext.Session.SetInt32(
			GetTabSessionKey(DemoUserSessionKey),
			request.UserId);

		HttpContext.Session.SetString(
			GetTabSessionKey(DepartmentSessionKey),
			defaultDepartment);

		return Ok(new
		{
			userId = request.UserId,
			department = defaultDepartment
		});
	}
	[HttpGet("demo-user")]
	public IActionResult GetDemoUser()
	{
		var userId = GetActiveDemoUserId();
		return userId.HasValue
			? Ok(new { userId })
			: BadRequest(new { error = "No demo user selected." });
	}

	public sealed class DemoUserRequest
	{
		public int UserId { get; set; }
	}

	[HttpGet("dashboard/{dashboardKey}/layout")]
	public async Task<IActionResult> GetUserLayout(
		string dashboardKey,
		[FromQuery] string department,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.GetUserLayoutAsync(
			GetActiveDemoUserId(), dashboardKey, department, cancellationToken);
		return FromServiceResult(result);
	}

	[HttpPut("dashboard/{dashboardKey}/layout")]
	public async Task<IActionResult> SaveUserLayout(
		string dashboardKey,
		[FromQuery] string department,
		[FromBody] JsonElement layout,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.SaveUserLayoutAsync(
			GetActiveDemoUserId(), dashboardKey, department, layout, cancellationToken);
		return FromServiceResult(result);
	}

	[HttpGet("dashboard/{dashboardKey}/department-layout")]
	public async Task<IActionResult> GetDepartmentLayout(
		string dashboardKey,
		[FromQuery] string department,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.GetDepartmentLayoutAsync(
			GetActiveDemoUserId(), dashboardKey, department, cancellationToken);
		return FromServiceResult(result);
	}

	[HttpPut("dashboard/{dashboardKey}/department-layout")]
	public async Task<IActionResult> PublishDepartmentLayout(
		string dashboardKey,
		[FromQuery] string department,
		[FromBody] JsonElement layout,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.PublishDepartmentLayoutAsync(
			GetActiveDemoUserId(), dashboardKey, department, layout, cancellationToken);
		return FromServiceResult(result);
	}

	[HttpDelete("dashboard/{dashboardKey}/department-layout")]
	public async Task<IActionResult> DeleteDepartmentLayout(
		string dashboardKey,
		[FromQuery] string department,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.DeleteDepartmentLayoutAsync(
			GetActiveDemoUserId(), dashboardKey, department, cancellationToken);
		return FromServiceResult(result);
	}

	[HttpGet("dashboard/{dashboardKey}/presets")]
	public async Task<IActionResult> GetDashboardPresets(
		string dashboardKey,
		[FromQuery] string department,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.GetDashboardPresetsAsync(
			GetActiveDemoUserId(), dashboardKey, department, cancellationToken);
		return FromServiceResult(result);
	}

	[HttpPost("dashboard/{dashboardKey}/presets")]
	public async Task<IActionResult> CreateDashboardPreset(
		string dashboardKey,
		[FromQuery] string department,
		[FromBody] DashboardPresetRequest request,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.CreateDashboardPresetAsync(
			GetActiveDemoUserId(), dashboardKey, department,
			request.PresetName, request.Description, request.Layout, cancellationToken);
		return FromServiceResult(result);
	}

	[HttpPut("dashboard/{dashboardKey}/presets/{presetId:int}")]
	public async Task<IActionResult> UpdateDashboardPreset(
		string dashboardKey,
		int presetId,
		[FromQuery] string department,
		[FromBody] DashboardPresetRequest request,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.UpdateDashboardPresetAsync(
			GetActiveDemoUserId(), dashboardKey, presetId, department,
			request.PresetName, request.Description, request.Layout, cancellationToken);
		return FromServiceResult(result);
	}

	[HttpDelete("dashboard/{dashboardKey}/presets/{presetId:int}")]
	public async Task<IActionResult> DeleteDashboardPreset(
		string dashboardKey,
		int presetId,
		[FromQuery] string department,
		CancellationToken cancellationToken)
	{
		var result = await _dashboardLayoutService.DeleteDashboardPresetAsync(
			GetActiveDemoUserId(), dashboardKey, presetId, department, cancellationToken);
		return FromServiceResult(result);
	}

	public sealed class DashboardPresetRequest
	{
		public string PresetName { get; set; } = "";
		public string? Description { get; set; }
		public JsonElement Layout { get; set; }
	}

	[HttpGet("departments")]
	public async Task<IActionResult> GetDepartments(
		CancellationToken cancellationToken)
	{
		var userId = GetActiveDemoUserId();

		if (!userId.HasValue)
			return BadRequest(new { error = "Select a demo user first." });

		var departments = await _departmentRepository.GetDepartmentsAsync(
			userId.Value,
			cancellationToken);

		return Ok(departments);
	}


	[HttpGet("query-builder/datasets")]
	public async Task<IActionResult> GetQueryBuilderDatasets(
	[FromQuery] string department,
	CancellationToken cancellationToken)
	{
		var accessError = await CheckDepartmentAccessAsync(department, cancellationToken);
		if (accessError is not null)
			return accessError;

		return Ok(await _queryBuilderService.GetDatasetsAsync(
			department, cancellationToken));
	}

	[HttpGet("query-builder/metadata")]
	public async Task<IActionResult> GetQueryBuilderMetadata(
		[FromQuery] string department,
		[FromQuery] string dataset,
		CancellationToken cancellationToken)
	{
		var accessError = await CheckDepartmentAccessAsync(department, cancellationToken);
		if (accessError is not null)
			return accessError;

		try
		{
			return Ok(await _queryBuilderService.GetMetadataAsync(
				department, dataset, cancellationToken));
		}
		catch (UnauthorizedAccessException ex)
		{
			return StatusCode(403, new { error = ex.Message });
		}
	}

	[HttpPost("query-builder/execute")]
	public async Task<IActionResult> ExecuteQueryBuilder(
		[FromQuery] string department,
		[FromQuery] string? title,
		[FromBody] JsonElement request,
		CancellationToken cancellationToken)
	{
		var accessError = await CheckDepartmentAccessAsync(department, cancellationToken);
		if (accessError is not null)
			return accessError;

		try
		{
			var result = await _queryBuilderService.ExecuteAsync(
				department, request, cancellationToken);

			var rows = result.Select(row =>
			{
				var values = (IDictionary<string, object>)row;

				return values.ToDictionary(
					item => item.Key,
					item => (object?)item.Value,
					StringComparer.OrdinalIgnoreCase);
			}).ToList();

			var userId = GetActiveDemoUserId();

			if (!userId.HasValue)
				return BadRequest(new { error = "Select a demo user first." });

			if (!Guid.TryParse(
				Request.Headers[DashboardTabHeader].ToString(),
				out var tabId))
			{
				return BadRequest(new { error = "Invalid dashboard tab." });
			}

			var exportId = Guid.NewGuid();

			var columns = rows.FirstOrDefault()?.Keys.ToList()
				?? new List<string>();

			var cachedExport = new CachedWidgetExport(
				userId.Value,
				department,
				tabId,
				string.IsNullOrWhiteSpace(title) ? "Dashboard Table" : title.Trim(),
				columns,
				rows);

			_cache.Set(
				$"widget-export:{exportId:N}",
				cachedExport,
				TimeSpan.FromMinutes(15));

			return Ok(new { rows, exportId });
		}
		catch (UnauthorizedAccessException ex)
		{
			return StatusCode(403, new { error = ex.Message });
		}
		catch (ArgumentException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}


	[HttpGet("dashboards")]
	public async Task<IActionResult> GetDashboards(
		CancellationToken cancellationToken)
	{
		var department = GetActiveDepartment();

		var accessError = await CheckDepartmentAccessAsync(
			department, cancellationToken);

		if (accessError is not null)
			return accessError;

		if (string.IsNullOrWhiteSpace(department))
			return BadRequest(new { error = "Department is required." });

		var dashboards = await _dashboardRepository.GetDashboardsAsync(
			department, cancellationToken);

		return Ok(dashboards);
	}


	[HttpPost("dashboard/{dashboardKey}/widget/{widgetKey}")]
	public async Task<IActionResult> GetWidgetData(
		string dashboardKey,
		string widgetKey,
		[FromQuery] string department,
		[FromQuery] string? title = null,
		[FromQuery] int page = 1,
		[FromQuery] int pageSize = 100,
		CancellationToken cancellationToken = default)
	{
		var accessError = await CheckDepartmentAccessAsync(department, cancellationToken);
		if (accessError is not null)
			return accessError;

		var result = await _widgetQueryService.ExecuteAsync(
			dashboardKey,
			widgetKey,
			department,
			page,
			pageSize,
			cancellationToken);


		var rows = result.Select(row =>
		{
			var values = (IDictionary<string, object>)row;

			return values.ToDictionary(
				item => item.Key,
				item => (object?)item.Value,
				StringComparer.OrdinalIgnoreCase);
		}).ToList();


		var userId = GetActiveDemoUserId();

		if (!userId.HasValue)
			return BadRequest(new { error = "Select a demo user first." });

		if (!Guid.TryParse(Request.Headers[DashboardTabHeader].ToString(), out var tabId))
		{
			return BadRequest(new { error = "Invalid dashboard tab." });
		}

		var exportId = Guid.NewGuid();
		var columns = rows.FirstOrDefault()?.Keys.ToList() ?? new List<string>();

		var cachedExport = new CachedWidgetExport(
			userId.Value,
			department,
			tabId,
				string.IsNullOrWhiteSpace(title) ? widgetKey : title,

			columns,
			rows);

		_cache.Set(
			$"widget-export:{exportId:N}",
			cachedExport,
			TimeSpan.FromMinutes(15));



		return Ok(new
		{
			rows,
			exportId
		});
	}


	[HttpGet("widget-export/{exportId:guid}")]
	public IActionResult ExportWidgetPdf(Guid exportId)
	{
		var userId = GetActiveDemoUserId();

		if (!userId.HasValue)
			return BadRequest(new { error = "Select a demo user first." });

		if (!Guid.TryParse(
			Request.Headers[DashboardTabHeader].ToString(),
			out var tabId))
		{
			return BadRequest(new { error = "Invalid dashboard tab." });
		}

		if (!_cache.TryGetValue(
			$"widget-export:{exportId:N}",
			out CachedWidgetExport? cached) || cached is null)
		{
			return NotFound(new
			{
				error = "This export has expired. Refresh the dashboard and try again."
			});
		}

		// A cached result can only be exported by the same user,
		// department and browser tab that originally loaded it.
		if (cached.UserId != userId.Value ||
			cached.TabId != tabId ||
			!string.Equals(
				cached.Department,
				GetActiveDepartment(),
				StringComparison.OrdinalIgnoreCase))
		{
			return Forbid();
		}

		var pdf = _pdfExportService.GeneratePdf(
	cached.Title,
	cached.Columns,
	cached.Rows);

		var fileName = $"dashboard-{exportId:N}.pdf";

		return File(pdf, "application/pdf", fileName);
	}



	[HttpGet("relationships")]
	public async Task<IActionResult> GetRelationships(
	[FromQuery] string? schemaName,
	[FromQuery] string? datasetKey,
	CancellationToken cancellationToken)
	{
		var graph = await _relationshipService.BuildGraphAsync(
			schemaName,
			cancellationToken,
			datasetKey);

		return Ok(graph);
	}

	[HttpGet("dashboard/{dashboardKey}")]
	public async Task<IActionResult> GetDashboard(
	string dashboardKey,
	[FromQuery] string department,
	CancellationToken cancellationToken)
	{
		var accessError = await CheckDepartmentAccessAsync(department, cancellationToken);
		if (accessError is not null)
			return accessError;

		var dashboard = await _dashboardConfigurationService.GetDashboardAsync(
			dashboardKey,
			department,
			cancellationToken);

		if (dashboard is null)
			return NotFound(new { error = "Dashboard not found." });

		return Ok(dashboard);
	}

	[HttpPost("execute-query")]
	public async Task<IActionResult> ExecuteQuery(
		[FromBody] AggregationQueryPlan plan,
		CancellationToken cancellationToken)
	{
		var preparation = await _aggregationQueryPreparationService.PrepareAsync(
			plan,
			cancellationToken);

		var preparationError = CreatePreparationError(preparation);
		if (preparationError is not null)
			return preparationError;

		var rows = await _executionService.ExecuteAsync(
			preparation.Generated!,
			cancellationToken);

		return Ok(new
		{
			isValid = true,
			rowCount = rows.Count(),
			rows
		});
	}


	[HttpPost("generate-sql")]
	public async Task<IActionResult> GenerateSql(
		[FromBody] AggregationQueryPlan plan,
		CancellationToken cancellationToken)
	{
		var preparation = await _aggregationQueryPreparationService.PrepareAsync(
			plan,
			cancellationToken);

		var preparationError = CreatePreparationError(preparation);
		if (preparationError is not null)
			return preparationError;

		return Ok(new
		{
			isValid = true,
			joinPlan = preparation.JoinPlan,
			safety = preparation.Safety,
			Sql = preparation.Generated!.Sql,
			Parameters = preparation.Generated.Parameters
		});
	}

	private IActionResult? CreatePreparationError(
		AggregationQueryPreparationResult preparation)
	{
		return preparation.Failure switch
		{
			AggregationQueryPreparationFailure.JoinPlan or
			AggregationQueryPreparationFailure.Validation =>
				BadRequest(new
				{
					isValid = false,
					errors = preparation.Errors
				}),
			AggregationQueryPreparationFailure.Safety =>
				BadRequest(new
				{
					isValid = false,
					message = "The query may produce incorrect aggregations.",
					safety = preparation.Safety
				}),
			_ => null
		};
	}


	[HttpPost("plan-joins")]
	public async Task<IActionResult> PlanJoins(
	[FromBody] List<AggregationTable> selectedTables,
	CancellationToken cancellationToken)
	{
		var graph = await _relationshipService.BuildGraphAsync(
			null,
			cancellationToken);

		var result = _joinPlanner.Plan(
			graph,
			selectedTables);

		if (!result.IsValid)
		{
			return BadRequest(result);
		}

		return Ok(result);
	}

	[HttpPost("validate-plan")]
	public async Task<IActionResult> ValidatePlan(
	[FromBody] AggregationQueryPlan plan,
	CancellationToken cancellationToken)
	{
		var schema = await _schemaDiscovery.DiscoverAsync(
			null,
			cancellationToken);

		var errors = _queryValidator.Validate(plan, schema);

		if (errors.Count > 0)
		{
			return BadRequest(new
			{
				isValid = false,
				errors
			});
		}

		return Ok(new
		{
			isValid = true,
			message = "Query plan passed validation."
		});
	}

	[HttpPost("aggregation-safety")]
	public async Task<IActionResult> CheckAggregationSafety(
	[FromBody] AggregationQueryPlan plan,
	[FromQuery] string? schemaName,
	CancellationToken cancellationToken)
	{
		var graph = await _relationshipService.BuildGraphAsync(
			schemaName,
			cancellationToken);

		var result = _aggregationSafetyService.Analyze(
			graph,
			plan);

		return Ok(result);
	}



	[HttpGet("relationships/paths")]
	public async Task<IActionResult> GetRelationshipPaths(
		[FromQuery] string fromSchema,
		[FromQuery] string fromTable,
		[FromQuery] string toSchema,
		[FromQuery] string toTable,
		CancellationToken cancellationToken)
	{
		var graph = await _relationshipService.BuildGraphAsync(
			null,
			cancellationToken);

		var paths = _relationshipService.FindJoinPaths(
			graph,
			fromSchema,
			fromTable,
			toSchema,
			toTable);

		return Ok(new
		{
			From = $"{fromSchema}.{fromTable}",
			To = $"{toSchema}.{toTable}",
			PathCount = paths.Count,
			Paths = paths
		});
	}

	[HttpGet("schema")]
	public async Task<IActionResult> GetSchema(
		[FromQuery] string? schemaName, CancellationToken cancellationToken)

	{
		var schema = await _schemaDiscovery.DiscoverAsync(schemaName, cancellationToken);
		return Ok(schema);
	}

	[HttpGet("ping")]
	public async Task<IActionResult> Ping()
	{
		await using var connection = await _db.OpenAsync();
		var n = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.vw_SalesMaster");
		return Ok(new { count = n });
	}

	[HttpGet("overview")]
	public async Task<IActionResult> Overview()
	{
		var result = await _overviewService.GetOverviewAsync();
		return Ok(result);
	}

	[HttpGet("meta/datasets")]
	public async Task<IActionResult> Meta([FromQuery] string dataset)
	{
		try
		{
			return Ok(await _catalog.GetAsync(dataset));
		}
		catch (ArgumentException ex)
		{
			return NotFound(new { error = ex.Message });
		}
	}

	[HttpPost("query")]
	public async Task<IActionResult> Query([FromBody] DashQueryRequest request)
	{
		try
		{
			var result = await _queryService.QueryAsync(request);
			return Ok(result);
		}
		catch (ArgumentException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}

	[HttpPost("drilldown")]
	public async Task<IActionResult> Drilldown([FromBody] DashDrilldownRequest request)
	{
		try
		{
			return Ok(await _drilldownService.QueryAsync(request));
		}
		catch (ArgumentException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}
}

