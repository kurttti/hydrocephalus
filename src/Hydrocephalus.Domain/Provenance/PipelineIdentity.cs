namespace Hydrocephalus.Domain.Provenance;

/// <summary>
/// Идентичность конвейера, воспроизводящая результат: версии предобработки,
/// схемы признаков и сборки приложения (docs/ml/README.md, раздел о воспроизводимости).
/// </summary>
public sealed record PipelineIdentity
{
    /// <summary>Версия конфигурации предобработки.</summary>
    public required string PreprocessingVersion { get; init; }

    /// <summary>Версия схемы признаков.</summary>
    public required string FeatureSchemaVersion { get; init; }

    /// <summary>Версия label map сегментации.</summary>
    public required string LabelMapVersion { get; init; }

    /// <summary>Commit SHA сборки приложения.</summary>
    public required string ApplicationCommitSha { get; init; }
}
