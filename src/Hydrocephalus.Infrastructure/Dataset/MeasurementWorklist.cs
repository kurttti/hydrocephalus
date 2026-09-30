using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Dataset;

/// <summary>Исследование набора вместе с тем, измерено ли оно.</summary>
/// <param name="Study">Запись манифеста.</param>
/// <param name="Directory">Каталог исследования внутри набора.</param>
/// <param name="Measurement">Ручное измерение либо null, если его нет.</param>
public readonly record struct QueuedStudy(
    DatasetStudy Study,
    string Directory,
    ManualMeasurement? Measurement)
{
    /// <summary>Измерено ли исследование.</summary>
    public bool IsMeasured => this.Measurement is not null;
}

/// <summary>
/// Рабочий список исследований набора для ручного измерения.
///
/// Нужна потому, что измерений предстоит около полусотни, а выбор каталога в
/// диалоге на каждое — и морока, и источник ошибок: по двум сотням папок
/// невозможно помнить, какие уже сделаны.
///
/// **Состояния очередь не хранит.** «Следующее» — это первое неизмеренное
/// исследование в порядке манифеста, а измеренность выводится из хранилища
/// отчётов. Файл с отметкой о том, где остановились, разошёлся бы с
/// действительностью при первом же измерении с оригинала, при откате отчёта или
/// при работе с другой машины; выведенное состояние не расходится никогда и
/// само себя исправляет.
///
/// Порядок — тот, в котором исследования лежат в манифесте, то есть порядок
/// сборки набора: устойчивый между запусками и не зависящий от файловой системы.
/// </summary>
public sealed class MeasurementWorklist
{
    private readonly List<QueuedStudy> studies;

    private MeasurementWorklist(string root, List<QueuedStudy> studies)
    {
        this.Root = root;
        this.studies = studies;
    }

    /// <summary>Корень набора.</summary>
    public string Root { get; }

    /// <summary>Все исследования списка в порядке манифеста.</summary>
    public IReadOnlyList<QueuedStudy> Studies => this.studies;

    /// <summary>Сколько исследований уже измерено.</summary>
    public int MeasuredCount => this.studies.Count(study => study.IsMeasured);

    /// <summary>
    /// Собирает список по набору и хранилищу отчётов.
    /// </summary>
    /// <param name="datasetRoot">Корень набора.</param>
    /// <param name="reportRoot">Корень хранилища отчётов.</param>
    /// <param name="group">Группа, которой ограничиться; null — все.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Список либо <see langword="null"/>, если манифеста нет.</returns>
    public static async Task<MeasurementWorklist?> OpenAsync(
        string datasetRoot,
        string reportRoot,
        string? group,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRoot);

        var manifest = await DatasetManifest.ReadAsync(datasetRoot, cancellationToken)
            .ConfigureAwait(false);

        if (manifest is null)
        {
            return null;
        }

        var measured = await ManualMeasurementIndex.ReadAsync(reportRoot, cancellationToken)
            .ConfigureAwait(false);

        var studies = manifest.Studies
            .Where(study => group is null
                || string.Equals(study.Group, group, StringComparison.Ordinal))
            .Select(study => new QueuedStudy(
                study,
                study.DirectoryIn(datasetRoot),
                Measurement(measured, study)))
            .ToList();

        return new MeasurementWorklist(datasetRoot, studies);
    }

    /// <summary>Группы, представленные в наборе, в порядке первого появления.</summary>
    /// <param name="manifest">Манифест набора.</param>
    /// <returns>Имена групп.</returns>
    public static IReadOnlyList<string> GroupsOf(DatasetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return [.. manifest.Studies.Select(study => study.Group).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Находит следующее неизмеренное исследование.
    /// </summary>
    /// <param name="afterDirectory">
    /// Каталог, после которого искать; null — искать с начала. Открытое сейчас
    /// исследование пропускается даже неизмеренным: иначе «следующее» вернуло бы
    /// то же самое, пока врач не запишет измерение.
    /// </param>
    /// <returns>Исследование либо <see langword="null"/>, если неизмеренных нет.</returns>
    public QueuedStudy? NextUnmeasured(string? afterDirectory)
    {
        var start = 0;

        if (afterDirectory is not null)
        {
            var current = this.studies.FindIndex(study => string.Equals(
                study.Directory,
                afterDirectory,
                StringComparison.OrdinalIgnoreCase));

            if (current >= 0)
            {
                start = current + 1;
            }
        }

        // Круг замыкается: после последнего поиск продолжается с начала, потому
        // что неизмеренные могли остаться позади — врач мог пропустить одно.
        for (var step = 0; step < this.studies.Count; step++)
        {
            var study = this.studies[(start + step) % this.studies.Count];

            if (!study.IsMeasured)
            {
                return study;
            }
        }

        return null;
    }

    private static ManualMeasurement? Measurement(MeasurementReadout readout, DatasetStudy study)
    {
        // Псевдонимов у исследования два: измерение с оригинала лежит под
        // исходным, из набора — под производным.
        foreach (var key in study.ReportKeys())
        {
            if (readout.Manual.TryGetValue(key, out var measurement))
            {
                return measurement;
            }
        }

        return null;
    }
}
