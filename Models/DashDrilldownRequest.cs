using DashBoard.Models;

namespace DashBoard.Models;

public sealed class DashDrilldownRequest
{
    public string Dataset { get; set; } = string.Empty;
    public string Dimension { get; set; } = string.Empty;
    public string? Value { get; set; }
    public List<DashFilter> Filters { get; set; } = [];
    public int PageSize { get; set; } = 200;
}
