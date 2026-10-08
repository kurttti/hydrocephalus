using Hydrocephalus.Application;
using Hydrocephalus.Desktop.Administration;
using Hydrocephalus.Desktop.Results;
using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Экран модели.
///
/// Проверяется не вёрстка, а то, что экран не говорит о модели больше, чем
/// знает установка, и не меньше, чем требует ADR 0008: что измеряет сейчас,
/// что помечено действующим, какие версии установлены, чем подписан
/// действующий пакет и почему действие недоступно.
/// </summary>
public sealed class ModelReadoutTests
{
    private static readonly ModelPackageManifest Active = new(
        FormatVersion: "1",
        ModelVersion: "vinn-axial-2.0.0",
        MinimumApplicationVersion: "1.0.0",
        PreprocessingVersion: "conform-lia-256-1",
        LabelMapVersion: "fastsurfer-vinn-axial-2.0.0",
        SigningKeyId: "release-2026");

    [Fact]
    public void What_measures_now_and_what_is_marked_are_separate_lines()
    {
        // Способ разметки собирается один раз при запуске, и новая метка
        // начинает действовать только со следующего. Одна строка на оба
        // вопроса заставила бы администратора принять неизменившееся
        // значение за неудачу переключения — и переключить снова.
        var rows = ModelReadout.Rows(State() with
        {
            ActiveVersion = "vinn-axial-2.1.0",
            MeasuringNow = "t1:ventricles-model/vinn-axial-2.0.0|other:ventricles-threshold/baseline-1.4.0",
        });

        var now = Assert.Single(rows, row => row.Text.StartsWith(
            "Измеряет в этом запуске:", StringComparison.Ordinal));
        var marked = Assert.Single(rows, row => row.Text.StartsWith(
            "Помечена действующей:", StringComparison.Ordinal));

        Assert.Contains("vinn-axial-2.0.0", now.Text, StringComparison.Ordinal);
        Assert.Contains("vinn-axial-2.1.0", marked.Text, StringComparison.Ordinal);
        Assert.Contains("следующего запуска", marked.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void No_model_is_an_ordinary_state_rather_than_a_fault()
    {
        // Приложение поставляется без модели, установщик её не приносит
        // (ADR 0008), и до установки работает пороговый путь.
        var rows = ModelReadout.Rows(State());

        Assert.DoesNotContain(rows, row => row.Severity == ResultSeverity.Blocking);
        Assert.Contains(rows, row => row.Text.Contains("пороговым методом", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_active_package_blocks_and_does_not_offer_a_substitute()
    {
        // ADR 0008 требует остановиться до решения администратора, а не
        // посчитать молча другим способом.
        var rows = ModelReadout.Rows(State() with
        {
            ActiveVersion = "vinn-axial-2.0.0",
            InstalledVersions = ["vinn-axial-2.0.0"],
            ActiveRejection = ModelPackageRejection.ContentAltered,
            ActiveDetail = "segmentation.onnx",
        });

        var row = Assert.Single(rows, row => row.Severity == ResultSeverity.Blocking);

        Assert.Contains("хеш файла не совпал", row.Text, StringComparison.Ordinal);
        Assert.Contains("segmentation.onnx", row.Text, StringComparison.Ordinal);
        Assert.Contains("другим способом", row.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Installed_versions_are_named_together_with_the_rollback_rule()
    {
        var rows = ModelReadout.Rows(State() with
        {
            InstalledVersions = ["vinn-axial-2.0.0", "vinn-axial-2.1.0"],
        });

        var row = Assert.Single(rows, row => row.Text.StartsWith("Установлено:", StringComparison.Ordinal));

        Assert.Contains("vinn-axial-2.0.0", row.Text, StringComparison.Ordinal);
        Assert.Contains("vinn-axial-2.1.0", row.Text, StringComparison.Ordinal);
        Assert.Contains("не удаляет предыдущую", row.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_trust_key_is_named_as_the_reason_installation_is_unavailable()
    {
        // Выключенная кнопка без причины хуже её отсутствия: подпись
        // проверяется ключом, который приносит не пакет (ADR 0004).
        var rows = ModelReadout.Rows(State());

        Assert.Contains(rows, row =>
            row.Text.Contains("Доверенного ключа нет", StringComparison.Ordinal));
    }

    [Fact]
    public void An_open_study_is_named_as_the_reason_switching_is_unavailable()
    {
        var rows = ModelReadout.Rows(State() with { StudyOpen = true });

        var row = Assert.Single(rows, row =>
            row.Text.Contains("Пока открыто исследование", StringComparison.Ordinal));

        Assert.Contains("ADR 0008", row.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_path_of_the_store_does_not_name_the_account()
    {
        // Путь профиля содержит имя учётной записи, а в клинике оно обычно
        // образовано от фамилии сотрудника — рядом с псевдонимами в журнале.
        var root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hydrocephalus",
            "models");

        var rows = ModelReadout.Rows(State() with { ModelRoot = root });

        var row = Assert.Single(rows, row => row.Text.StartsWith("Пакеты модели:", StringComparison.Ordinal));

        Assert.Contains("%LOCALAPPDATA%", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirmation_shows_the_version_the_key_the_signature_and_the_card()
    {
        // ADR 0008 перечисляет это прямо: подтверждать установку администратор
        // должен, увидев, что именно он устанавливает.
        var sections = ModelReadout.DescribeInspection(
            new ModelPackageInspection(
                Accepted: true,
                Manifest: Active with { ModelVersion = "vinn-axial-2.1.0" },
                Signature: ModelPackageSignature.Valid,
                ModelCard: "# Карточка модели",
                Rejection: null,
                Detail: ""),
            Active);

        var package = Rows(sections, "Пакет");

        Assert.Contains(package, row => row.Text.Contains("vinn-axial-2.1.0", StringComparison.Ordinal));
        Assert.Contains(package, row => row.Text.Contains("release-2026", StringComparison.Ordinal));
        Assert.Contains(package, row => row.Text.Contains("сошлась", StringComparison.Ordinal));

        Assert.Contains(
            Rows(sections, "Карточка модели"),
            row => row.Text.Contains("# Карточка модели", StringComparison.Ordinal));
    }

    [Fact]
    public void Confirmation_names_the_differences_from_the_active_version()
    {
        var sections = ModelReadout.DescribeInspection(
            new ModelPackageInspection(
                Accepted: true,
                Manifest: Active with { ModelVersion = "vinn-axial-2.1.0", SigningKeyId = "release-2027" },
                Signature: ModelPackageSignature.Valid,
                ModelCard: "карточка",
                Rejection: null,
                Detail: ""),
            Active);

        var differences = Rows(sections, "Отличия от действующей версии");

        Assert.Contains(differences, row => row.Text.Contains(
            "vinn-axial-2.0.0 → vinn-axial-2.1.0", StringComparison.Ordinal));

        // Смена ключа подписи — смена того, кто отвечает за пакет, а не
        // очередная версия модели, и читается она отдельно.
        var key = Assert.Single(differences, row => row.Text.StartsWith(
            "Ключ подписи:", StringComparison.Ordinal));

        Assert.Equal(ResultSeverity.Warning, key.Severity);
    }

    [Fact]
    public void A_refused_package_gets_no_confirmation_to_offer()
    {
        var sections = ModelReadout.DescribeInspection(
            new ModelPackageInspection(
                Accepted: false,
                Manifest: Active,
                Signature: ModelPackageSignature.Invalid,
                ModelCard: "",
                Rejection: ModelPackageRejection.SignatureInvalid,
                Detail: ""),
            Active);

        var package = Rows(sections, "Пакет");

        Assert.Contains(package, row => row.Severity == ResultSeverity.Blocking);
        Assert.Contains(package, row => row.Text.Contains(
            "записан в журнал", StringComparison.Ordinal)
            || (row.Note ?? string.Empty).Contains("записан в журнал", StringComparison.Ordinal));

        // Отличий у отвергнутого пакета не показывается: сравнивать имело бы
        // смысл только то, что можно установить.
        Assert.DoesNotContain(
            sections,
            section => string.Equals(
                section.Title, "Отличия от действующей версии", StringComparison.Ordinal));
    }

    [Fact]
    public void A_switch_says_it_takes_effect_at_the_next_start()
    {
        var row = ModelReadout.Describe(new ModelVersionActivation(
            ModelActivationOutcome.Activated,
            "vinn-axial-2.0.0",
            "vinn-axial-2.1.0",
            null,
            ""));

        Assert.Contains("vinn-axial-2.1.0", row.Text, StringComparison.Ordinal);
        Assert.Contains("была vinn-axial-2.0.0", row.Text, StringComparison.Ordinal);
        Assert.Contains("следующего запуска", row.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_switch_says_the_marker_stayed()
    {
        var row = ModelReadout.Describe(new ModelVersionActivation(
            ModelActivationOutcome.PackageRefused,
            "vinn-axial-2.0.0",
            "vinn-axial-2.1.0",
            ModelPackageRejection.ContentAltered,
            "segmentation.onnx"));

        Assert.Equal(ResultSeverity.Blocking, row.Severity);
        Assert.Contains("осталась на прежней версии", row.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_holding_another_version_names_both()
    {
        var row = ModelReadout.Describe(new ModelVersionActivation(
            ModelActivationOutcome.VersionMismatch,
            "vinn-axial-2.0.0",
            "vinn-axial-2.1.0",
            null,
            "vinn-axial-1.0.0"));

        Assert.Equal(ResultSeverity.Blocking, row.Severity);
        Assert.Contains("vinn-axial-2.1.0", row.Text, StringComparison.Ordinal);
        Assert.Contains("vinn-axial-1.0.0", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_active_package_does_not_claim_this_run_is_blocked()
    {
        // Если на старте пакет проверку прошёл, веса уже в памяти и измерение
        // продолжается прежней версией: заблокирован будет следующий запуск.
        var rows = ModelReadout.Rows(State() with
        {
            ActiveVersion = "vinn-axial-2.0.0",
            InstalledVersions = ["vinn-axial-2.0.0"],
            ActiveRejection = ModelPackageRejection.ContentAltered,
        });

        var row = Assert.Single(rows, row => row.Severity == ResultSeverity.Blocking);

        Assert.Contains("Следующий запуск заблокирует", row.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Reinstalling_the_same_version_says_why_it_is_not_allowed()
    {
        var row = ModelReadout.Describe(new ModelPackageInstallation(
            ModelInstallOutcome.AlreadyInstalled, "vinn-axial-2.0.0", null, ""));

        Assert.Equal(ResultSeverity.Warning, row.Severity);
        Assert.Contains("под одним номером", row.Note!, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ResultRow> Rows(
        IReadOnlyList<ResultSection> sections,
        string title) =>
        sections.Single(section => string.Equals(section.Title, title, StringComparison.Ordinal)).Rows;

    private static ModelInstallationState State() => new()
    {
        InstalledVersions = [],
        ActiveVersion = null,
        MeasuringNow = "ventricles-threshold/baseline-1.4.0",
        ActiveManifest = null,
        ActiveSignature = ModelPackageSignature.NotChecked,
        ActiveRejection = null,
        ActiveDetail = "",
        TrustKeyConfigured = false,
        ModelRoot = @"C:\data\models",
        StudyOpen = false,
    };
}
