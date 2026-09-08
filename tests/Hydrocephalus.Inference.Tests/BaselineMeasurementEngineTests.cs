using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Inference;
using Hydrocephalus.Inference.QualityControl;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Конвейер, измеряющий без модели.
///
/// Главное, что здесь проверяется, — не само число, а его пометка. Объём,
/// полученный неаттестованным методом и помеченный как надёжный, опаснее
/// отсутствующего объёма: он выглядит как измерение, которому можно верить.
/// </summary>
public sealed class BaselineMeasurementEngineTests
{
    private const int Size = 48;
    private const float Background = 0f;
    private const float Tissue = 300f;

    // Ликвор ярче ткани: серия помечена T2, и фантом обязан этому
    // соответствовать. Тёмный ликвор при T2 — это не «плохой фантом»,
    // а другая последовательность, и сегментация правильно ничего не найдёт.
    private const float Csf = 900f;

    [Fact]
    public async Task A_measurable_series_yields_a_ventricular_volume()
    {
        var result = await Analyse(Series());

        var biomarker = Assert.Single(result.Biomarkers);

        Assert.Equal(MeasurementUnit.Millilitre, biomarker.Unit);
        Assert.True(biomarker.Value > 0, "Объём желудочков на фантоме обязан быть положительным.");
    }

    [Fact]
    public async Task The_measurement_carries_the_quality_the_segmentation_vouches_for()
    {
        // Базовая сегментация не валидирована и помечает свой результат как
        // сомнительный. У RegionVolumes значение по умолчанию — «надёжно»,
        // и если движок забудет передать достоверность, отчёт получит
        // правдоподобное число с чужой пометкой.
        var result = await Analyse(Series());

        Assert.Equal(MeasurementQuality.Questionable, Assert.Single(result.Biomarkers).Quality);
    }

    [Fact]
    public async Task Classification_is_still_refused()
    {
        // Измерение не превращает конвейер в диагностический: проверенного
        // model package по-прежнему нет.
        var result = await Analyse(Series());

        var refused = Assert.IsType<AnalysisOutcome.Refused>(result.Outcome);

        Assert.Equal(RefusalCode.ModelPackageUnusable, refused.Reason.Code);
    }

    [Fact]
    public async Task A_baseline_series_is_not_measured()
    {
        // Объём считается по шагу сетки; на серии базового уровня он описывает
        // не полученную ткань, а объём, которым она представлена.
        var result = await Analyse(Series(tier: AcquisitionTier.Baseline));

        Assert.Empty(result.Biomarkers);
    }

    [Fact]
    public async Task A_series_of_unknown_weighting_is_not_measured()
    {
        // Направление порога определить нечем: ошибка выделит ткань вместо
        // ликвора и даст объём того же порядка с обратным смыслом.
        var result = await Analyse(Series(weighting: SeriesWeighting.Unknown));

        Assert.Empty(result.Biomarkers);
    }

    [Fact]
    public async Task An_unreadable_volume_does_not_take_the_report_down_with_it()
    {
        // Сжатый синтаксис передачи — обычное состояние реальной выгрузки.
        // Раньше такое исследование давало отчёт с результатом контроля
        // качества и обязано давать его и теперь.
        var result = await Analyse(
            Series(),
            new FailingVolumeSource(new InvalidDataException("compressed transfer syntax")));

        Assert.Empty(result.Biomarkers);
        Assert.IsType<AnalysisOutcome.Refused>(result.Outcome);
    }

    [Fact]
    public async Task A_defect_is_not_hidden_behind_an_empty_measurement()
    {
        // Обратная проверка: «измерений нет» не должно стать общим ответом
        // на любую беду. Обращение к уничтоженному сеансу рабочей копии —
        // это чтение данных, которых уже нет, и такой сбой обязан дойти наверх,
        // а не превратиться в пустой список.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Analyse(
                Series(),
                new FailingVolumeSource(new InvalidOperationException("working copy is gone"))));
    }

    [Fact]
    public async Task A_request_naming_an_absent_series_is_refused_outright()
    {
        var engine = new BaselineMeasurementEngine(
            new InputQualityControl(),
            new PhantomVolumeSource(),
            Pipeline());

        var request = new AnalysisRequest
        {
            Study = Study(Series()),
            PseudonymousSeriesId = "series-does-not-exist",
            VolumeReference = "working-copy/volume-0001",
        };

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => engine.AnalyzeAsync(request, progress: null, CancellationToken.None));
    }

    private static Task<AnalysisResult> Analyse(ImagingSeries series, IVolumeSource? source = null)
    {
        var engine = new BaselineMeasurementEngine(
            new InputQualityControl(),
            source ?? new PhantomVolumeSource(),
            Pipeline());

        var request = new AnalysisRequest
        {
            Study = Study(series),
            PseudonymousSeriesId = series.PseudonymousSeriesId,
            VolumeReference = "working-copy/volume-0001",
        };

        return engine.AnalyzeAsync(request, progress: null, CancellationToken.None);
    }

    private static ImagingStudy Study(ImagingSeries series) => new()
    {
        PseudonymousStudyId = "study-0001",
        PseudonymousSubjectId = "subject-0001",
        Series = [series],
    };

    private static ImagingSeries Series(
        AcquisitionTier tier = AcquisitionTier.Extended,
        SeriesWeighting weighting = SeriesWeighting.T2) => new()
        {
            PseudonymousSeriesId = "series-0001",
            Geometry = Geometry(tier),
            Weighting = weighting,
            IsContrastEnhanced = false,
        };

    private static SeriesGeometry Geometry(AcquisitionTier tier) => new()
    {
        // Уровень входа выводится доменной моделью из геометрии, а не задаётся
        // полем: базовый уровень получается толстым срезом, а не флагом.
        AcquisitionType = MrAcquisitionType.ThreeDimensional,
        SliceThicknessMillimetres = tier == AcquisitionTier.Extended ? 1.0 : 5.0,
        SliceSpacingMillimetres = tier == AcquisitionTier.Extended ? 1.0 : 5.0,
        PixelSpacing = new InPlaneSpacing(1.0, 1.0),
        Dimensions = new VolumeDimensions(Size, Size, Size),
        RowDirection = new SpatialVector(1, 0, 0),
        ColumnDirection = new SpatialVector(0, 1, 0),
        Origin = default,
    };

    private static PipelineIdentity Pipeline() => new()
    {
        PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
        FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,
        LabelMapVersion = PipelineIdentity.NotImplementedVersion,
        ApplicationCommitSha = "0000000000000000000000000000000000000000",
    };

    /// <summary>Источник, отдающий шар ликвора внутри шара ткани.</summary>
    private sealed class PhantomVolumeSource : IVolumeSource
    {
        public Task<IVoxelVolume> LoadAsync(
            string volumeReference,
            string pseudonymousSeriesId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IVoxelVolume>(new PhantomVolume());
    }

    /// <summary>Источник, который не может отдать объём.</summary>
    private sealed class FailingVolumeSource(Exception failure) : IVolumeSource
    {
        public Task<IVoxelVolume> LoadAsync(
            string volumeReference,
            string pseudonymousSeriesId,
            CancellationToken cancellationToken) => throw failure;
    }

    private sealed class PhantomVolume : IVoxelVolume
    {
        private readonly float[] voxels = Build();

        public SeriesGeometry Geometry { get; } = Geometry(AcquisitionTier.Extended);

        public VolumeGrid Grid { get; } =
            new(new VolumeDimensions(Size, Size, Size), 1.0, 1.0, 1.0);

        public float Minimum => Background;

        public float Maximum => Csf;

        public float this[int column, int row, int slice] =>
            this.voxels[(((slice * Size) + row) * Size) + column];

        private static float[] Build()
        {
            const int Centre = Size / 2;
            const int HeadRadius = 18;
            const int VentricleRadius = 6;

            var values = new float[Size * Size * Size];

            for (var slice = 0; slice < Size; slice++)
            {
                for (var row = 0; row < Size; row++)
                {
                    for (var column = 0; column < Size; column++)
                    {
                        var dc = column - Centre;
                        var dr = row - Centre;
                        var ds = slice - Centre;

                        var distance = Math.Sqrt((dc * dc) + (dr * dr) + (ds * ds));

                        values[(((slice * Size) + row) * Size) + column] = distance > HeadRadius
                            ? Background
                            : distance <= VentricleRadius ? Csf : Tissue;
                    }
                }
            }

            return values;
        }
    }
}
