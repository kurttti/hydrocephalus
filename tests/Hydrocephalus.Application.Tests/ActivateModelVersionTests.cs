using Hydrocephalus.Application;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Смена действующей версии модели, она же откат.
///
/// ADR 0008: откат — переключение метки на другую установленную версию, без
/// переустановки файла и без сети. Причина клиническая: смена версии меняет
/// результат для одного и того же пациента, и возврат назад не должен зависеть
/// от того, сохранил ли администратор исходный файл.
/// </summary>
public sealed class ActivateModelVersionTests
{
    private static readonly Actor Administrator = Actor.Create("admin-1", ClinicalRole.Administrator);
    private static readonly Actor Clinician = Actor.Create("doctor-1", ClinicalRole.Clinician);

    [Fact]
    public async Task Activating_records_both_versions()
    {
        // Запись с одной версией не отвечает на вопрос, что изменилось,
        // а восстанавливать это по соседним записям значило бы полагаться
        // на то, что ни одна из них не потеряна.
        var audit = new RecordingAudit();
        var store = new RecordingStore
        {
            Versions = { "vinn-axial-2.0.0", "vinn-axial-2.1.0" },
            Active = "vinn-axial-2.0.0",
        };

        var outcome = await Create(store, audit)
            .ExecuteAsync("vinn-axial-2.1.0", Administrator, CancellationToken.None);

        Assert.Equal(ModelActivationOutcome.Activated, outcome.Outcome);
        Assert.Equal("vinn-axial-2.1.0", store.Active);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.ModelPackageActivated, recorded.Code);
        Assert.Equal("vinn-axial-2.0.0", recorded.ModelActivation?.FromVersion);
        Assert.Equal("vinn-axial-2.1.0", recorded.ModelActivation?.ToVersion);
        Assert.Equal("admin-1", recorded.PseudonymousActorId);
    }

    [Fact]
    public async Task Rolling_back_installs_nothing()
    {
        // Откат — та же операция, и файла-источника он не требует.
        var store = new RecordingStore
        {
            Versions = { "vinn-axial-2.0.0", "vinn-axial-2.1.0" },
            Active = "vinn-axial-2.1.0",
        };

        var outcome = await Create(store, new RecordingAudit())
            .ExecuteAsync("vinn-axial-2.0.0", Administrator, CancellationToken.None);

        Assert.Equal(ModelActivationOutcome.Activated, outcome.Outcome);
        Assert.Equal("vinn-axial-2.0.0", store.Active);
        Assert.Empty(store.Installed);
        Assert.Equal(["vinn-axial-2.0.0", "vinn-axial-2.1.0"], store.InstalledVersions());
    }

    [Fact]
    public async Task An_empty_previous_version_is_not_mistaken_for_one()
    {
        var audit = new RecordingAudit();
        var store = new RecordingStore { Versions = { "vinn-axial-2.0.0" } };

        await Create(store, audit)
            .ExecuteAsync("vinn-axial-2.0.0", Administrator, CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(audit.Events).ModelActivation?.FromVersion);
    }

    [Fact]
    public async Task A_version_that_is_not_installed_is_refused()
    {
        var audit = new RecordingAudit();
        var store = new RecordingStore { Versions = { "vinn-axial-2.0.0" } };

        var outcome = await Create(store, audit)
            .ExecuteAsync("vinn-axial-9.9.9", Administrator, CancellationToken.None);

        Assert.Equal(ModelActivationOutcome.NotInstalled, outcome.Outcome);
        Assert.Null(store.Active);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Activating_the_active_version_changes_nothing()
    {
        // «Версия сменена с X на X» читается как событие, которого не было.
        var audit = new RecordingAudit();
        var store = new RecordingStore
        {
            Versions = { "vinn-axial-2.0.0" },
            Active = "vinn-axial-2.0.0",
        };

        var outcome = await Create(store, audit)
            .ExecuteAsync("vinn-axial-2.0.0", Administrator, CancellationToken.None);

        Assert.Equal(ModelActivationOutcome.AlreadyActive, outcome.Outcome);
        Assert.Empty(audit.Events);
        Assert.Empty(store.Activated);
    }

    [Fact]
    public async Task A_package_spoiled_in_the_store_does_not_become_active()
    {
        // Проверяется именно тот файл, которым будут измерять. Иначе порча
        // обнаружилась бы отказом анализа при следующем запуске — то есть
        // у врача, а не у администратора.
        var audit = new RecordingAudit();
        var store = new RecordingStore
        {
            Versions = { "vinn-axial-2.0.0", "vinn-axial-2.1.0" },
            Active = "vinn-axial-2.0.0",
        };

        var useCase = new ActivateModelVersionUseCase(
            new StubReader(new ModelPackageCheck(
                null, ModelPackageRejection.ContentAltered, "segmentation.onnx")),
            store,
            audit,
            TimeProvider.System);

        var outcome = await useCase.ExecuteAsync(
            "vinn-axial-2.1.0", Administrator, CancellationToken.None);

        Assert.Equal(ModelActivationOutcome.PackageRefused, outcome.Outcome);
        Assert.Equal(ModelPackageRejection.ContentAltered, outcome.Rejection);

        // Метка осталась на прежней версии: подставлять другой пакет молча
        // ADR 0008 запрещает, а сбрасывать действующую версию — тем более.
        Assert.Equal("vinn-axial-2.0.0", store.Active);
        Assert.Empty(store.Activated);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.ModelPackageRefused, recorded.Code);
        Assert.Equal("vinn-axial-2.1.0", recorded.ModelVersion);
    }

    [Fact]
    public async Task A_file_holding_another_version_does_not_become_active()
    {
        // Версия в хранилище — это имя файла, а измеряет приложение той,
        // которую называет объявление внутри пакета. Пока этого экрана не
        // было, пакет клали под нужным именем руками, и разойтись эти две
        // версии могут. Журнал сказал бы тогда одно, а отчёт — другое.
        var audit = new RecordingAudit();
        var store = new RecordingStore
        {
            Versions = { "vinn-axial-2.0.0", "vinn-axial-2.1.0" },
            Active = "vinn-axial-2.0.0",
        };

        var useCase = new ActivateModelVersionUseCase(
            new StubReader(new ModelPackageCheck(ManifestFor("vinn-axial-1.0.0"), null)
            {
                Signature = ModelPackageSignature.Valid,
            }),
            store,
            audit,
            TimeProvider.System);

        var outcome = await useCase.ExecuteAsync(
            "vinn-axial-2.1.0", Administrator, CancellationToken.None);

        Assert.Equal(ModelActivationOutcome.VersionMismatch, outcome.Outcome);
        Assert.Equal("vinn-axial-1.0.0", outcome.Detail);
        Assert.Equal("vinn-axial-2.0.0", store.Active);
        Assert.Empty(store.Activated);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.ModelPackageRefused, recorded.Code);
        Assert.Equal("vinn-axial-2.1.0", recorded.ModelVersion);

        // Причины отказа нет: пакет проверку прошёл, не сошлось объявление
        // с именем файла, и названо оно уточнением.
        Assert.Null(recorded.ModelPackage?.Rejection);
        Assert.Equal("vinn-axial-1.0.0", recorded.ModelPackage?.Detail);
    }

    [Fact]
    public async Task A_clinician_may_not_switch_the_version()
    {
        var store = new RecordingStore
        {
            Versions = { "vinn-axial-2.0.0" },
            Active = "vinn-axial-2.1.0",
        };

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Create(store, new RecordingAudit())
                .ExecuteAsync("vinn-axial-2.0.0", Clinician, CancellationToken.None));
    }

    private static ActivateModelVersionUseCase Create(IInstalledModelStore store, IAuditLog audit) =>
        new(new PathReader(), store, audit, TimeProvider.System);

    private static ModelPackageManifest ManifestFor(string version) => new(
        FormatVersion: "1",
        ModelVersion: version,
        MinimumApplicationVersion: "1.0.0",
        PreprocessingVersion: "conform-lia-256-1",
        LabelMapVersion: "fastsurfer-vinn-axial-2.0.0",
        SigningKeyId: "release-2026");

    /// <summary>
    /// Отвечает за тот пакет, о котором спросили.
    ///
    /// Один ответ на любой путь скрыл бы ровно то, что здесь проверяется:
    /// совпадает ли объявление внутри пакета с именем, под которым он лежит.
    /// </summary>
    private sealed class PathReader : IModelPackageReader
    {
        public ModelPackageCheck Verify(string packagePath) =>
            new(ManifestFor(Path.GetFileNameWithoutExtension(packagePath)), null)
            {
                Signature = ModelPackageSignature.Valid,
            };

        public ModelPackageCheck Open(string packagePath) => this.Verify(packagePath);
    }

    private sealed class StubReader(ModelPackageCheck check) : IModelPackageReader
    {
        public ModelPackageCheck Verify(string packagePath) => check;

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
