using Dapper;
using DashBoard;
using Microsoft.AspNetCore.Mvc;
using DashBoard.Catalog;
using DashBoard.Models;
using DashBoard.Services;

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
		AggregationQueryExecutionService executionService
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

	[HttpGet("relationships")]
	public async Task<IActionResult> GetRelationships(
	[FromQuery] string? schemaName,
	CancellationToken cancellationToken)
	{
		var graph = await _relationshipService.BuildGraphAsync(
			schemaName,
			cancellationToken);

		return Ok(graph);
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
