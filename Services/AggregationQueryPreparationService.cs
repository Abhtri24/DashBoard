using DashBoard.Models;

namespace DashBoard.Services;

public enum AggregationQueryPreparationFailure
{
	None,
	JoinPlan,
	Validation,
	Safety
}

public sealed class AggregationQueryPreparationResult
{
	public AggregationQueryPreparationFailure Failure { get; init; }
	public AutomaticJoinPlan? JoinPlan { get; init; }
	public AggregationSafetyResult? Safety { get; init; }
	public GeneratedAggregationSql? Generated { get; init; }
	public List<string> Errors { get; init; } = [];
}

public sealed class AggregationQueryPreparationService
{
	private readonly DatabaseSchemaDiscoveryService _schemaDiscovery;
	private readonly DatabaseRelationshipService _relationships;
	private readonly AutomaticJoinPlannerService _joinPlanner;
	private readonly AggregationQueryValidator _validator;
	private readonly DatabaseAggregationSafetyService _safety;
	private readonly AggregationSqlGenerator _sqlGenerator;

	public AggregationQueryPreparationService(
		DatabaseSchemaDiscoveryService schemaDiscovery,
		DatabaseRelationshipService relationships,
		AutomaticJoinPlannerService joinPlanner,
		AggregationQueryValidator validator,
		DatabaseAggregationSafetyService safety,
		AggregationSqlGenerator sqlGenerator)
	{
		_schemaDiscovery = schemaDiscovery;
		_relationships = relationships;
		_joinPlanner = joinPlanner;
		_validator = validator;
		_safety = safety;
		_sqlGenerator = sqlGenerator;
	}

	public async Task<AggregationQueryPreparationResult> PrepareAsync(
	AggregationQueryPlan plan,
	CancellationToken cancellationToken = default,
	string? datasetKey = null)
	{
		var schema = await _schemaDiscovery.DiscoverAsync(null, cancellationToken);
		var graph = await _relationships.BuildGraphAsync(
	null,
	cancellationToken,
	datasetKey,
	discoveredSchema: schema);
		var joinPlan = _joinPlanner.Plan(graph, plan.Tables);
		if (!joinPlan.IsValid)
		{
			return new AggregationQueryPreparationResult
			{
				Failure = AggregationQueryPreparationFailure.JoinPlan,
				JoinPlan = joinPlan,
				Errors = joinPlan.Errors
			};
		}

		var validationPlan = new AggregationQueryPlan
		{
			Tables = joinPlan.Tables,
			Joins = joinPlan.Joins.Select(join => new AggregationJoin
			{
				LeftAlias = join.LeftAlias,
				RightAlias = join.RightAlias,
				ConstraintName = join.ConstraintName
			}).ToList(),
			Dimensions = plan.Dimensions,
			Measures = plan.Measures,
			Filters = plan.Filters,
			HavingFilters = plan.HavingFilters,
			Sort = plan.Sort,
			Page = plan.Page,
			PageSize = plan.PageSize
		};

		var errors = _validator.Validate(validationPlan, schema);
		if (errors.Count > 0)
		{
			return new AggregationQueryPreparationResult
			{
				Failure = AggregationQueryPreparationFailure.Validation,
				JoinPlan = joinPlan,
				Errors = errors
			};
		}

		var safety = _safety.Analyze(graph, validationPlan);
		if (!safety.IsSafe)
		{
			return new AggregationQueryPreparationResult
			{
				Failure = AggregationQueryPreparationFailure.Safety,
				JoinPlan = joinPlan,
				Safety = safety
			};
		}

		return new AggregationQueryPreparationResult
		{
			JoinPlan = joinPlan,
			Safety = safety,
			Generated = _sqlGenerator.Generate(plan, joinPlan, schema)
		};
	}
}
