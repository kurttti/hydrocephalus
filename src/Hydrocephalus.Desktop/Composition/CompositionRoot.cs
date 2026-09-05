using System.Reflection;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Inference;
using Hydrocephalus.Inference.QualityControl;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dicom;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Сборка приложения из реализаций портов.
///
/// Связывание выполняется конструкторами вручную, без контейнера: объектов пять,
/// и контейнер здесь добавил бы зависимость и косвенность, ничего не упростив.
/// Он появится, когда появятся ViewModel со своим жизненным циклом.
///
/// Это единственное место, где слои встречаются (docs/architecture/README.md):
/// UI получает готовый сценарий и не знает ни о fo-dicom, ни о том, где лежат файлы.
/// </summary>
public sealed class CompositionRoot : IDisposable
{
    private readonly HashChainAuditLog auditLog;

    private CompositionRoot(
        Hydrocephalus.Application.AnalyzeStudyUseCase analyzeStudy,
        HashChainAuditLog auditLog,
        PipelineIdentity pipeline)
    {
        this.AnalyzeStudy = analyzeStudy;
        this.auditLog = auditLog;
        this.Pipeline = pipeline;
    }

    /// <summary>Сценарий анализа исследования.</summary>
    public Hydrocephalus.Application.AnalyzeStudyUseCase AnalyzeStudy { get; }

    /// <summary>Версии конвейера, попадающие в отчёт.</summary>
    public PipelineIdentity Pipeline { get; }

    /// <summary>
    /// Собирает приложение.
    /// </summary>
    /// <param name="paths">Расположение локальных каталогов.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Собранное приложение.</returns>
    public static async Task<CompositionRoot> CreateAsync(
        ApplicationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Соль читается или создаётся до всего остального: без неё импорт
        // не может построить ни одного псевдонима, и запускаться незачем.
        var salt = await PseudonymSaltStore
            .GetOrCreateAsync(paths.PseudonymSaltPath, cancellationToken)
            .ConfigureAwait(false);

        var pipeline = DescribePipeline();

        var auditLog = new HashChainAuditLog(paths.AuditLogPath);

        var useCase = new Hydrocephalus.Application.AnalyzeStudyUseCase(
            new StudyImporter(
                new DicomImportOptions { PseudonymSalt = salt },
                new WorkingCopyOptions { RootDirectory = paths.WorkingCopyRoot }),
            new QualityControlOnlyEngine(new InputQualityControl(), pipeline),
            new JsonReportStore(paths.ReportRoot),
            auditLog,
            TimeProvider.System);

        return new CompositionRoot(useCase, auditLog, pipeline);
    }

    /// <summary>Освобождает ресурсы собранных реализаций.</summary>
    public void Dispose() => this.auditLog.Dispose();

    /// <summary>
    /// Описывает версии конвейера.
    ///
    /// Предобработка, схема признаков и label map отмечены как нереализованные,
    /// а не проставлены версией: этих этапов в конвейере ещё нет, и «1.0.0»
    /// в таком поле — заявка на воспроизводимость, которой не существует.
    ///
    /// Commit SHA берётся из атрибута сборки. Он называет коммит, из которого
    /// собрано приложение, но не состояние рабочего дерева: сборка с
    /// незакоммиченными правками сошлётся на предыдущий коммит. Для сборок
    /// из CI это точное значение, и именно они попадают к врачу.
    /// </summary>
    private static PipelineIdentity DescribePipeline() => new()
    {
        PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
        FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,
        LabelMapVersion = PipelineIdentity.NotImplementedVersion,
        ApplicationCommitSha = BuildProvenance.CommitShaOf(Assembly.GetExecutingAssembly()),
    };
}
