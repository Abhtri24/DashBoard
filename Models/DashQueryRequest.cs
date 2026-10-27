
namespace DashBoard.Models;

public sealed class DashQueryRequest
{
	public string Dataset { get; set; } = string.Empty;

	public List<string> Dimensions { get; set; } = [];

	public List<string> Measures { get; set; } = [];

	public List<DashFilter> Filters { get; set; } = [];

	public List<DashSort> Sort { get; set; } = [];

	public int Page { get; set; } = 1;

	public int PageSize { get; set; } = 50;
}

public sealed class DashFilter
{
	public string Dimension { get; set; } = string.Empty;

	public string Operator { get; set; } = "eq";

	public string? Value { get; set; }
}

public sealed class DashSort
{
	public string Field { get; set; } = string.Empty;

	public string Direction { get; set; } = "asc";
}
