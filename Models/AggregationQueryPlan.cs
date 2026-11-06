
namespace DashBoard.Models;

public sealed class AggregationQueryPlan
{
	public List<AggregationTable> Tables { get; set; } = [];

	public List<AggregationJoin> Joins { get; set; } = [];

	public List<AggregationDimension> Dimensions { get; set; } = [];

	public List<AggregationMeasure> Measures { get; set; } = [];

	public List<AggregationFilter> Filters { get; set; } = [];

	public List<AggregationHavingFilter> HavingFilters { get; set; } = [];

	public List<AggregationSort> Sort { get; set; } = [];

	public int Page { get; set; } = 1;

	public int PageSize { get; set; } = 50;
}

public sealed class AggregationTable
{
	public string Alias { get; set; } = "";

	public string Schema { get; set; } = "";

	public string Table { get; set; } = "";
}

public sealed class AggregationJoin
{
	public string LeftAlias { get; set; } = "";

	public string RightAlias { get; set; } = "";

	public string ConstraintName { get; set; } = "";
}

public sealed class AggregationDimension
{
	public string Key { get; set; } = "";

	public string SourceAlias { get; set; } = "";

	public string Column { get; set; } = "";
}

public sealed class AggregationMeasure
{
	public string Key { get; set; } = "";
	public string SourceAlias { get; set; } = "";
	public string Column { get; set; } = "";
	public string Aggregation { get; set; } = "";

	public AggregationExpression? Expression { get; set; }

}

public sealed class AggregationExpression
{
	// COLUMN, VALUE, or an arithmetic operator.
	public string Type { get; set; } = "";

	// Used by COLUMN.
	public string SourceAlias { get; set; } = "";
	public string Column { get; set; } = "";

	// Used by VALUE.
	public decimal? Value { get; set; }

	// Used by arithmetic operators.
	public List<AggregationExpression> Operands { get; set; } = [];
}

public sealed class AggregationFilter
{
	public string SourceAlias { get; set; } = "";

	public string Column { get; set; } = "";

	public string Operator { get; set; } = "eq";

	public string? Value { get; set; }

	public string DataType { get; set; } = "nvarchar";
}

public sealed class AggregationSort
{
	public string Field { get; set; } = "";

	public string Direction { get; set; } = "asc";
}

public sealed class AggregationHavingFilter
{
	public string MeasureKey { get; set; } = "";
	public string Operator { get; set; } = "gt";
	public string? Value { get; set; }
	public string DataType { get; set; } = "decimal";
}
