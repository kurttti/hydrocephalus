using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Выбор способа разметки по тому, что установлено.
///
/// Три исхода неравнозначны, и путать их нельзя. Отсутствие модели — рядовое
/// состояние: приложение поставляется без неё. Отвергнутый пакет — повод
/// остановиться, а не повод тихо посчитать иначе: ADR 0008 запрещает
/// подставлять другой пакет молча, потому что врач не должен получить число,
/// посчитанное не тем способом, который называет экран.
///
/// Весов эти тесты не требуют: читатель пакета подставной, и до создания сети
/// дело доходит только там, где пакет принят, — такого случая здесь нет
/// намеренно, его проверяет прогон на настоящих весах.
/// </summary>
public sealed class VentricleSegmentationChoiceTests
{
    private static readonly ModelPackageManifest Manifest = new(
        FormatVersion: "1",
        ModelVersion: "vinn-axial-2.0.0",
        MinimumApplicationVersion: "1.0.0",
        PreprocessingVersion: "conform-lia-256-1",
        LabelMapVersion: "fastsurfer-vinn-axial-2.0.0",
        SigningKeyId: "test-key");

    [Fact]
    public void Without_a_package_the_threshold_path_works()
    {
        var chosen = VentricleSegmentationChoice.For(activePackagePath: null, reader: null);

        Assert.Equal(
            $"{ThresholdVentricleSegmentation.MethodCode}/{BaselineVentricleSegmentation.LabelMapVersion}",
            chosen.Provenance);
    }

    [Fact]
    public void Without_a_trusted_key_the_threshold_path_works()
    {
        // Пакет есть, проверить его нечем. Это не отказ пакета, а отсутствие
        // доверия к любому из них: блокировать анализ не за что.
        var chosen = VentricleSegmentationChoice.For("package.hcmp", reader: null);

        Assert.StartsWith(ThresholdVentricleSegmentation.MethodCode, chosen.Provenance, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_package_blocks_analysis_instead_of_falling_back()
    {
        var reader = new StubReader(
            new ModelPackageCheck(Manifest, ModelPackageRejection.ContentAltered, "segmentation.onnx"));

        var chosen = VentricleSegmentationChoice.For("package.hcmp", reader);

        Assert.Equal("ventricles-blocked", chosen.Provenance);
    }

    [Fact]
    public void A_blocked_path_names_the_rejection_without_measuring()
    {
        var reader = new StubReader(
            new ModelPackageCheck(Manifest, ModelPackageRejection.SignatureInvalid));

        var result = VentricleSegmentationChoice
            .For("package.hcmp", reader)
            .Segment(new TestVolume(), SeriesWeighting.T1, "series-1");

        Assert.Equal(MeasurementQuality.Unreliable, result.Quality);

        var issue = Assert.Single(result.Issues);

        Assert.Equal("modelPackageRefused", issue.Parameters["reason"]);
        Assert.Equal(nameof(ModelPackageRejection.SignatureInvalid), issue.Parameters["rejection"]);
    }

    [Fact]
    public void An_accepted_package_without_weights_blocks_too()
    {
        // Принятый пакет обязан отдать и объявление, и веса: разбор доходит до
        // них одним проходом. Если чего-то нет — это противоречие внутри
        // проверки, и продолжать на нём нельзя.
        var reader = new StubReader(new ModelPackageCheck(Manifest, null));

        var chosen = VentricleSegmentationChoice.For("package.hcmp", reader);

        Assert.Equal("ventricles-blocked", chosen.Provenance);
    }

    [Fact]
    public void Routing_sends_everything_but_T1_to_the_threshold_path()
    {
        // Модель проверена только на T1. Установка модели не должна отнимать у
        // T2 и FLAIR того, что уже считалось пороговым путём.
        var model = new NamedSegmentation("модель");
        var threshold = new NamedSegmentation("порог");
        var routed = new WeightingRoutedVentricleSegmentation(model, threshold);

        routed.Segment(new TestVolume(), SeriesWeighting.T1, "a");
        routed.Segment(new TestVolume(), SeriesWeighting.T2, "b");
        routed.Segment(new TestVolume(), SeriesWeighting.Flair, "c");
        routed.Segment(new TestVolume(), SeriesWeighting.Unknown, "d");

        Assert.Equal(1, model.Calls);
        Assert.Equal(3, threshold.Calls);
    }

    [Fact]
    public void Routing_names_both_ways()
    {
        var routed = new WeightingRoutedVentricleSegmentation(
            new NamedSegmentation("модель"), new NamedSegmentation("порог"));

        Assert.Equal("t1:модель|иначе:порог", routed.Provenance);
    }

    private sealed class StubReader(ModelPackageCheck check) : IModelPackageReader
    {
        public ModelPackageCheck Verify(string packagePath) => check;

        public ModelPackageCheck Open(string packagePath) => check;
    }

    private sealed class NamedSegmentation(string name) : IVentricleSegmentation
    {
        public string Provenance => name;

        internal int Calls { get; private set; }

        public VentricleSegmentationResult Segment(
            IVoxelVolume volume,
            SeriesWeighting weighting,
            string pseudonymousSeriesId,
            CancellationToken cancellationToken = default)
        {
            this.Calls++;

            var grid = volume.Grid;

            return new VentricleSegmentationResult(
                new VoxelMask(
                    grid,
                    new LabelMap { Version = name, Labels = [] },
                    new byte[grid.Dimensions.Columns * grid.Dimensions.Rows * grid.Dimensions.Slices]),
                [],
                MeasurementQuality.Questionable);
        }
    }

    private sealed class TestVolume : IVoxelVolume
    {
        private const int Size = 8;

        public SeriesGeometry Geometry { get; } = new()
        {
            AcquisitionType = MrAcquisitionType.ThreeDimensional,
            SliceThicknessMillimetres = 1.0,
            SliceSpacingMillimetres = 1.0,
            PixelSpacing = new InPlaneSpacing(1.0, 1.0),
            Dimensions = new VolumeDimensions(Size, Size, Size),
            RowDirection = new SpatialVector(1, 0, 0),
            ColumnDirection = new SpatialVector(0, 1, 0),
            Origin = default,
        };

        public VolumeGrid Grid { get; } =
            new(new VolumeDimensions(Size, Size, Size), 1.0, 1.0, 1.0);

        public float Minimum => 0;

        public float Maximum => 1;

        public float this[int column, int row, int slice] => 0;
    }
}
