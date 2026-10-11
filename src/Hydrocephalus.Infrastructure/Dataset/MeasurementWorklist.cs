using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Dataset;

/// <summary>
/// Какое измерение считать выполненным.
///
/// Очередей две потому, что это два разных обхода набора по разным сериям:
/// индекс Эванса меряется на аксиальном срезе, угол мозолистого тела — на
/// корональном, и наборы пригодных исследований у них разные (корональная серия
/// есть у 55 из 57, аксиальная пригодная — у 36). Одна очередь на оба признака
/// считала бы готовым исследование, у которого сделана половина.
/// </summary>
public enum MeasurementKind
{
    /// <summary>Индекс Эванса, четыре точки на аксиальном срезе.</summary>
    EvansIndex = 0,

    /// <summary>Угол мозолистого тела, три точки на корональном срезе.</summary>
    CallosalAngle = 1,
}

/// <summary>Исследование набора вместе с тем, что по нему измерено.</summary>
/// <param name="Study">Запись манифеста.</param>
/// <param name="Directory">Каталог исследования внутри набора.</param>
/// <param name="Measurement">Ручной индекс Эванса либо null, если его нет.</param>
/// <param name="Angle">Ручной угол мозолистого тела либо null, если его нет.</param>
public readonly record struct QueuedStudy(
    DatasetStudy Study,
    string Directory,
    ManualMeasurement? Measurement,
    ManualAngle? Angle = null)
{
    /// <summary>Измерен ли индекс Эванса.</summary>
    public bool IsMeasured => this.Measurement is not null;

    /// <summary>Измерен ли угол мозолистого тела.</summary>
    public bool HasAngle => this.Angle is not null;

    /// <summary>
    /// Выполнено ли измерение названного вида.
    /// </summary>
    /// <param name="kind">Вид измерения.</param>
    /// <returns><see langword="true"/>, если измерение есть.</returns>
    public bool Has(MeasurementKind kind) => kind switch
    {
        MeasurementKind.EvansIndex => this.IsMeasured,
        MeasurementKind.CallosalAngle => this.HasAngle,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
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

    /// <summary>Сколько исследований уже измерено по индексу Эванса.</summary>
    public int MeasuredCount => this.CountOf(MeasurementKind.EvansIndex);

    /// <summary>
    /// Сколько исследований уже измерено названным видом измерения.
    /// </summary>
    /// <param name="kind">Вид измерения.</param>
    /// <returns>Число исследований с таким измерением.</returns>
    public int CountOf(MeasurementKind kind) => this.studies.Count(study => study.Has(kind));

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
                Find(study, measured.Manual),
                Find(study, measured.CallosalAngle)))
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
    public QueuedStudy? NextUnmeasured(string? afterDirectory) =>
        this.NextUnmeasured(MeasurementKind.EvansIndex, afterDirectory);

    /// <summary>
    /// Находит следующее исследование без измерения названного вида.
    /// </summary>
    /// <param name="kind">Вид измерения.</param>
    /// <param name="afterDirectory">
    /// Каталог, после которого искать; null — искать с начала. Открытое сейчас
    /// исследование пропускается даже неизмеренным: иначе «следующее» вернуло бы
    /// то же самое, пока врач не запишет измерение.
    /// </param>
    /// <returns>Исследование либо <see langword="null"/>, если неизмеренных нет.</returns>
    public QueuedStudy? NextUnmeasured(MeasurementKind kind, string? afterDirectory)
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

            if (!study.Has(kind))
            {
                return study;
            }
        }

        return null;
    }

    private static T? Find<T>(DatasetStudy study, IReadOnlyDictionary<string, T> measured)
        where T : struct
    {
        // Псевдонимов у исследования два: измерение с оригинала лежит под
        // исходным, из набора — под производным.
        foreach (var key in study.ReportKeys())
        {
            if (measured.TryGetValue(key, out var measurement))
            {
                return measurement;
            }
        }

        return null;
    }
}
