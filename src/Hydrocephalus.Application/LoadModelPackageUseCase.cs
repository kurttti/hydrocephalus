using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Application;

/// <summary>
/// Итог установки пакета модели.
/// </summary>
/// <param name="Accepted">Принят ли пакет.</param>
/// <param name="ModelVersion">Версия модели; пусто, если манифест не прочитан.</param>
/// <param name="Rejection">Причина отказа; <see langword="null"/> у принятого пакета.</param>
/// <param name="Detail">Уточнение к причине: имя файла или версия. Не содержит PHI.</param>
public sealed record ModelPackageInstallation(
    bool Accepted,
    string ModelVersion,
    ModelPackageRejection? Rejection,
    string Detail);

/// <summary>
/// Установка пакета модели.
///
/// Отдельный сценарий, а не часть анализа: смена модели меняет то, что
/// приложение измеряет у всех последующих пациентов, и ADR 0008 требует, чтобы
/// она происходила только действием администратора.
///
/// В журнал пишутся и принятие, и отказ. Отказ — не менее важное событие:
/// попытка подсунуть изменённый пакет есть ровно то, ради чего журнал ведётся
/// (`docs/security/README.md`), и бесследной она быть не должна.
/// </summary>
public sealed class LoadModelPackageUseCase
{
    private readonly IModelPackageReader reader;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="reader">Читатель пакета: он же держит доверенный ключ.</param>
    /// <param name="auditLog">Журнал аудита.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public LoadModelPackageUseCase(
        IModelPackageReader reader,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.reader = reader;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Проверяет пакет и записывает исход в журнал.
    /// </summary>
    /// <param name="packagePath">Путь к файлу пакета.</param>
    /// <param name="requestedBy">Кто устанавливает.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Исход установки.</returns>
    /// <exception cref="AccessDeniedException">Если у роли нет права на установку модели.</exception>
    public async Task<ModelPackageInstallation> ExecuteAsync(
        string packagePath,
        Actor requestedBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(requestedBy);

        if (!requestedBy.Can(Capability.InstallModelPackage))
        {
            throw AccessDeniedException.For(requestedBy.Role, Capability.InstallModelPackage);
        }

        var check = this.reader.Verify(packagePath);

        // Версия пишется и при отказе, если манифест успели прочитать: «какой
        // именно пакет отвергнут» — часть ответа на вопрос, что происходило.
        var version = check.Manifest?.ModelVersion ?? string.Empty;

        await this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = check.Rejection is null
                    ? AuditEventCode.ModelPackageLoaded
                    : AuditEventCode.ModelPackageRefused,
                OccurredAt = this.timeProvider.GetUtcNow(),
                ModelVersion = version.Length > 0 ? version : null,
                PseudonymousActorId = requestedBy.PseudonymousUserId,
                ModelPackage = Describe(check),
            },
            cancellationToken).ConfigureAwait(false);

        return new ModelPackageInstallation(
            check.Rejection is null,
            version,
            check.Rejection,
            check.Detail);
    }

    /// <summary>
    /// Собирает запись о проверке для журнала.
    ///
    /// Состояние подписи выводится из того, докуда дошёл разбор: отказы до
    /// проверки подписи означают «не проверялась», а не «неверна». Путать их
    /// нельзя — первое говорит о повреждённом файле, второе о подмене.
    /// </summary>
    private static ModelPackageAudit Describe(ModelPackageCheck check) => new(
        check.Manifest?.SigningKeyId ?? string.Empty,
        check.Rejection switch
        {
            ModelPackageRejection.SignatureInvalid => ModelPackageSignature.Invalid,
            ModelPackageRejection.NotAPackage
                or ModelPackageRejection.ManifestUnreadable
                or ModelPackageRejection.ChecksumsUnreadable => ModelPackageSignature.NotChecked,
            _ => ModelPackageSignature.Valid,
        },
        check.Rejection,
        check.Detail);
}
