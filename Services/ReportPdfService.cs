using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using OpenCredential.AdminWeb.Services;

namespace OpenCredential.AdminWeb;

public static class ReportPdfService
{
    public static byte[] BuildReportPdf(ReportsResponse reports, ReportPdfContext context)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(TextStyle.Default.FontSize(9));
                page.PageColor(Colors.White);

                page.Header().Column(column =>
                {
                    column.Spacing(8);
                    column.Item().Background("#13233F").Padding(14).Row(row =>
                    {
                        row.RelativeItem().Column(header =>
                        {
                            header.Spacing(2);
                            header.Item().Text("OPENCREDENTIAL").FontSize(9).SemiBold().FontColor("#D7E5FF");
                            header.Item().Text("Informe operativo y academico").FontSize(22).Bold().FontColor(Colors.White);
                            header.Item().Text("Consolidado institucional de uso, sesiones y actividad de equipos.").FontSize(9).FontColor("#D7E5FF");
                        });

                        row.ConstantItem(250).AlignRight().Column(meta =>
                        {
                            meta.Spacing(4);
                            meta.Item().AlignRight().Text($"Generado: {context.GeneratedAtUtc.ToLocalTime():dd/MM/yyyy hh:mm tt}")
                                .FontSize(9).FontColor(Colors.White);
                            meta.Item().AlignRight().Text($"Rango: {context.FromUtc.ToLocalTime():dd/MM/yyyy} - {context.ToUtc.ToLocalTime():dd/MM/yyyy}")
                                .FontSize(9).FontColor("#D7E5FF");
                            meta.Item().AlignRight().Text("Destino: Administracion / Reportes")
                                .FontSize(8).FontColor("#AFC8F7");
                        });
                    });
                });

                page.Content().PaddingVertical(12).Column(column =>
                {
                    column.Spacing(14);

                    if (context.Filters.Count > 0)
                    {
                        column.Item().Element(container => BuildFilterBlock(container, context.Filters));
                    }

                    column.Item().Element(container => BuildKpiGrid(container, reports.Kpis));

                    if (reports.TopUsers.Count > 0)
                    {
                        column.Item().Element(container => BuildMetricTable(
                            container,
                            "Top usuarios",
                            "Usuario",
                            reports.TopUsers.Select(item => new[] { item.Label, item.SecondaryLabel ?? "Sin usuario identificado", item.Hours.ToString("0.0"), item.Sessions.ToString() })));
                    }

                    if (reports.TopEquipment.Count > 0)
                    {
                        column.Item().Element(container => BuildMetricTable(
                            container,
                            "Top equipos",
                            "Equipo",
                            reports.TopEquipment.Select(item => new[] { item.Label, item.SecondaryLabel ?? "Sin inventario", item.Hours.ToString("0.0"), item.Sessions.ToString() })));
                    }

                    column.Item().Element(container => BuildSessionsTable(container, reports.Sessions.Take(50).ToList()));
                });

                page.Footer()
                    .BorderTop(1)
                    .BorderColor("#D8E3F4")
                    .PaddingTop(6)
                    .Row(row =>
                    {
                        row.RelativeItem().Text("OpenCredential AdminWeb · Informe exportado en PDF")
                            .FontSize(8)
                            .FontColor("#5F7397");

                        row.ConstantItem(120).AlignRight().DefaultTextStyle(TextStyle.Default.FontSize(8).FontColor("#5F7397")).Text(text =>
                        {
                            text.Span("Pagina ");
                            text.CurrentPageNumber();
                            text.Span(" de ");
                            text.TotalPages();
                        });
                    });
            });
        }).GeneratePdf();
    }

    private static void BuildFilterBlock(IContainer container, IReadOnlyDictionary<string, string> filters)
    {
        container.Border(1).BorderColor("#D8E3F4").Background("#FAFCFF").Padding(10).Column(column =>
        {
            column.Spacing(6);
            column.Item().Text("Filtros aplicados").SemiBold().FontColor("#13233F");

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                });

                foreach (var filter in filters.Where(item => !string.IsNullOrWhiteSpace(item.Value)))
                {
                    table.Cell().PaddingBottom(4).Text(text =>
                    {
                        text.Span($"{filter.Key}: ").SemiBold();
                        text.Span(filter.Value);
                    });
                }
            });
        });
    }

    private static void BuildKpiGrid(IContainer container, ReportKpis kpis)
    {
        var cards = new[]
        {
            ("Sesiones", kpis.SessionCount.ToString()),
            ("Horas", kpis.TotalHours.ToString("0.0")),
            ("Usuarios unicos", kpis.UniqueUsers.ToString()),
            ("Programas", kpis.ActivePrograms.ToString()),
            ("Salas", kpis.ActiveRooms.ToString()),
            ("Offline recuperadas", kpis.OfflineRecoveredSessions.ToString()),
            ("Reemplazadas", kpis.SupersededSessions.ToString()),
            ("Heartbeat timeout", kpis.HeartbeatTimeoutSessions.ToString()),
            ("Apagados inesperados", kpis.UnexpectedShutdownSessions.ToString())
        };

        container.Column(column =>
        {
            column.Spacing(6);
            column.Item().Text("Resumen ejecutivo").SemiBold().FontColor("#13233F");
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                });

                foreach (var (label, value) in cards)
                {
                    table.Cell().Border(1).BorderColor("#D8E3F4").Background("#F8FBFF").Padding(10).Column(card =>
                    {
                        card.Item().Text(label).FontSize(8).FontColor("#5F7397");
                        card.Item().PaddingTop(4).Text(value).FontSize(18).Bold().FontColor("#13233F");
                    });
                }
            });
        });
    }

    private static void BuildMetricTable(IContainer container, string title, string firstColumnTitle, IEnumerable<string[]> rows)
    {
        container.Column(column =>
        {
            column.Spacing(6);
            column.Item().Background("#F7FAFF").Padding(8).Text(title).SemiBold().FontColor("#13233F");
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(1);
                });

                table.Header(header =>
                {
                    HeaderCell(header.Cell(), firstColumnTitle);
                    HeaderCell(header.Cell(), "Detalle");
                    HeaderCell(header.Cell(), "Horas");
                    HeaderCell(header.Cell(), "Sesiones");
                });

                foreach (var row in rows)
                {
                    BodyCell(table.Cell(), row.ElementAtOrDefault(0));
                    BodyCell(table.Cell(), row.ElementAtOrDefault(1));
                    BodyCell(table.Cell(), row.ElementAtOrDefault(2));
                    BodyCell(table.Cell(), row.ElementAtOrDefault(3));
                }
            });
        });
    }

    private static void BuildSessionsTable(IContainer container, IReadOnlyList<ReportSessionRow> sessions)
    {
        container.Column(column =>
        {
            column.Spacing(6);
            column.Item().Background("#F7FAFF").Padding(8).Text("Sesiones recientes filtradas").SemiBold().FontColor("#13233F");
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1.4f);
                    columns.RelativeColumn(1.2f);
                    columns.RelativeColumn(1.4f);
                    columns.RelativeColumn(1.3f);
                    columns.RelativeColumn(1.1f);
                    columns.RelativeColumn(1.1f);
                    columns.RelativeColumn(0.8f);
                });

                table.Header(header =>
                {
                    HeaderCell(header.Cell(), "Inicio");
                    HeaderCell(header.Cell(), "Usuario");
                    HeaderCell(header.Cell(), "Equipo");
                    HeaderCell(header.Cell(), "Sala");
                    HeaderCell(header.Cell(), "Modo de acceso");
                    HeaderCell(header.Cell(), "Estado");
                    HeaderCell(header.Cell(), "Horas");
                });

                foreach (var item in sessions)
                {
                    BodyCell(table.Cell(), item.LoginStamp.ToLocalTime().ToString("dd/MM/yyyy hh:mm tt"));
                    BodyCell(table.Cell(), BuildSessionIdentity(item));
                    BodyCell(table.Cell(), item.Machine);
                    BodyCell(table.Cell(), item.RoomName ?? "Sin sala");
                    BodyCell(table.Cell(), RepositorySupport.TranslateSessionOrigin(item.SessionOrigin));
                    BodyCell(table.Cell(), item.OperationalStatusLabel ?? item.OperationalStatus ?? "Disponible");
                    BodyCell(table.Cell(), item.DurationHours.ToString("0.00"));
                }
            });
        });
    }

    private static string BuildSessionIdentity(ReportSessionRow item)
    {
        var username = string.IsNullOrWhiteSpace(item.Username) ? "Sin usuario identificado" : item.Username.Trim();
        var detail = string.IsNullOrWhiteSpace(item.FullName)
            ? string.IsNullOrWhiteSpace(item.DocumentId) ? null : item.DocumentId.Trim()
            : item.FullName.Trim();

        return string.IsNullOrWhiteSpace(detail) || detail.Equals(username, StringComparison.OrdinalIgnoreCase)
            ? username
            : $"{username} - {detail}";
    }

    private static void HeaderCell(IContainer cell, string text)
    {
        cell
            .Background("#EEF3FB")
            .BorderBottom(1)
            .BorderColor("#D8E3F4")
            .Padding(6)
            .Text(text)
            .FontSize(8)
            .SemiBold()
            .FontColor("#5F7397");
    }

    private static void BodyCell(IContainer cell, string? text)
    {
        cell
            .BorderBottom(1)
            .BorderColor("#E5ECF7")
            .Padding(6)
            .Text(text ?? string.Empty)
            .FontSize(8.5f)
            .FontColor("#13233F");
    }
}

public sealed class ReportPdfContext
{
    public DateTime GeneratedAtUtc { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public Dictionary<string, string> Filters { get; init; } = [];
}
