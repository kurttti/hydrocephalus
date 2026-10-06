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
        var useCase = Create(new ModelPackageCheck(Manifest, null), audit);

        var outcome = await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.True(outcome.Accepted);
        Assert.Equal("vinn-axial-2.0.0", outcome.ModelVersion);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.ModelPackageLoaded, recorded.Code);
        Assert.Equal("vinn-axial-2.0.0", recorded.ModelVersion);
        Assert.Equal("release-2026", recorded.ModelPackage?.SigningKeyId);
        Assert.Equal(ModelPackageSignature.Valid, recorded.ModelPackage?.Signature);
    }

    [Fact]
    public async Task A_refused_package_is_recorded_too()
    {
        // Отказ пишется наравне с успехом: попытка подсунуть изменённый пакет —
        // ровно то событие, ради которого журнал ведётся.
        var audit = new RecordingAudit();
        var useCase = Create(
            new ModelPackageCheck(Manifest, ModelPackageRejection.ContentAltered, "segmentation.onnx"),
            audit);

        var outcome = await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.False(outcome.Accepted);
        Assert.Equal(ModelPackageRejection.ContentAltered, outcome.Rejection);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.ModelPackageRefused, recorded.Code);
        Assert.Equal("segmentation.onnx", recorded.ModelPackage?.Detail);
    }

    [Fact]
    public async Task A_broken_file_does_not_claim_the_signature_was_checked()
    {
        // «Не проверялась» и «неверна» — разные события: первое о повреждённом
        // файле, второе о подмене. Одно вместо другого увело бы разбор не туда.
        var audit = new RecordingAudit();
        var useCase = Create(new ModelPackageCheck(null, ModelPackageRejection.NotAPackage), audit);

        await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.Equal(ModelPackageSignature.NotChecked, Assert.Single(audit.Events).ModelPackage?.Signature);
    }

    [Fact]
    public async Task A_broken_signature_is_named_as_such()
    {
        var audit = new RecordingAudit();
        var useCase = Create(
            new ModelPackageCheck(Manifest, ModelPackageRejection.SignatureInvalid), audit);

        await useCase.ExecuteAsync("package.hcmp", Administrator, CancellationToken.None);

        Assert.Equal(ModelPackageSignature.Invalid, Assert.Single(audit.Events).ModelPackage?.Signature);
    }

    [Fact]
    public async Task A_clinician_may_not_install_a_model()
    {
        var audit = new RecordingAudit();
        var useCase = Create(new ModelPackageCheck(Manifest, null), audit);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => useCase.ExecuteAsync("package.hcmp", Clinician, CancellationToken.None));

        // Отказ в праве не читает пакет и не пишет о модели: событие о доступе
        // ведёт проверка прав, а не этот сценарий.
        Assert.Empty(audit.Events);
    }

    private static LoadModelPackageUseCase Create(ModelPackageCheck check, IAuditLog audit) =>
        new(new StubReader(check), audit, TimeProvider.System);

    private sealed class StubReader(ModelPackageCheck check) : IModelPackageReader
    {
        public ModelPackageCheck Verify(string packagePath) => check;
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
