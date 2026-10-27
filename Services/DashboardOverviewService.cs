using Dapper;

namespace DashBoard.Services;

public class DashboardOverviewService
{
	private readonly SqlConnectionFactory _db;

	public DashboardOverviewService(SqlConnectionFactory db)
	{
		_db = db;
	}

	public async Task<object> GetOverviewAsync()
	{
		await using var connection = await _db.OpenAsync();

		const string kpiSql = """
            SELECT
                COALESCE(SUM(NetAmount), 0) AS TotalRevenue,
                COUNT(DISTINCT OrderId) AS TotalOrders,
                COUNT(DISTINCT Region) AS ActiveRegions
            FROM dbo.vw_SalesMaster
            WHERE Status = 'Completed';
            """;

		var kpis = await connection.QuerySingleAsync(kpiSql);

		const string monthlySql = """
            SELECT
                YearMonth AS [Month],
                SUM(NetAmount) AS Revenue
            FROM dbo.vw_SalesMaster
            WHERE Status = 'Completed'
            GROUP BY YearMonth
            ORDER BY YearMonth;
            """;

		var monthlyRevenue = await connection.QueryAsync(monthlySql);

		const string regionSql = """
            SELECT
                Region,
                SUM(NetAmount) AS Revenue
            FROM dbo.vw_SalesMaster
            WHERE Status = 'Completed'
            GROUP BY Region
            ORDER BY Revenue DESC;
            """;

		var regionalRevenue = await connection.QueryAsync(regionSql);

		const string transactionsSql = """
            WITH Orders AS
            (
                SELECT
                    OrderId,
                    MAX(OrderNumber) AS OrderNumber,
                    MAX(OrderDate) AS OrderDate,
                    MAX(Region) AS Region,
                    MAX(Status) AS Status,
                    SUM(NetAmount) AS Amount
                FROM dbo.vw_SalesMaster
                GROUP BY OrderId
            )
            SELECT TOP (10)
                OrderNumber,
                OrderDate,
                Region,
                Amount,
                Status
            FROM Orders
            ORDER BY OrderDate DESC, OrderId DESC;
            """;

		var recentTransactions =
			await connection.QueryAsync(transactionsSql);

		return new
		{
			kpis = new
			{
				totalRevenue = (decimal)kpis.TotalRevenue,
				totalOrders = (int)kpis.TotalOrders,
				averageOrderValue = (int)kpis.TotalOrders == 0
					? 0m
					: (decimal)kpis.TotalRevenue / (int)kpis.TotalOrders,
				activeRegions = (int)kpis.ActiveRegions
			},
			monthlyRevenue,
			regionalRevenue,
			recentTransactions
		};
	}
}
