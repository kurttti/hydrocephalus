using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Application;

/// <summary>
/// Запрос на экспорт отчёта.
/// </summary>
public sealed record ReportExportRequest
{
    /// <summary>Отчёт.</summary>
    public required AnalysisReport Report { get; init; }

    /// <summary>Вариант экспорта.</summary>
    public required ReportExportVariant Variant { get; init; }

    /// <summary>Тот, кто запросил экспорт.</summary>
    public required Actor RequestedBy { get; init; }

    /// <summary>
    /// Подтверждение того, что свободный текст комментариев врача может
    /// содержать введённую вручную PHI.
    ///
    /// Требуется только для обезличенного варианта и только при наличии
    /// комментариев. ADR 0005 требует отдельного предупреждения; отдельное
    /// поле делает предупреждение обязательным к прочтению, а не украшением
    /// диалога: без него экспорт не состоится.
    /// </summary>
    public bool AcknowledgeAnnotationsMayContainPhi { get; init; }
}

/// <summary>
/// Экспорт отчёта в одном из двух вариантов (ADR 0005).
///
/// Разграничение выполняется здесь, а не при отрисовке: рендерер не решает,
/// что скрывать. И не при отображении, а именно при экспорте — PHI не должна
/// попадать в файл, который может быть передан вовне.
/// </summary>
public sealed class ExportReportUseCase
{
    private readonly IReportExportStore store;
    private readonly IPatientIdentityRegistry registry;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="store">Хранилище экспортированных отчётов.</param>
    /// <param name="registry">Внешний реестр идентификаторов пациента.</param>
    /// <param name="auditLog">Журнал аудита.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public ExportReportUseCase(
        IReportExportStore store,
        IPatientIdentityRegistry registry,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.store = store;
        this.registry = registry;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Выполняет экспорт.
    /// </summary>
    /// <param name="request">Запрос на экспорт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ссылка на экспортированный файл.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если вариант не задан, реестр не знает пациента либо не подтверждён
    /// риск PHI в комментариях.
    /// </exception>
    /// <exception cref="AccessDeniedException">
    /// Если у инициатора нет права на этот вариант экспорта.
    /// </exception>
    public async Task<string> ExecuteAsync(
        ReportExportRequest request,
        CancellationToken cancellationToken)
    {
        var export = await this.PrepareAsync(request, cancellationToken).ConfigureAwait(false);

        var reference = await this.store.WriteAsync(export, cancellationToken).ConfigureAwait(false);

        await this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = AuditEventCode.ReportExported,
                OccurredAt = export.ExportedAt,
                PseudonymousStudyId = request.Report.PseudonymousStudyId,
                PseudonymousActorId = request.RequestedBy.PseudonymousUserId,
                ReportExportVariant = request.Variant,
            },
            cancellationToken).ConfigureAwait(false);

        return reference;
    }

    /// <summary>
    /// Готовит отчёт к показу, ничего не записывая.
    ///
    /// Предпросмотр проходит тот же путь, что и экспорт: то же право, тот же
    /// разбор варианта, та же сборка содержимого. Иначе экран показывал бы
    /// одно, а в файл уходило другое — то есть ровно то, от чего предпросмотр
    /// и защищает.
    ///
    /// Право проверяется всерьёз, а не для вида: клинический вариант содержит
    /// идентификаторы пациента, и показ его на экране — такое же раскрытие,
    /// как запись в файл. Поэтому же показ попадает в журнал.
    /// </summary>
    /// <param name="request">Запрос на экспорт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Содержимое, которое было бы записано.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если вариант не задан, реестр не знает пациента либо не подтверждён
    /// риск PHI в комментариях.
    /// </exception>
    /// <exception cref="AccessDeniedException">
    /// Если у инициатора нет права на этот вариант экспорта.
    /// </exception>
    public async Task<ReportExport> PreviewAsync(
        ReportExportRequest request,
        CancellationToken cancellationToken)
    {
        var export = await this.PrepareAsync(request, cancellationToken).ConfigureAwait(false);

        await this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = AuditEventCode.ReportPreviewed,
                OccurredAt = export.ExportedAt,
                PseudonymousStudyId = request.Report.PseudonymousStudyId,
                PseudonymousActorId = request.RequestedBy.PseudonymousUserId,
                ReportExportVariant = request.Variant,
            },
            cancellationToken).ConfigureAwait(false);

        return export;
    }

    private async Task<ReportExport> PrepareAsync(
        ReportExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var capability = request.Variant switch
        {
            ReportExportVariant.Deidentified => Capability.ExportDeidentifiedReport,
            ReportExportVariant.Clinical => Capability.ExportClinicalReport,

            // Незаданный вариант не отображается ни в какое право, и выбрать
            // за пользователя более безопасный нельзя: он мог иметь в виду
            // другой, и молчаливая подмена варианта — это подмена содержимого.
            _ => throw new DomainRuleViolationException(
                "A report export must name its variant."),
        };

        await this.RequireAsync(request.RequestedBy, capability, cancellationToken)
            .ConfigureAwait(false);

        return request.Variant == ReportExportVariant.Clinical
            ? await this.BuildClinicalAsync(request, cancellationToken).ConfigureAwait(false)
            : this.BuildDeidentified(request);
    }

    private ReportExport BuildDeidentified(ReportExportRequest request)
    {
        if (request.Report.ClinicianAnnotations.Count > 0
            && !request.AcknowledgeAnnotationsMayContainPhi)
        {
            // Комментарии врача попадают в экспорт, и свободный текст может
            // содержать введённую вручную PHI. Вырезать их молча нельзя —
            // они часть заключения; отправить не предупредив тоже нельзя.
            throw new DomainRuleViolationException(
                "A deidentified export of a report with clinician annotations requires "
                + "acknowledging that free text may contain manually entered identifiers.");
        }

        return ReportExport.Deidentified(
            request.Report,
            request.RequestedBy,
            this.timeProvider.GetUtcNow());
    }

    private async Task<ReportExport> BuildClinicalAsync(
        ReportExportRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await this.registry
            .ResolveAsync(request.Report.PseudonymousStudyId, cancellationToken)
            .ConfigureAwait(false);

        if (identity is null)
        {
            // Выдать обезличенный файл под клиническим названием нельзя:
            // врач решит, что перед ним карта конкретного пациента.
            throw new DomainRuleViolationException(
                "The registry does not know this study, so a clinical export cannot be produced.");
        }

        return ReportExport.Clinical(
            request.Report,
            identity,
            request.RequestedBy,
            this.timeProvider.GetUtcNow());
    }

    private async Task RequireAsync(
        Actor requestedBy,
        Capability capability,
        CancellationToken cancellationToken)
    {
        if (requestedBy.Can(capability))
        {
            return;
        }

        await this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = AuditEventCode.AccessDenied,
                OccurredAt = this.timeProvider.GetUtcNow(),
                PseudonymousActorId = requestedBy.PseudonymousUserId,
            },
            cancellationToken).ConfigureAwait(false);

        throw AccessDeniedException.For(requestedBy.Role, capability);
    }
}
