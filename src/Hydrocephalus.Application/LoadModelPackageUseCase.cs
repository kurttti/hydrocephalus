using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Application;

/// <summary>Чем закончилась установка пакета.</summary>
public enum ModelInstallOutcome
{
    /// <summary>Исход не задан.</summary>
    Unspecified = 0,

    /// <summary>Пакет проверен и положен в хранилище. Действующим он не стал.</summary>
    Installed = 1,

    /// <summary>
    /// Такая версия уже установлена, и ничего не изменилось.
    ///
    /// Не ошибка пакета: проверку он мог пройти. Но две разные сборки под одним
    /// номером — это молчаливая смена измерительного инструмента, и отчёты, уже
    /// сославшиеся на эту версию, стали бы ссылаться не на то, чем получены.
    /// </summary>
    AlreadyInstalled = 2,

    /// <summary>Пакет проверку не прошёл и в хранилище не попал.</summary>
    Refused = 3,
}

/// <summary>
/// Что показывается администратору до подтверждения установки.
///
/// ADR 0008 требует показать версию, идентификатор ключа, статус подписи и
/// карточку модели — и только после подтверждения устанавливать. Поэтому у
/// осмотра свой тип: он ничего не меняет и ничего не устанавливает.
/// </summary>
/// <param name="Accepted">Прошёл ли пакет проверки ADR 0004.</param>
/// <param name="Manifest">Объявление пакета; <see langword="null"/>, если не прочитано.</param>
/// <param name="Signature">Состояние подписи так, как его определила проверка.</param>
/// <param name="ModelCard">Карточка модели из пакета; пусто, если разбор до неё не дошёл.</param>
/// <param name="Rejection">Причина отказа; <see langword="null"/> у принятого пакета.</param>
/// <param name="Detail">Уточнение к причине: имя файла или версия. Не содержит PHI.</param>
public sealed record ModelPackageInspection(
    bool Accepted,
    ModelPackageManifest? Manifest,
    ModelPackageSignature Signature,
    string ModelCard,
    ModelPackageRejection? Rejection,
    string Detail);

/// <summary>
/// Итог установки пакета модели.
/// </summary>
/// <param name="Outcome">Чем закончилось.</param>
/// <param name="ModelVersion">Версия модели; пусто, если манифест не прочитан.</param>
/// <param name="Rejection">Причина отказа; <see langword="null"/> у принятого пакета.</param>
/// <param name="Detail">Уточнение к причине: имя файла или версия. Не содержит PHI.</param>
public sealed record ModelPackageInstallation(
    ModelInstallOutcome Outcome,
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
/// Два шага, и это требование ADR 0008, а не удобство: сначала осмотр —
/// показать версию, ключ, подпись и карточку, — и только после подтверждения
/// установка. Пакет при осмотре никуда не кладётся и ничего не меняет.
///
/// Установка не делает версию действующей. Это <see
/// cref="ActivateModelVersionUseCase"/>: пакет, лежащий в хранилище, ещё ничем
/// не измеряет, и разделение нужно и для отката, который переключает метку,
/// а не переустанавливает файл.
///
/// В журнал пишутся и принятие, и отказ. Отказ — не менее важное событие:
/// попытка подсунуть изменённый пакет есть ровно то, ради чего журнал ведётся
/// (`docs/security/README.md`), и бесследной она быть не должна. Поэтому отказ
/// записывается уже на осмотре: иначе подделка, забракованная до того, как
/// администратору предложили подтвердить, не оставила бы следа вовсе.
/// </summary>
public sealed class LoadModelPackageUseCase
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
    public LoadModelPackageUseCase(
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
    /// Собирает запись о проверке для журнала.
    ///
    /// Состояние подписи берётся у самой проверки, а не выводится из кода
    /// отказа: по коду это восстанавливается не всегда, а «подпись не
    /// проверялась» и «подпись неверна» — разные события. Первое говорит
    /// о повреждённом файле, второе о подмене.
    /// </summary>
    /// <param name="check">Итог проверки пакета.</param>
    /// <returns>Запись для журнала.</returns>
    public static ModelPackageAudit Describe(ModelPackageCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        return new ModelPackageAudit(
            check.Manifest?.SigningKeyId ?? string.Empty,
            check.Signature,
            check.Rejection,
            check.Detail);
    }

    /// <summary>
    /// Осматривает пакет, ничего не устанавливая.
    ///
    /// Отказ записывается в журнал, принятие — нет: принятый, но не
    /// подтверждённый пакет ничего не изменил, и записи о нём означали бы
    /// установку, которой не было. Отказ же изменением не является по другой
    /// причине — он сам и есть то событие, ради которого журнал ведётся.
    /// </summary>
    /// <param name="packagePath">Путь к файлу пакета.</param>
    /// <param name="requestedBy">Кто осматривает.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Что показать администратору.</returns>
    /// <exception cref="AccessDeniedException">Если у роли нет права на установку модели.</exception>
    public async Task<ModelPackageInspection> InspectAsync(
        string packagePath,
        Actor requestedBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(requestedBy);

        requestedBy.Require(Capability.InstallModelPackage);

        var check = this.reader.Verify(packagePath);

        if (check.Rejection is not null)
        {
            await this.RecordAsync(check, requestedBy, cancellationToken).ConfigureAwait(false);
        }

        return new ModelPackageInspection(
            check.Rejection is null,
            check.Manifest,
            check.Signature,
            check.ModelCard,
            check.Rejection,
            check.Detail);
    }

    /// <summary>
    /// Проверяет пакет, кладёт его в хранилище и записывает исход в журнал.
    ///
    /// Проверка выполняется заново, а не берётся с осмотра: между показом
    /// карточки и подтверждением файл администратора остаётся доступным для
    /// записи, и устанавливать то, что проверено раньше, значило бы доверять
    /// тому, чего уже может не быть.
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

        requestedBy.Require(Capability.InstallModelPackage);

        var check = this.reader.Verify(packagePath);

        // Версия пишется и при отказе, если манифест успели прочитать: «какой
        // именно пакет отвергнут» — часть ответа на вопрос, что происходило.
        var version = check.Manifest?.ModelVersion ?? string.Empty;

        if (check.Rejection is not null)
        {
            await this.RecordAsync(check, requestedBy, cancellationToken).ConfigureAwait(false);

            return new ModelPackageInstallation(
                ModelInstallOutcome.Refused, version, check.Rejection, check.Detail);
        }

        // Повторная установка той же версии не выполняется и в журнал не идёт:
        // ничего не изменилось, а запись «пакет установлен» означала бы, что
        // в хранилище легли другие байты под тем же номером.
        if (this.store.InstalledVersions().Contains(version, StringComparer.Ordinal))
        {
            return new ModelPackageInstallation(
                ModelInstallOutcome.AlreadyInstalled, version, null, string.Empty);
        }

        this.store.Install(packagePath, version);

        await this.RecordAsync(check, requestedBy, cancellationToken).ConfigureAwait(false);

        return new ModelPackageInstallation(
            ModelInstallOutcome.Installed, version, null, string.Empty);
    }

    private Task RecordAsync(
        ModelPackageCheck check,
        Actor requestedBy,
        CancellationToken cancellationToken)
    {
        var version = check.Manifest?.ModelVersion ?? string.Empty;

        return this.auditLog.RecordAsync(
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
            cancellationToken);
    }
}
