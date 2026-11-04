using System.Text.Json;
using Dapper;
using DashBoard;
using DashBoard.Catalog;
using DashBoard.Models;
using DashBoard.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

[ApiController]
[Route("api/dash")]
public class DashController : ControllerBase
{
	private readonly SqlConnectionFactory _db;
	private readonly CatalogService _catalog;
	private readonly DashboardQueryService _queryService;
	private readonly DashboardOverviewService _overviewService;
	private readonly DashboardDrilldownService _drilldownService;

	private readonly DatabaseSchemaDiscoveryService _schemaDiscovery;

	private readonly DatabaseRelationshipService _relationshipService;

	private readonly DatabaseAggregationSafetyService _aggregationSafetyService;

	private readonly AggregationQueryValidator _queryValidator;

	private readonly AutomaticJoinPlannerService _joinPlanner;

	private readonly AggregationSqlGenerator _sqlGenerator;

	private readonly AggregationQueryExecutionService _executionService;

	private readonly DashboardConfigurationService _dashboardConfigurationService;

	private readonly DashboardWidgetQueryService _widgetQueryService;
	private readonly QueryBuilderService _queryBuilderService;

	private readonly UIConfigurationService _uiConfigurationService;

	public DashController(
		SqlConnectionFactory db,
		CatalogService catalog,
		DashboardQueryService queryService,
		DashboardOverviewService overviewService,
		DashboardDrilldownService drilldownService,
		DatabaseSchemaDiscoveryService schemaDiscovery,
		DatabaseRelationshipService relationshipService,
		DatabaseAggregationSafetyService aggregationSafetyService,
		AggregationQueryValidator queryValidator,
		AutomaticJoinPlannerService joinPlanner,
		AggregationSqlGenerator sqlGenerator,
		AggregationQueryExecutionService executionService,
		DashboardConfigurationService dashboardConfigurationService,
		DashboardWidgetQueryService widgetQueryService,
		QueryBuilderService queryBuilderService,
		UIConfigurationService uiConfigurationService

		)
	{
		_db = db;
		_catalog = catalog;
		_queryService = queryService;
		_overviewService = overviewService;
		_drilldownService = drilldownService;
		_schemaDiscovery = schemaDiscovery;
		_relationshipService = relationshipService;
		_aggregationSafetyService = aggregationSafetyService;
		_queryValidator = queryValidator;
		_joinPlanner = joinPlanner;
		_sqlGenerator = sqlGenerator;
		_executionService = executionService;
		_dashboardConfigurationService = dashboardConfigurationService;
		_widgetQueryService = widgetQueryService;
		_queryBuilderService = queryBuilderService;
		_uiConfigurationService = uiConfigurationService;
	}


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

	[HttpPost("department")]
	public IActionResult SetDepartment([FromBody] DepartmentRequest request)
	{
		var allowedDepartments = new[]
		{
		"sales",
		"finance",
		"hr",
		"operations"
	};

		var department = request.Department?.Trim().ToLowerInvariant();

		if (department is null || !allowedDepartments.Contains(department))
			return BadRequest(new { error = "Invalid department." });

		HttpContext.Session.SetString(DepartmentSessionKey, department);

		return Ok(new { department });
	}

	[HttpGet("department")]
	public IActionResult GetDepartment()
	{
		var department = HttpContext.Session.GetString(DepartmentSessionKey);

		if (string.IsNullOrWhiteSpace(department))
			return BadRequest(new { error = "No department selected." });

		return Ok(new { department });
	}

	public sealed class DepartmentRequest
	{
		public string Department { get; set; } = "";
	}

	[HttpGet("query-builder/datasets")]
	public async Task<IActionResult> GetQueryBuilderDatasets(
	[FromQuery] string department,
	CancellationToken cancellationToken)
	{
		return Ok(await _queryBuilderService.GetDatasetsAsync(
			department, cancellationToken));
	}

	[HttpGet("query-builder/metadata")]
	public async Task<IActionResult> GetQueryBuilderMetadata(
		[FromQuery] string department,
		[FromQuery] string dataset,
		CancellationToken cancellationToken)
	{
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
		[FromBody] JsonElement request,
		CancellationToken cancellationToken)
	{
		try
		{
			return Ok(await _queryBuilderService.ExecuteAsync(
				department, request, cancellationToken));
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
	[FromQuery] string department,
	CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(department))
			return BadRequest(new { error = "Department is required." });

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

		var dashboards = await connection.QueryAsync(
			new CommandDefinition(
				sql,
				new { Department = department },
				cancellationToken: cancellationToken));

		return Ok(dashboards);
	}


	[HttpPost("dashboard/{dashboardKey}/widget/{widgetKey}")]
	public async Task<IActionResult> GetWidgetData(
		string dashboardKey,
		string widgetKey,
		[FromQuery] string department,
		[FromQuery] int page = 1,
		[FromQuery] int pageSize = 100,
		CancellationToken cancellationToken = default)
	{
		var result = await _widgetQueryService.ExecuteAsync(
			dashboardKey,
			widgetKey,
			department,
			page,
			pageSize,
			cancellationToken);

		return Ok(result);
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
		var schema = await _schemaDiscovery.DiscoverAsync(
			null,
			cancellationToken);

		var graph = await _relationshipService.BuildGraphAsync(
			null,
			cancellationToken);

		var joinPlan = _joinPlanner.Plan(
			graph,
			plan.Tables);

		if (!joinPlan.IsValid)
		{
			return BadRequest(new
			{
				isValid = false,
				errors = joinPlan.Errors
			});
		}

		var validationPlan = new AggregationQueryPlan
		{
			Tables = joinPlan.Tables,

			Joins = joinPlan.Joins.Select(j => new AggregationJoin
			{
				LeftAlias = j.LeftAlias,
				RightAlias = j.RightAlias,
				ConstraintName = j.ConstraintName
			}).ToList(),

			Dimensions = plan.Dimensions,
			Measures = plan.Measures,
			Filters = plan.Filters,
			Sort = plan.Sort,
			Page = plan.Page,
			PageSize = plan.PageSize
		};

		var errors = _queryValidator.Validate(
			validationPlan,
			schema);

		if (errors.Count > 0)
		{
			return BadRequest(new
			{
				isValid = false,
				errors
			});
		}

		var safety = _aggregationSafetyService.Analyze(
			graph,
			validationPlan);

		if (!safety.IsSafe)
		{
			return BadRequest(new
			{
				isValid = false,
				message = "The query may produce incorrect aggregations.",
				safety
			});
		}

		var generated = _sqlGenerator.Generate(
			plan,
			joinPlan,
			schema);

		var rows = await _executionService.ExecuteAsync(
			generated,
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
		// Discover the actual database schema.
		var schema = await _schemaDiscovery.DiscoverAsync(
			null,
			cancellationToken);

		// Build the relationship graph.
		var graph = await _relationshipService.BuildGraphAsync(
			null,
			cancellationToken);

		// Automatically resolve the joins.
		var joinPlan = _joinPlanner.Plan(
			graph,
			plan.Tables);

		if (!joinPlan.IsValid)
		{
			return BadRequest(new
			{
				isValid = false,
				errors = joinPlan.Errors
			});
		}

		// Create a validation plan containing the resolved tables and joins.
		var validationPlan = new AggregationQueryPlan
		{
			Tables = joinPlan.Tables,

			Joins = joinPlan.Joins.Select(j => new AggregationJoin
			{
				LeftAlias = j.LeftAlias,
				RightAlias = j.RightAlias,
				ConstraintName = j.ConstraintName
			}).ToList(),

			Dimensions = plan.Dimensions,
			Measures = plan.Measures,
			Filters = plan.Filters,
			Sort = plan.Sort,
			Page = plan.Page,
			PageSize = plan.PageSize
		};

		// Validate tables, columns, fields and pagination.
		var errors = _queryValidator.Validate(
			validationPlan,
			schema);

		if (errors.Count > 0)
		{
			return BadRequest(new
			{
				isValid = false,
				errors
			});
		}

		// Check aggregation safety using the resolved join relationships.
		var safety = _aggregationSafetyService.Analyze(
			graph,
			validationPlan);

		if (!safety.IsSafe)
		{
			return BadRequest(new
			{
				isValid = false,
				message = "The query may produce incorrect aggregations.",
				safety
			});
		}

		// Generate SQL without executing it.
		var generated = _sqlGenerator.Generate(
			plan,
			joinPlan,
			schema);

		return Ok(new
		{
			isValid = true,
			joinPlan,
			safety,
			generated.Sql,
			generated.Parameters
		});
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
