using System.Globalization;
using Hydrocephalus.Desktop.Results;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Экран результата.
///
/// Проверяются не формулировки, а то, что по тексту нельзя ошибиться: число
/// не отрывается от своей достоверности, выход за диапазон читается как ошибка
/// измерения, а не как находка у пациента, и отказ по отсутствию пакета модели
/// не выглядит поломкой установки.
/// </summary>
public sealed class ResultReadoutTests
{
    [Fact]
    public void A_measurement_carries_its_quality_in_the_same_line()
    {
        // Достоверность рядом, но в другой строке — это то же самое, что
        // достоверности нет: скопированное значение уйдёт без неё.
        var row = Assert.Single(Section("Измерения", Report(Volume())).Rows);

        Assert.Contains(Number(42.3), row.Text, StringComparison.Ordinal);
        Assert.Contains("мл", row.Text, StringComparison.Ordinal);
        Assert.Contains("сомнительно", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_measurement_names_the_method_that_produced_it()
    {
        var row = Assert.Single(Section("Измерения", Report(Volume())).Rows);

        Assert.Contains("volume.ventricular-system", row.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void An_implausible_value_is_called_an_error_of_measurement()
    {
        // Объём желудочков в 4 литра — это отказ метода, а не находка.
        // Текст, оставляющий второе прочтение, опаснее отсутствия числа.
        var row = Assert.Single(Section("Измерения", Report(Volume(value: 4000))).Rows);

        Assert.Contains("ошибку измерения", row.Note ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(ResultSeverity.Blocking, row.Severity);
    }

    [Fact]
    public void A_reliable_measurement_is_not_flagged()
    {
        var row = Assert.Single(
            Section("Измерения", Report(Volume(quality: MeasurementQuality.Reliable))).Rows);

        Assert.Equal(ResultSeverity.Neutral, row.Severity);
    }

    [Fact]
    public void An_absent_measurement_shows_the_series_instead_of_a_guess()
    {
        // Причины, по которой измерений нет, в отчёте не записано. Экран
        // называет свойства серии и не выдумывает объяснения.
        var row = Assert.Single(
            Section("Измерения", Report(), Series(AcquisitionTier.Baseline)).Rows);

        Assert.Contains("не выполнены", row.Text, StringComparison.Ordinal);
        Assert.Contains("базовый", row.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_model_package_does_not_read_as_a_broken_installation()
    {
        // Дословный перевод кода («пакет несовместим или не прошёл проверку»)
        // заставил бы врача заявить о неисправности исправной программы.
        var row = Assert.Single(Section("Итог", Report()).Rows);

        Assert.Equal(ResultSeverity.Neutral, row.Severity);
        Assert.Contains("намеренно", row.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_quality_control_is_shown_as_an_obstacle()
    {
        var row = Assert.Single(
            Section("Итог", Report(refusal: RefusalCode.QualityControlFailed)).Rows);

        Assert.Equal(ResultSeverity.Blocking, row.Severity);
    }

    [Fact]
    public void A_quality_issue_shows_the_numbers_that_make_it_actionable()
    {
        // «UnsupportedVoxelGeometry» врачу не говорит ничего. Толщина среза
        // и допустимая граница — говорят, и ровно для этого у замечания
        // есть параметры.
        var row = Assert.Single(Section("Контроль качества", Report(issue: ThickSlice())).Rows);

        // Числа из параметров записаны в инвариантной культуре, а на экране
        // обязаны выглядеть так же, как остальные числа интерфейса.
        Assert.Contains(Number(5), row.Text, StringComparison.Ordinal);
        Assert.Contains(Number(1.5), row.Text, StringComparison.Ordinal);
        Assert.Equal(ResultSeverity.Blocking, row.Severity);
    }

    [Fact]
    public void A_quality_issue_with_parameters_does_not_leak_the_raw_code()
    {
        var row = Assert.Single(Section("Контроль качества", Report(issue: ThickSlice())).Rows);

        Assert.DoesNotContain("UnsupportedVoxelGeometry", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sliceThickness", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_issue_that_does_not_yet_reach_the_report_is_still_rendered()
    {
        // Замечания разбора срезов при импорте и замечания сегментации
        // в отчёт сегодня не попадают: первые оставляют серию за пределами
        // рабочей копии, вторые движок не переносит в результат. Текст для них
        // проверяется здесь, чтобы заготовка не разошлась с параметрами молча.
        var row = Assert.Single(Section("Контроль качества", Report(issue: IrregularSpacing())).Rows);

        Assert.Contains(Number(5), row.Text, StringComparison.Ordinal);
        Assert.Contains(Number(3.2), row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_issue_is_named_by_its_code_rather_than_dropped()
    {
        // Замечание, для которого текста нет, обязано остаться видимым:
        // молчание об известной проблеме хуже непонятного кода.
        var issue = new QualityIssue
        {
            Code = QualityIssueCode.MotionArtefact,
            Severity = QualityIssueSeverity.Warning,
        };

        var row = Assert.Single(Section("Контроль качества", Report(issue: issue)).Rows);

        Assert.NotEqual(string.Empty, row.Text);
        Assert.Equal(ResultSeverity.Warning, row.Severity);
    }

    [Fact]
    public void A_clean_assessment_says_so_rather_than_showing_nothing()
    {
        var row = Assert.Single(Section("Контроль качества", Report()).Rows);

        Assert.Contains("нет", row.Text, StringComparison.Ordinal);
        Assert.Equal(ResultSeverity.Neutral, row.Severity);
    }

    [Fact]
    public void The_blocking_issues_behind_a_refusal_are_not_listed_twice()
    {
        // ContributingIssues — подмножество замечаний контроля качества.
        // Вторым списком те же находки выглядели бы как новые.
        var issue = ThickSlice();

        var report = Report(issue: issue, refusal: RefusalCode.QualityControlFailed);

        Assert.Single(Section("Контроль качества", report).Rows);
        Assert.Single(Section("Итог", report).Rows);
    }

    [Fact]
    public void A_series_that_never_reached_the_working_copy_is_still_listed()
    {
        // Врач, видящий одну серию, не может отличить «серия была одна»
        // от «было две, одну отбросили». Второе — повод посмотреть на выгрузку,
        // а не работать с остатком.
        var section = Section(
            "Серии исследования",
            Report(),
            excluded: [Excluded()]);

        Assert.Equal(2, section.Rows.Count);
        Assert.Contains("не загружена", section.Rows[1].Text, StringComparison.Ordinal);
        Assert.Equal(ResultSeverity.Blocking, section.Rows[1].Severity);
    }

    [Fact]
    public void The_reason_a_series_was_dropped_is_shown_with_it()
    {
        // Замечание разбора срезов при импорте до отчёта не доходит: отчёта
        // по отброшенной серии не существует. Единственное место, где врач
        // может его увидеть, — этот раздел.
        var section = Section("Серии исследования", Report(), excluded: [Excluded()]);

        var note = section.Rows[1].Note ?? string.Empty;

        Assert.Contains("плоскост", note, StringComparison.Ordinal);
        Assert.DoesNotContain("mixedOrientations", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_analysed_series_is_marked_among_the_others()
    {
        var section = Section("Серии исследования", Report());

        Assert.Contains("выбрана для анализа", Assert.Single(section.Rows).Text, StringComparison.Ordinal);
    }

    private static ExcludedSeries Excluded() => new()
    {
        Series = Series(AcquisitionTier.Baseline, "series-0002"),
        Issues =
        [
            new QualityIssue
            {
                Code = QualityIssueCode.InconsistentGeometry,
                Severity = QualityIssueSeverity.Blocking,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reason"] = "mixedOrientations",
                    ["instances"] = "60",
                    ["distinctOrientations"] = "3",
                },
            },
        ],
    };

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.CurrentCulture);

    private static ResultSection Section(
        string title,
        AnalysisReport report,
        ImagingSeries? series = null,
        IReadOnlyList<ExcludedSeries>? excluded = null)
    {
        var analysed = series ?? Series(AcquisitionTier.Extended);

        return ResultReadout.Describe(
                report,
                new AnalysedStudy
                {
                    Study = Study(analysed),
                    Analysed = analysed,
                    Excluded = excluded ?? [],
                })
            .Single(section => string.Equals(section.Title, title, StringComparison.Ordinal));
    }

    private static ImagingStudy Study(params ImagingSeries[] series) => new()
    {
        PseudonymousStudyId = "study-0001",
        PseudonymousSubjectId = "subject-0001",
        Series = series,
    };

    /// <summary>Замечание входного контроля качества: такие доходят до отчёта.</summary>
    private static QualityIssue ThickSlice() => new()
    {
        Code = QualityIssueCode.UnsupportedVoxelGeometry,
        Severity = QualityIssueSeverity.Blocking,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parameter"] = "sliceThickness",
            ["valueMm"] = "5",
            ["maxMm"] = "1.5",
        },
    };

    /// <summary>Замечание разбора срезов при импорте: до отчёта пока не доходит.</summary>
    private static QualityIssue IrregularSpacing() => new()
    {
        Code = QualityIssueCode.InconsistentGeometry,
        Severity = QualityIssueSeverity.Blocking,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = "irregularSliceSpacing",
            ["spacingMm"] = "5",
            ["maxDeviationMm"] = "3.2",
        },
    };

    private static AnalysisReport Report(
        Biomarker? biomarker = null,
        QualityIssue? issue = null,
        RefusalCode refusal = RefusalCode.ModelPackageUnusable) => new()
        {
            PseudonymousStudyId = "study-0001",
            CreatedAt = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
            Quality = new QualityAssessment { Issues = issue is null ? [] : [issue] },
            Outcome = new AnalysisOutcome.Refused
            {
                Reason = new RefusalReason
                {
                    Code = refusal,
                    ContributingIssues = issue is null || issue.Severity != QualityIssueSeverity.Blocking
                        ? []
                        : [issue],
                },
            },
            Pipeline = Pipeline(),
            Biomarkers = biomarker is null ? [] : [biomarker],
        };

    private static Biomarker Volume(
        double value = 42.3,
        MeasurementQuality quality = MeasurementQuality.Questionable) => Biomarker.Create(
        new MeasurementMethod
        {
            Code = "volume.ventricular-system",
            DefinitionVersion = "1.0.0",
            RequiredTier = AcquisitionTier.Extended,
        },
        AcquisitionTier.Extended,
        value,
        MeasurementUnit.Millilitre,
        quality,
        // Диапазон тот же, что у RegionVolumes: он ловит ошибку сегментации,
        // а не отклонение от нормы, и верхняя граница выше внутричерепного
        // объёма взрослого. Иначе каждый пациент с гидроцефалией получал бы
        // верное число с пометкой об ошибке измерения.
        new MeasurementRange(0.0, 2500.0));

    private static ImagingSeries Series(AcquisitionTier tier, string id = "series-0001") => new()
    {
        PseudonymousSeriesId = id,
        Geometry = new SeriesGeometry
        {
            AcquisitionType = MrAcquisitionType.ThreeDimensional,
            SliceThicknessMillimetres = tier == AcquisitionTier.Extended ? 1.0 : 5.0,
            SliceSpacingMillimetres = tier == AcquisitionTier.Extended ? 1.0 : 5.0,
            PixelSpacing = new InPlaneSpacing(1.0, 1.0),
            Dimensions = new VolumeDimensions(256, 256, 160),
            RowDirection = new SpatialVector(1, 0, 0),
            ColumnDirection = new SpatialVector(0, 1, 0),
            Origin = default,
        },
        Weighting = SeriesWeighting.T2,
        IsContrastEnhanced = false,
    };

    private static PipelineIdentity Pipeline() => new()
    {
        PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
        FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,
        LabelMapVersion = PipelineIdentity.NotImplementedVersion,
        ApplicationCommitSha = "0000000000000000000000000000000000000000",
    };
}
