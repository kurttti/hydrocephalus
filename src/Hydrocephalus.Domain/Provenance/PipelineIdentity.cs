namespace Hydrocephalus.Domain.Provenance;

/// <summary>
/// Идентичность конвейера, воспроизводящая результат: версии предобработки,
/// схемы признаков и сборки приложения (docs/ml/README.md, раздел о воспроизводимости).
/// </summary>
public sealed record PipelineIdentity
{
    /// <summary>
    /// Значение для этапа, которого в конвейере ещё нет.
    ///
    /// Отдельная строка, а не «1.0.0» и не пустое поле: версия несуществующего
    /// этапа — это заявка на воспроизводимость, которой нет, а пустое поле
    /// невозможно отличить от потерянного. По этому значению отчёт, полученный
    /// неполным конвейером, находится поиском.
    /// </summary>
    public const string NotImplementedVersion = "not-implemented";

    /// <summary>Версия конфигурации предобработки.</summary>
    public required string PreprocessingVersion { get; init; }

    /// <summary>Версия схемы признаков.</summary>
    public required string FeatureSchemaVersion { get; init; }

    /// <summary>Версия label map сегментации.</summary>
    public required string LabelMapVersion { get; init; }

    /// <summary>Commit SHA сборки приложения.</summary>
    public required string ApplicationCommitSha { get; init; }
}
