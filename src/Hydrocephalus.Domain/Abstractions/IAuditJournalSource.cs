namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Состояние записи журнала относительно цепочки хешей.
///
/// Состояний три, а не два. «Запись изменена» и «о записи ничего нельзя
/// сказать» — разные утверждения: после первого разрыва опорный хеш потерян,
/// и всё, что идёт дальше, не проверено ни в одну сторону. Свести их к одному
/// признаку значило бы либо объявить изменёнными записи, которых никто
/// не трогал, либо показать изменённую запись наравне с проверенными.
/// </summary>
public enum AuditRecordIntegrity
{
    /// <summary>Цепочка сходится на этой записи.</summary>
    Verified = 0,

    /// <summary>Цепочка разошлась именно здесь: запись изменена или удалена соседняя.</summary>
    Broken = 1,

    /// <summary>
    /// Запись идёт после разрыва, и проверить её нечем: опорный хеш утрачен.
    /// </summary>
    Unverifiable = 2,
}

/// <summary>
/// Прочитанная запись журнала аудита.
///
/// Отдельный тип от <see cref="AuditEvent"/>: событие описывает то, что
/// произошло, запись — то, что лежит в файле, вместе с её положением
/// и состоянием цепочки. Нечитаемая строка тоже запись: пропустить её значило бы
/// показать журнал, в котором её нет.
/// </summary>
public sealed record AuditRecord
{
    /// <summary>Порядковый номер записи в журнале, начиная с единицы.</summary>
    public required int Number { get; init; }

    /// <summary>Состояние записи относительно цепочки.</summary>
    public required AuditRecordIntegrity Integrity { get; init; }

    /// <summary>
    /// Удалось ли разобрать содержимое записи.
    ///
    /// Нечитаемая запись не то же самое, что разорванная цепочка: строка может
    /// быть испорчена так, что от неё не осталось ни кода, ни времени.
    /// </summary>
    public required bool IsReadable { get; init; }

    /// <summary>Код события; <see cref="AuditEventCode.Unspecified"/> у нечитаемой записи.</summary>
    public AuditEventCode Code { get; init; }

    /// <summary>Момент события, если он прочитан.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>Псевдонимный идентификатор исследования.</summary>
    public string? PseudonymousStudyId { get; init; }

    /// <summary>Версия model package.</summary>
    public string? ModelVersion { get; init; }

    /// <summary>Псевдонимный идентификатор инициатора.</summary>
    public string? PseudonymousActorId { get; init; }

    /// <summary>Вариант экспорта отчёта.</summary>
    public Reporting.ReportExportVariant? ReportExportVariant { get; init; }

    /// <summary>Итог уборки рабочих копий.</summary>
    public WorkingCopyRetentionOutcome? Retention { get; init; }
}

/// <summary>
/// Журнал аудита, прочитанный целиком вместе с проверкой цепочки.
///
/// Записи и проверка приходят вместе, а не по отдельности: показать журнал,
/// не сказав, сходится ли цепочка, значит выдать подделанный журнал за
/// настоящий — а именно от этого цепочка и защищает.
/// </summary>
public sealed record AuditJournal
{
    /// <summary>Записи в порядке их появления в файле.</summary>
    public required IReadOnlyList<AuditRecord> Records { get; init; }

    /// <summary>Сходится ли цепочка на всём журнале.</summary>
    public required bool IsIntact { get; init; }

    /// <summary>
    /// Хеш последней проверенной записи.
    ///
    /// Целостность цепочки не означает полноты журнала: у того, кто может
    /// писать в файл, остаётся возможность удалить хвост, и оставшаяся цепочка
    /// будет корректной. Обнаружить это можно только сравнением с якорем,
    /// сохранённым вне файла, и для этого хеш отдаётся наружу.
    /// </summary>
    public required string LatestHash { get; init; }

    /// <summary>
    /// Оборвана ли последняя строка файла.
    ///
    /// Журнал дописывается в конец, и чтение может застать запись незавершённой.
    /// Это не разрыв цепочки и объявлять его подделкой нельзя: следующее чтение
    /// увидит строку целой.
    /// </summary>
    public required bool TailIsIncomplete { get; init; }
}

/// <summary>
/// Чтение журнала аудита.
///
/// Порт отдельно от <see cref="IAuditLog"/>: писать в журнал должны все
/// сценарии, читать его — право обслуживания установки, и объединение
/// в один интерфейс заставило бы каждого писателя иметь и доступ на чтение.
/// </summary>
public interface IAuditJournalSource
{
    /// <summary>
    /// Читает журнал и проверяет цепочку.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Записи вместе с результатом проверки.</returns>
    Task<AuditJournal> ReadAsync(CancellationToken cancellationToken);
}
