namespace Hydrocephalus.Domain.Reporting;

/// <summary>
/// Ручной комментарий или исправление врача. Хранится отдельно от вывода модели
/// и никогда не смешивается с ним: отчёт обязан различать исходные данные,
/// вычисленный результат и ручной комментарий (docs/security/README.md).
/// </summary>
public sealed record ClinicianAnnotation
{
    /// <summary>Псевдонимный идентификатор автора комментария.</summary>
    public required string PseudonymousAuthorId { get; init; }

    /// <summary>Момент создания комментария.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Текст комментария. Свободный ввод может содержать введённую вручную PHI,
    /// поэтому при обезличенном экспорте требует отдельной обработки (ADR 0005).
    /// </summary>
    public required string Text { get; init; }
}
