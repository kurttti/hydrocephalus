namespace Hydrocephalus.Domain.Quality;

/// <summary>
/// Машинно-читаемый код проблемы качества. Человекочитаемый текст формируется
/// на слое представления: в домене и в отчёте хранятся коды и параметры (ADR 0005).
/// </summary>
public enum QualityIssueCode
{
    /// <summary>Код не задан. Значение существует только для выявления неинициализированных данных.</summary>
    Unspecified = 0,

    /// <summary>Геометрия DICOM противоречива или неполна.</summary>
    InconsistentGeometry = 1,

    /// <summary>Толщина среза или шаг вокселя вне диапазона, поддерживаемого конвейером.</summary>
    UnsupportedVoxelGeometry = 2,

    /// <summary>Серия получена в 2D, а запрошенный признак требует объёмного получения.</summary>
    AcquisitionTierTooLow = 3,

    /// <summary>Выраженные двигательные артефакты.</summary>
    MotionArtefact = 4,

    /// <summary>Голова обрезана полем обзора.</summary>
    HeadTruncated = 5,

    /// <summary>Серия постконтрастная и не должна подаваться в MRI-only конвейер.</summary>
    ContrastEnhancedSeries = 6,

    /// <summary>Обнаружены вписанные в изображение аннотации (burned-in annotations).</summary>
    BurnedInAnnotation = 7,

    /// <summary>Значение технического тега недостоверно (например, единицы MagneticFieldStrength).</summary>
    ImplausibleMetadata = 8,

    /// <summary>Вход вне распределения, на котором модель обучалась и валидировалась.</summary>
    OutOfDistribution = 9,
}

/// <summary>
/// Степень влияния проблемы на пригодность серии.
/// </summary>
public enum QualityIssueSeverity
{
    /// <summary>Степень не задана.</summary>
    Unspecified = 0,

    /// <summary>Предупреждение: анализ возможен, результат сопровождается оговоркой.</summary>
    Warning = 1,

    /// <summary>Блокирующая проблема: анализ выполнять нельзя.</summary>
    Blocking = 2,
}

/// <summary>
/// Отдельная выявленная проблема качества входных данных.
/// </summary>
public sealed record QualityIssue
{
    /// <summary>Машинно-читаемый код проблемы.</summary>
    public required QualityIssueCode Code { get; init; }

    /// <summary>Влияние проблемы на пригодность серии.</summary>
    public required QualityIssueSeverity Severity { get; init; }

    /// <summary>
    /// Параметры для подстановки в человекочитаемое объяснение на слое представления
    /// (например, измеренная толщина среза). Не содержат PHI.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
