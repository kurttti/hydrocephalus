using System.Globalization;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Хранилище канонических отчётов в файлах (ADR 0005).
///
/// Сохранённый отчёт неизменен: смена версии модели не пересчитывает прошлые
/// отчёты. Неизменность обеспечивается способом записи, а не соглашением —
/// файл создаётся только если его ещё нет, и повторная запись по тому же пути
/// не проходит молча.
/// </summary>
public sealed class JsonReportStore : IReportStore
{
    private readonly string rootDirectory;

    /// <summary>Создаёт хранилище.</summary>
    /// <param name="rootDirectory">Каталог, в котором складываются отчёты.</param>
    public JsonReportStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Сохраняет отчёт.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача сохранения.</returns>
    /// <exception cref="IOException">Если отчёт по этому пути уже существует.</exception>
    public async Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var path = this.PathFor(report);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // CreateNew, а не Create: перезапись сохранённого отчёта — это потеря
        // того, что уже могло уйти врачу, и она должна быть ошибкой, а не
        // незаметным следствием повторного запуска.
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        await stream.WriteAsync(CanonicalReportJson.Serialize(report), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Строит путь файла отчёта.
    ///
    /// Имя складывается из псевдонима исследования и момента формирования:
    /// у одного исследования может быть несколько разборов, и затирать прошлый
    /// нельзя. Обе части непрозрачны и PHI не содержат.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <returns>Полный путь файла.</returns>
    public string PathFor(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var stamp = report.CreatedAt
            .ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);

        return Path.Combine(
            this.rootDirectory,
            report.PseudonymousStudyId,
            stamp + ".json");
    }
}
