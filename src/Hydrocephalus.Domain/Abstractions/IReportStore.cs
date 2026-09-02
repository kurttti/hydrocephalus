using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Хранилище канонических отчётов (JSON-слой из ADR 0005).
/// Сохранённый отчёт неизменен: смена версии модели не пересчитывает прошлые отчёты.
/// </summary>
public interface IReportStore
{
    /// <summary>
    /// Сохраняет отчёт.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача сохранения.</returns>
    Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken);
}
