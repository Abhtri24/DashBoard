namespace DashBoard.Models;

public sealed class AutomaticJoinPlan
{
	public bool IsValid { get; set; }

	public List<AggregationTable> Tables { get; set; } = [];

	public List<PlannedJoin> Joins { get; set; } = [];

	public List<string> Errors { get; set; } = [];
}

public sealed class PlannedJoin
{
	public string LeftAlias { get; set; } = "";

	public string RightAlias { get; set; } = "";

	public string ConstraintName { get; set; } = "";

	public List<PlannedJoinColumn> Columns { get; set; } = [];
}

public sealed class PlannedJoinColumn
{
	public string LeftColumn { get; set; } = "";

	public string RightColumn { get; set; } = "";
}