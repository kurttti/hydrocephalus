using Hydrocephalus.Application;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Установка пакета модели.
///
/// Смена модели меняет то, что приложение измеряет у всех последующих пациентов,
/// поэтому она — действие администратора (ADR 0008) и оставляет след в журнале
/// независимо от исхода.
///
/// Два шага проверяются по отдельности: осмотр ничего не устанавливает, а
/// установка не делает версию действующей. Слить их значило бы разрешить смену
/// измерительного инструмента одним нажатием, чего ADR 0008 не допускает.
/// </summary>
public sealed class LoadModelPackageTests
{
    private static readonly Actor Administrator = Actor.Create("admin-1", ClinicalRole.Administrator);
    private static readonly Actor Clinician = Actor.Create("doctor-1", ClinicalRole.Clinician);

    private static readonly ModelPackageManifest Manifest = new(
        FormatVersion: "1",
        ModelVersion: "vinn-axial-2.0.0",
        MinimumApplicationVersion: "1.0.0",
        PreprocessingVersion: "conform-lia-256-1",
        LabelMapVersion: "fastsurfer-vinn-axial-2.0.0",
        SigningKeyId: "release-2026");

    [Fact]
    public async Task A_sound_package_is_recorded_as_loaded()
    {
        var audit = new RecordingAudit();
        var store = new RecordingStore();
        var useCase = Create(new ModelPackageCheck(Manifest, null)
        {
            Signature = ModelPackageSignature.Valid,
        }, store, audit);

        var outcome = await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.Equal(ModelInstallOutcome.Installed, outcome.Outcome);
        Assert.Equal("vinn-axial-2.0.0", outcome.ModelVersion);
        Assert.Equal([("package.hcmp", "vinn-axial-2.0.0")], store.Installed);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.ModelPackageLoaded, recorded.Code);
        Assert.Equal("vinn-axial-2.0.0", recorded.ModelVersion);
        Assert.Equal("release-2026", recorded.ModelPackage?.SigningKeyId);
        Assert.Equal(ModelPackageSignature.Valid, recorded.ModelPackage?.Signature);
    }

    [Fact]
    public async Task Installing_does_not_make_the_version_active()
    {
        // ADR 0008 разделяет установку и активацию: пакет, лежащий в хранилище,
        // ещё ничего не измеряет. Иначе откат пришлось бы делать переустановкой.
        var store = new RecordingStore();
        var useCase = Create(new ModelPackageCheck(Manifest, null), store, new RecordingAudit());

        await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.Empty(store.Activated);
    }

    [Fact]
    public async Task The_same_version_is_not_installed_over_itself()
    {
        // Две разные сборки под одним номером — молчаливая смена измерительного
        // инструмента: отчёты, уже сославшиеся на эту версию, стали бы ссылаться
        // не на то, чем получены.
        var audit = new RecordingAudit();
        var store = new RecordingStore { Versions = { "vinn-axial-2.0.0" } };
        var useCase = Create(new ModelPackageCheck(Manifest, null), store, audit);

        var outcome = await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.Equal(ModelInstallOutcome.AlreadyInstalled, outcome.Outcome);
        Assert.Empty(store.Installed);

        // Записи нет, потому что ничего не произошло: «пакет установлен»
        // означало бы, что в хранилище легли другие байты под тем же номером.
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task A_refused_package_is_recorded_too()
    {
        // Отказ пишется наравне с успехом: попытка подсунуть изменённый пакет —
        // ровно то событие, ради которого журнал ведётся.
        var audit = new RecordingAudit();
        var store = new RecordingStore();
        var useCase = Create(
            new ModelPackageCheck(Manifest, ModelPackageRejection.ContentAltered, "segmentation.onnx"),
            store,
            audit);

        var outcome = await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.Equal(ModelInstallOutcome.Refused, outcome.Outcome);
        Assert.Equal(ModelPackageRejection.ContentAltered, outcome.Rejection);
        Assert.Empty(store.Installed);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.ModelPackageRefused, recorded.Code);
        Assert.Equal("segmentation.onnx", recorded.ModelPackage?.Detail);
    }

    [Fact]
    public async Task The_signature_state_comes_from_the_check_itself()
    {
        // «Не проверялась» и «неверна» — разные события: первое о повреждённом
        // файле, второе о подмене. Выводить одно из другого по коду отказа
        // нельзя: «список хешей не разбирается» встречается с обеих сторон
        // от проверки подписи.
        var audit = new RecordingAudit();
        var useCase = Create(
            new ModelPackageCheck(Manifest, ModelPackageRejection.ChecksumsUnreadable)
            {
                Signature = ModelPackageSignature.Valid,
            },
            new RecordingStore(),
            audit);

        await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.Equal(ModelPackageSignature.Valid, Assert.Single(audit.Events).ModelPackage?.Signature);
    }

    [Fact]
    public async Task An_inspected_package_is_not_installed()
    {
        // Осмотр нужен до подтверждения: ADR 0008 требует показать версию,
        // ключ, подпись и карточку — и только потом устанавливать.
        var audit = new RecordingAudit();
        var store = new RecordingStore();
        var useCase = Create(
            new ModelPackageCheck(Manifest, null)
            {
                Signature = ModelPackageSignature.Valid,
                ModelCard = "# Карточка",
            },
            store,
            audit);

        var inspection = await useCase.InspectAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.True(inspection.Accepted);
        Assert.Equal("vinn-axial-2.0.0", inspection.Manifest?.ModelVersion);
        Assert.Equal("# Карточка", inspection.ModelCard);
        Assert.Empty(store.Installed);

        // Принятый, но не подтверждённый пакет ничего не изменил, и запись
        // о нём означала бы установку, которой не было.
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task A_package_refused_at_inspection_is_recorded()
    {
        // Иначе подделка, забракованная до того, как администратору предложили
        // подтвердить, не оставила бы следа вовсе.
        var audit = new RecordingAudit();
        var useCase = Create(
            new ModelPackageCheck(Manifest, ModelPackageRejection.SignatureInvalid)
            {
                Signature = ModelPackageSignature.Invalid,
            },
            new RecordingStore(),
            audit);

        var inspection = await useCase.InspectAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.False(inspection.Accepted);
        Assert.Equal(AuditEventCode.ModelPackageRefused, Assert.Single(audit.Events).Code);
    }

    [Fact]
    public async Task A_clinician_may_not_install_a_model()
    {
        var audit = new RecordingAudit();
        var useCase = Create(new ModelPackageCheck(Manifest, null), new RecordingStore(), audit);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => useCase.ExecuteAsync("package.hcmp", Clinician, CancellationToken.None));

        // Отказ в праве не читает пакет и не пишет о модели: событие о доступе
        // ведёт проверка прав, а не этот сценарий.
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task A_clinician_may_not_even_inspect_a_package()
    {
        var useCase = Create(new ModelPackageCheck(Manifest, null), new RecordingStore(), new RecordingAudit());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => useCase.InspectAsync("package.hcmp", Clinician, CancellationToken.None));
    }

    private static LoadModelPackageUseCase Create(
        ModelPackageCheck check,
        IInstalledModelStore store,
        IAuditLog audit) =>
        new(new StubReader(check), store, audit, TimeProvider.System);

    private sealed class StubReader(ModelPackageCheck check) : IModelPackageReader
    {
        public ModelPackageCheck Verify(string packagePath) => check;

        // Установка весов не читает: экрану администратора нужно решение о
        // пакете, а не сотня мегабайт. Веса берёт загрузка, своим проходом.
        public ModelPackageCheck Open(string packagePath) => check;
    }

    private sealed class RecordingStore : IInstalledModelStore
    {
        public List<string> Versions { get; } = [];

        public List<(string Path, string Version)> Installed { get; } = [];

        public List<string> Activated { get; } = [];

        public string? Active { get; set; }

        public IReadOnlyList<string> InstalledVersions() => this.Versions;

        public string? ActiveVersion() => this.Active;

        public string? ActivePackagePath() =>
            this.Active is null ? null : this.PackagePathOf(this.Active);

        public string PackagePathOf(string modelVersion) => modelVersion + ".hcmp";

        public string Install(string packagePath, string modelVersion)
        {
            this.Installed.Add((packagePath, modelVersion));
            this.Versions.Add(modelVersion);

            return this.PackagePathOf(modelVersion);
        }

        public void Activate(string modelVersion)
        {
            this.Activated.Add(modelVersion);
            this.Active = modelVersion;
        }
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            this.Events.Add(auditEvent);

            return Task.CompletedTask;
        }
    }
}
