using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Application;

/// <summary>Чем закончилась смена действующей версии.</summary>
public enum ModelActivationOutcome
{
    /// <summary>Исход не задан.</summary>
    Unspecified = 0,

    /// <summary>Метка переключена: версия стала действующей.</summary>
    Activated = 1,

    /// <summary>Эта версия уже действовала; ничего не менялось.</summary>
    AlreadyActive = 2,

    /// <summary>Такая версия не установлена.</summary>
    NotInstalled = 3,

    /// <summary>
    /// Пакет в хранилище проверку не прошёл, и метка не переключена.
    ///
    /// Проверяется перед переключением именно тот файл, которым будут измерять:
    /// пакет, испортившийся в хранилище после установки, иначе обнаружился бы
    /// только отказом анализа при следующем запуске — то есть у врача,
    /// а не у администратора.
    /// </summary>
    PackageRefused = 4,

    /// <summary>
    /// В файле хранилища лежит пакет другой версии, и метка не переключена.
    ///
    /// Версия в хранилище — это имя файла, а измеряет приложение той версией,
    /// которую называет объявление внутри пакета. Разойтись они могут: пока
    /// экрана установки не было, пакет кладут под нужным именем руками.
    /// Переключиться на такую версию значило бы записать в журнал одно,
    /// а измерять другим.
    /// </summary>
    VersionMismatch = 5,
}

/// <summary>
/// Итог смены действующей версии.
/// </summary>
/// <param name="Outcome">Чем закончилось.</param>
/// <param name="FromVersion">Версия, действовавшая до смены; пусто, если никакая.</param>
/// <param name="ToVersion">Версия, которую просили сделать действующей.</param>
/// <param name="Rejection">Причина отказа пакета; <see langword="null"/>, если его не отвергали.</param>
/// <param name="Detail">Уточнение к причине: имя файла или версия. Не содержит PHI.</param>
public sealed record ModelVersionActivation(
    ModelActivationOutcome Outcome,
    string FromVersion,
    string ToVersion,
    ModelPackageRejection? Rejection,
    string Detail);

/// <summary>
/// Смена действующей версии модели, она же откат.
///
/// Откат отдельным сценарием не выделен: по ADR 0008 это та же операция —
/// переключение метки на другую установленную версию, без переустановки файла
/// и без сети. Отдельный путь для отката пришлось бы держать в согласии
/// с этим, а разошлись бы они молча.
///
/// Право то же, что на установку (<see cref="Capability.InstallModelPackage"/>):
/// ADR 0008 называет установку и откат одним действием администратора.
///
/// В журнал идут обе версии — «из» и «в». Запись с одной не отвечает на вопрос,
/// что именно изменилось, а восстанавливать это сопоставлением соседних записей
/// значило бы полагаться на то, что ни одна из них не потеряна.
/// </summary>
public sealed class ActivateModelVersionUseCase
{
    private readonly IModelPackageReader reader;
    private readonly IInstalledModelStore store;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="reader">Читатель пакета: он же держит доверенный ключ.</param>
    /// <param name="store">Хранилище установленных версий.</param>
    /// <param name="auditLog">Журнал аудита.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public ActivateModelVersionUseCase(
        IModelPackageReader reader,
        IInstalledModelStore store,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.reader = reader;
        this.store = store;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Делает установленную версию действующей.
    /// </summary>
    /// <param name="modelVersion">Версия, которая должна стать действующей.</param>
    /// <param name="requestedBy">Кто переключает.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Исход смены.</returns>
    /// <exception cref="AccessDeniedException">Если у роли нет права на установку модели.</exception>
    public async Task<ModelVersionActivation> ExecuteAsync(
        string modelVersion,
        Actor requestedBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        ArgumentNullException.ThrowIfNull(requestedBy);

        requestedBy.Require(Capability.InstallModelPackage);

        var from = this.store.ActiveVersion() ?? string.Empty;

        if (!this.store.InstalledVersions().Contains(modelVersion, StringComparer.Ordinal))
        {
            return new ModelVersionActivation(
                ModelActivationOutcome.NotInstalled, from, modelVersion, null, string.Empty);
        }

        if (string.Equals(from, modelVersion, StringComparison.Ordinal))
        {
            // Ничего не изменилось, и записи об этом в журнале быть не должно:
            // «версия сменена с X на X» читается как событие, которого не было.
            return new ModelVersionActivation(
                ModelActivationOutcome.AlreadyActive, from, modelVersion, null, string.Empty);
        }

        var check = this.reader.Verify(this.store.PackagePathOf(modelVersion));

        if (check.Rejection is not null)
        {
            // Отказ пишется: пакет, испорченный уже в хранилище, — то самое
            // событие, ради которого журнал ведётся, и метка при этом остаётся
            // на прежней версии, а не сбрасывается.
            await this.auditLog.RecordAsync(
                new AuditEvent
                {
                    Code = AuditEventCode.ModelPackageRefused,
                    OccurredAt = this.timeProvider.GetUtcNow(),
                    ModelVersion = modelVersion,
                    PseudonymousActorId = requestedBy.PseudonymousUserId,
                    ModelPackage = LoadModelPackageUseCase.Describe(check),
                },
                cancellationToken).ConfigureAwait(false);

            return new ModelVersionActivation(
                ModelActivationOutcome.PackageRefused,
                from,
                modelVersion,
                check.Rejection,
                check.Detail);
        }

        // Объявление внутри пакета сверяется с именем, под которым он лежит:
        // имя файла задаёт версию в хранилище, а измеряет приложение той,
        // которую называет объявление. Пока этого экрана не было, пакет
        // клали руками, и разойтись эти две версии могут. Журнал сказал бы
        // тогда одно, а отчёт — другое.
        var declared = check.Manifest?.ModelVersion ?? string.Empty;

        if (!string.Equals(declared, modelVersion, StringComparison.Ordinal))
        {
            await this.auditLog.RecordAsync(
                new AuditEvent
                {
                    Code = AuditEventCode.ModelPackageRefused,
                    OccurredAt = this.timeProvider.GetUtcNow(),
                    ModelVersion = modelVersion,
                    PseudonymousActorId = requestedBy.PseudonymousUserId,

                    // Причины отказа нет: сам пакет проверку прошёл. Не прошло
                    // соответствие его объявления тому имени, под которым он
                    // лежит, и названо оно уточнением.
                    ModelPackage = new ModelPackageAudit(
                        check.Manifest?.SigningKeyId ?? string.Empty,
                        check.Signature,
                        null,
                        declared),
                },
                cancellationToken).ConfigureAwait(false);

            return new ModelVersionActivation(
                ModelActivationOutcome.VersionMismatch, from, modelVersion, null, declared);
        }

        this.store.Activate(modelVersion);

        await this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = AuditEventCode.ModelPackageActivated,
                OccurredAt = this.timeProvider.GetUtcNow(),
                ModelVersion = modelVersion,
                PseudonymousActorId = requestedBy.PseudonymousUserId,
                ModelActivation = new ModelActivationAudit(from, modelVersion),
            },
            cancellationToken).ConfigureAwait(false);

        return new ModelVersionActivation(
            ModelActivationOutcome.Activated, from, modelVersion, null, string.Empty);
    }
}
