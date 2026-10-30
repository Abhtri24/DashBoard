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

	public DashController(
		SqlConnectionFactory db,
		CatalogService catalog,
		DashboardQueryService queryService,
		DashboardOverviewService overviewService,
		DashboardDrilldownService drilldownService)
	{
		_db = db;
		_catalog = catalog;
		_queryService = queryService;
		_overviewService = overviewService;
		_drilldownService = drilldownService;
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
