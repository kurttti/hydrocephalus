using System.Globalization;
using Hydrocephalus.Desktop.Composition;
using Hydrocephalus.Desktop.Results;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Desktop.Administration;

/// <summary>
/// Состояние установки и журнал аудита, приведённые к тексту для экрана.
///
/// Коды событий переводятся здесь, а не в журнале: в файле остаются коды
/// и псевдонимы, а человекочитаемый текст собирается на уровне представления
/// (ADR 0005). Журнал при этом ничем не дополняется — показано ровно то,
/// что в нём записано.
///
/// Три правила, которые здесь соблюдаются намеренно.
///
/// **Цепочка проверяется, и об этом сказано.** Показать записи, не сказав,
/// сходится ли цепочка, значит выдать подделанный журнал за настоящий —
/// именно то, от чего цепочка защищает.
///
/// **У целостности названа её граница.** Целая цепочка не означает полного
/// журнала: у того, кто может писать в файл, остаётся возможность удалить
/// хвост, и остаток будет корректен. Поэтому рядом с «цепочка цела» стоит
/// хеш последней записи и сказано, что сравнивать его нужно с якорем,
/// сохранённым вне этого файла. Без этой оговорки экран делал бы заявление,
/// которого проверка не делает.
///
/// **Непроверенное не выдаётся за целое.** Записи после разрыва показаны
/// отдельным состоянием: опорный хеш утрачен, и сказать о них нечего
/// ни в одну сторону.
/// </summary>
public static class AdministrationReadout
{
    /// <summary>
    /// Сколько записей журнала показывается.
    ///
    /// Журнал не ротируется и растёт всю жизнь установки. Экран, строящий
    /// строку на каждую запись, перестанет открываться раньше, чем журнал
    /// станет неудобным; скрытые записи названы числом, а не молча опущены.
    /// </summary>
    public const int MaxShownRecords = 500;

    /// <summary>
    /// Описывает состояние установки и журнал.
    /// </summary>
    /// <param name="snapshot">Состояние установки.</param>
    /// <returns>Разделы экрана администрирования.</returns>
    public static IReadOnlyList<ResultSection> Describe(AdministrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return
        [
            new ResultSection { Title = "Целостность журнала", Rows = DescribeIntegrity(snapshot.Journal) },
            new ResultSection { Title = "Установка", Rows = DescribeInstallation(snapshot) },
            new ResultSection { Title = "Хранение", Rows = DescribePaths(snapshot) },
            new ResultSection { Title = "События", Rows = DescribeRecords(snapshot.Journal) },
        ];
    }

    /// <summary>
    /// Описывает одну запись журнала.
    /// </summary>
    /// <param name="record">Запись.</param>
    /// <returns>Строка для показа.</returns>
    public static ResultRow DescribeRecord(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var severity = record.Integrity switch
        {
            AuditRecordIntegrity.Broken => ResultSeverity.Blocking,
            AuditRecordIntegrity.Unverifiable => ResultSeverity.Warning,
            _ => ResultSeverity.Neutral,
        };

        if (!record.IsReadable)
        {
            // Нечитаемая запись остаётся на экране: пропустить её значило бы
            // показать журнал, в котором её нет, — то есть скрыть то самое
            // место, где что-то не так.
            return new ResultRow
            {
                Text = Position(record) + " запись не читается",
                Note = NoteFor(record.Integrity),
                Severity = ResultSeverity.Blocking,
            };
        }

        var text = Position(record) + Moment(record.OccurredAt) + " — " + NameOf(record.Code);

        if (record.ReportExportVariant is { } variant)
        {
            text += " (" + NameOf(variant) + ")";
        }

        if (record.Retention is { } retention)
        {
            text += ": " + DescribeSweep(retention);
        }

        if (!string.IsNullOrWhiteSpace(record.PseudonymousStudyId))
        {
            text += " · исследование " + record.PseudonymousStudyId;
        }

        if (!string.IsNullOrWhiteSpace(record.PseudonymousActorId))
        {
            text += " · инициатор " + record.PseudonymousActorId;
        }

        if (!string.IsNullOrWhiteSpace(record.ModelVersion))
        {
            text += " · модель " + record.ModelVersion;
        }

        return new ResultRow { Text = text, Note = NoteFor(record.Integrity), Severity = severity };
    }

    private static List<ResultRow> DescribeIntegrity(AuditJournal journal)
    {
        var rows = new List<ResultRow>();

        var broken = journal.Records.FirstOrDefault(
            record => record.Integrity == AuditRecordIntegrity.Broken);

        rows.Add(journal.IsIntact
            ? new ResultRow
            {
                Text = "Цепочка хешей сходится на всех записях: "
                    + Count(journal.Records.Count) + ".",
                Severity = ResultSeverity.Neutral,
            }
            : new ResultRow
            {
                Text = "Цепочка хешей разошлась на записи №"
                    + (broken?.Number ?? 0).ToString(CultureInfo.CurrentCulture)
                    + ".",
                Note = "Записи журнала изменены или удалены после того, как были сделаны. "
                    + "Всё, что идёт после разрыва, проверить нечем.",
                Severity = ResultSeverity.Blocking,
            });

        rows.Add(new ResultRow
        {
            Text = "Хеш последней записи: " + journal.LatestHash,

            // Граница защиты названа прямо: без внешнего якоря «цепочка цела»
            // не означает «журнал полон».
            Note = "Целая цепочка не означает полного журнала: удаление хвоста "
                + "оставляет остаток корректным. Обнаружить это можно только "
                + "сравнением этого хеша с записанным вне журнала.",
            Severity = ResultSeverity.Warning,
        });

        if (journal.TailIsIncomplete)
        {
            rows.Add(new ResultRow
            {
                Text = "Последняя строка файла не дописана.",
                Note = "Журнал дописывается в конец, и чтение застало запись незавершённой. "
                    + "Это не разрыв цепочки.",
                Severity = ResultSeverity.Neutral,
            });
        }

        return rows;
    }

    private static List<ResultRow> DescribeInstallation(AdministrationSnapshot snapshot) =>
    [
        new ResultRow
        {
            Text = RetentionSettings.Describe(snapshot.Retention),
            Severity = ResultSeverity.Neutral,
        },
        new ResultRow
        {
            Text = snapshot.PatientRegistryConfigured
                ? "Реестр идентификаторов пациента подключён."
                : "Реестр идентификаторов пациента не подключён: клинический вариант отчёта недоступен.",
            Severity = snapshot.PatientRegistryConfigured
                ? ResultSeverity.Neutral
                : ResultSeverity.Warning,
        },
        new ResultRow
        {
            // Отсутствие пакета модели — состояние сборки, а не поломка
            // установки: проверенного пакета пока нет (ADR 0004).
            Text = "Пакет модели не подключён: вероятность диагноза не выдаётся.",
            Severity = ResultSeverity.Warning,
        },
        new ResultRow
        {
            Text = "Сборка приложения: " + snapshot.Pipeline.ApplicationCommitSha,
            Note = DescribePipelineGaps(snapshot.Pipeline),
            Severity = ResultSeverity.Neutral,
        },
        new ResultRow
        {
            // Диагностики ускорителя нет, потому что нет инференса. Пустой
            // раздел «GPU» выглядел бы как исправное отсутствие устройства.
            Text = "Диагностика ускорителя не показывается: локального инференса в этой сборке нет.",
            Severity = ResultSeverity.Neutral,
        },
    ];

    private static List<ResultRow> DescribePaths(AdministrationSnapshot snapshot) =>
    [
        Place("Рабочие копии", snapshot.WorkingCopyRoot),
        Place("Отчёты", snapshot.ReportRoot),
        Place("Экспортированные отчёты", snapshot.ReportExportRoot),
        Place("Манифесты датасета", snapshot.DatasetManifestRoot),
        Place("Журнал аудита", snapshot.AuditLogPath),
    ];

    private static List<ResultRow> DescribeRecords(AuditJournal journal)
    {
        if (journal.Records.Count == 0)
        {
            // «Событий не было» и «журнал не прочитан» выглядели бы одинаково
            // при пустом списке, а читаются по-разному.
            return
            [
                new ResultRow
                {
                    Text = "Событий в журнале нет.",
                    Severity = ResultSeverity.Neutral,
                },
            ];
        }

        var rows = new List<ResultRow>();

        // Сначала последние: журнал растёт всю жизнь установки, и то, что
        // ищут, почти всегда произошло недавно.
        var shown = journal.Records.Reverse().Take(MaxShownRecords).ToList();

        if (journal.Records.Count > shown.Count)
        {
            rows.Add(new ResultRow
            {
                Text = "Показаны последние " + Count(shown.Count) + " из "
                    + Count(journal.Records.Count) + ".",
                Note = "Остальные остаются в файле журнала.",
                Severity = ResultSeverity.Neutral,
            });
        }

        rows.AddRange(shown.Select(DescribeRecord));

        return rows;
    }

    private static ResultRow Place(string what, string path) => new()
    {
        Text = what + ": " + path,
        Severity = ResultSeverity.Neutral,
    };

    private static string? DescribePipelineGaps(PipelineIdentity pipeline)
    {
        var missing = new List<string>();

        if (string.Equals(
            pipeline.PreprocessingVersion,
            PipelineIdentity.NotImplementedVersion,
            StringComparison.Ordinal))
        {
            missing.Add("предобработка");
        }

        if (string.Equals(
            pipeline.FeatureSchemaVersion,
            PipelineIdentity.NotImplementedVersion,
            StringComparison.Ordinal))
        {
            missing.Add("схема признаков");
        }

        if (string.Equals(
            pipeline.LabelMapVersion,
            PipelineIdentity.NotImplementedVersion,
            StringComparison.Ordinal))
        {
            missing.Add("label map");
        }

        // Этапы отмечены как нереализованные, а не проставлены версией:
        // номер в таком поле — заявка на воспроизводимость, которой нет.
        return missing.Count == 0
            ? null
            : "Не реализовано в этой сборке: " + string.Join(", ", missing) + ".";
    }

    private static string DescribeSweep(WorkingCopyRetentionOutcome retention)
    {
        var parts = new List<string>
        {
            "по сроку " + Count(retention.Expired),
            "за прерванными сеансами " + Count(retention.Orphaned),
        };

        if (retention.Failed > 0)
        {
            // «Данные пациента должны были исчезнуть и не исчезли» — ровно то,
            // ради чего журнал ведётся, и в строке это стоит последним,
            // потому что читается как итог.
            parts.Add("не удалось удалить " + Count(retention.Failed));
        }

        return string.Join(", ", parts);
    }

    private static string? NoteFor(AuditRecordIntegrity integrity) => integrity switch
    {
        AuditRecordIntegrity.Broken => "Цепочка разошлась на этой записи.",
        AuditRecordIntegrity.Unverifiable => "Запись идёт после разрыва: проверить её нечем.",
        _ => null,
    };

    private static string Position(AuditRecord record) =>
        "№" + record.Number.ToString(CultureInfo.CurrentCulture) + " ";

    private static string Moment(DateTimeOffset? moment) =>
        moment is { } value
            ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "время не прочитано";

    private static string Count(int value) =>
        value.ToString(CultureInfo.CurrentCulture);

    private static string NameOf(ReportExportVariant variant) => variant switch
    {
        ReportExportVariant.Deidentified => "обезличенный",
        ReportExportVariant.Clinical => "клинический",
        _ => variant.ToString(),
    };

    private static string NameOf(AuditEventCode code) => code switch
    {
        AuditEventCode.StudyImported => "Исследование импортировано",
        AuditEventCode.QualityControlCompleted => "Входной контроль качества завершён",
        AuditEventCode.AnalysisStarted => "Анализ запущен",
        AuditEventCode.AnalysisCompleted => "Анализ завершён",
        AuditEventCode.AnalysisRefused => "Система отказалась от ответа",
        AuditEventCode.AnalysisCancelled => "Анализ отменён",
        AuditEventCode.AnalysisFailed => "Анализ завершился ошибкой",
        AuditEventCode.ReportStored => "Отчёт сохранён",
        AuditEventCode.DatasetManifestExported => "Манифест датасета экспортирован",
        AuditEventCode.AccessDenied => "Операция отклонена по правам доступа",
        AuditEventCode.ReportExported => "Отчёт экспортирован",
        AuditEventCode.WorkingCopiesSwept => "Уборка рабочих копий",
        AuditEventCode.ReportPreviewed => "Отчёт показан перед экспортом",

        // Код, которого нет в этой версии приложения, читается как неизвестный,
        // а не пропускается: запись, сделанная более новой версией, должна
        // остаться видимой, пусть и без перевода.
        AuditEventCode.Unspecified => "Событие неизвестного кода",

        // Код, добавленный в перечисление и забытый здесь, не должен
        // притвориться одним из известных.
        _ => "Событие " + code.ToString(),
    };
}
