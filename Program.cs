using DashBoard;
using DashBoard.Catalog;
using DashBoard.Services;
using DashBoard.Repositories;

var builder = WebApplication.CreateBuilder(args);

// Adding services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddScoped<DepartmentRepository>();
builder.Services.AddScoped<DashboardLayoutRepository>();
builder.Services.AddScoped<DashboardRepository>();


builder.Services.AddMemoryCache();
builder.Services.AddScoped<DatasetMetadataProvider>();
builder.Services.AddScoped<DashboardDrilldownService>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddScoped<DashboardOverviewService>();
builder.Services.AddScoped<DatabaseSchemaDiscoveryService>();
builder.Services.AddScoped<DatabaseRelationshipService>();
builder.Services.AddScoped<DatabaseAggregationSafetyService>();
builder.Services.AddScoped<AggregationQueryValidator>();
builder.Services.AddScoped<AggregationSqlGenerator>();
builder.Services.AddScoped<AggregationQueryPreparationService>();
builder.Services.AddScoped<AutomaticJoinPlannerService>();
builder.Services.AddScoped<AggregationQueryExecutionService>();
builder.Services.AddScoped<DashboardConfigurationService>();
builder.Services.AddScoped<QueryBuilderService>();
builder.Services.AddScoped<DashboardWidgetQueryService>();
builder.Services.AddScoped<DashboardQueryService>();
builder.Services.AddScoped<PdfExportService>();
builder.Services.AddScoped<UIConfigurationService>();


builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
	options.IdleTimeout = TimeSpan.FromHours(2);
	options.Cookie.HttpOnly = true;
	options.Cookie.IsEssential = true;
});

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var app = builder.Build();



// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();
app.UseSession();


app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapControllers();

app.Run();
