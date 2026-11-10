
using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DashBoard.Services;

public sealed class PdfExportService
{
	public byte[] GeneratePdf(
		string title,
		IReadOnlyList<string> columns,
		IReadOnlyList<Dictionary<string, object?>> rows)
	{
		ArgumentNullException.ThrowIfNull(columns);
		ArgumentNullException.ThrowIfNull(rows);

		var document = Document.Create(container =>
		{
			container.Page(page =>
			{
				page.Size(PageSizes.A4.Landscape());
				page.Margin(24);
				page.DefaultTextStyle(x => x.FontSize(8));

				page.Header().Column(header =>
				{
					header.Item()
						.Text(title)
						.FontSize(17)
						.Bold()
						.FontColor(Colors.Blue.Darken3);

					header.Item()
						.PaddingTop(3)
						.Text($"Generated: {DateTime.Now:dd MMM yyyy HH:mm}")
						.FontSize(8)
						.FontColor(Colors.Grey.Darken1);

					header.Item()
						.PaddingTop(6)
						.Text($"{rows.Count:N0} records")
						.SemiBold();
				});

				page.Content().PaddingTop(12).Column(content =>
				{
					if (columns.Count == 0)
					{
						content.Item().Text("No columns available.");
						return;
					}

					if (rows.Count == 0)
					{
						content.Item().Text("No records available.");
						return;
					}

					content.Item().Table(table =>
					{
						table.ColumnsDefinition(definition =>
						{
							foreach (var _ in columns)
								definition.RelativeColumn();
						});

						table.Header(header =>
						{
							foreach (var column in columns)
							{
								header.Cell()
									.Background(Colors.Blue.Darken3)
									.Border(0.5f)
									.BorderColor(Colors.Grey.Lighten1)
									.Padding(5)
									.DefaultTextStyle(x =>
										x.FontColor(Colors.White)
										 .Bold()
										 .FontSize(8))
									.Text(column);
							}
						});

						foreach (var row in rows)
						{
							foreach (var column in columns)
							{
								var value = row.TryGetValue(column, out var item)
									? FormatValue(item)
									: string.Empty;

								table.Cell()
									.Border(0.5f)
									.BorderColor(Colors.Grey.Lighten2)
									.Padding(4)
									.Text(value);
							}
						}
					});
				});

				page.Footer()
					.AlignCenter()
					.DefaultTextStyle(x =>
						x.FontSize(8).FontColor(Colors.Grey.Darken1))
					.Text(text =>
					{
						text.Span("Page ");
						text.CurrentPageNumber();
						text.Span(" of ");
						text.TotalPages();
					});
			});
		});

		return document.GeneratePdf();
	}

	private static string FormatValue(object? value)
	{
		if (value is null)
			return string.Empty;

		if (value is DateTime dateTime)
			return dateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

		if (value is IFormattable formattable)
			return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "";

		return value.ToString() ?? "";
	}
}
