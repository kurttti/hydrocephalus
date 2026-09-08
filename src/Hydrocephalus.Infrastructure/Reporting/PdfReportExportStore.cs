using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Хранилище экспорта, выдающее оба слоя отчёта: канонический JSON и PDF (ADR 0005).
///
/// Обёртка, а не замена: JSON пишет вложенное хранилище, и оно же остаётся
/// источником истины. PDF строится **из записанного файла**, а не из объекта
/// в памяти. Разница существенная — так проверяемо, что человек и программа
/// читают одно и то же: если бы PDF рисовался из объекта, а JSON писался
/// отдельно, между ними мог бы образоваться зазор, и заметить его было бы
/// нечем.
///
/// Возвращается путь PDF: его открывает человек. JSON лежит рядом под тем же
/// именем и находится по нему.
/// </summary>
public sealed class PdfReportExportStore : IReportExportStore
{
    /// <summary>Расширение человекочитаемого слоя.</summary>
    public const string PdfExtension = ".pdf";

    private readonly IReportExportStore canonical;

    /// <summary>Создаёт хранилище.</summary>
    /// <param name="canonical">Хранилище канонического слоя.</param>
    public PdfReportExportStore(IReportExportStore canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        this.canonical = canonical;
    }

    /// <summary>
    /// Записывает канонический JSON и порождает из него PDF.
    /// </summary>
    /// <param name="prepared">Подготовленный экспорт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Полный путь записанного PDF.</returns>
    public async Task<string> WriteAsync(ReportExport prepared, CancellationToken cancellationToken)
    {
        var jsonPath = await this.canonical.WriteAsync(prepared, cancellationToken).ConfigureAwait(false);

        var content = await File.ReadAllBytesAsync(jsonPath, cancellationToken).ConfigureAwait(false);

        var pdfPath = Path.ChangeExtension(jsonPath, PdfExtension);

        await File.WriteAllBytesAsync(pdfPath, PdfReportRenderer.Render(content), cancellationToken)
            .ConfigureAwait(false);

        return pdfPath;
    }
}
