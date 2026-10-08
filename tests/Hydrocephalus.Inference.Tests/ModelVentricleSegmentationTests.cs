using System.Globalization;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Путь модели в конвейере: всё вокруг сети, кроме самой сети.
///
/// Разметка здесь подставная, и это главное свойство этих тестов. Веса в
/// репозиторий не входят, CI их не качает, а проверять нужно именно обвязку:
/// отказы, пороги, пометку происхождения и то, что непригодный вход до сети не
/// доходит. Сама сеть сверена с исходной моделью отдельно, на настоящих срезах.
///
/// Пороги правдоподобия взяты по измерению: у 150 здоровых серий IXI наименьший
/// объём, выданный моделью, — 6,6 мл, а два известных срыва на клинических
/// сериях дали 0,3 и 0,4 мл. Проверяется, что отказ ловит второе и пропускает
/// первое.
/// </summary>
public sealed class ModelVentricleSegmentationTests
{
    private const int Size = 64;
    private const double SpacingMillimetres = 3.0;

    [Fact]
    public void A_sound_mask_is_measured_and_marked_questionable()
    {
        var labelling = new FakeLabelling(ventricleVoxels: 400);

        var result = Segment(labelling);

        Assert.Equal(MeasurementQuality.Questionable, result.Quality);

        // Число вокселей здесь не проверяется: перенос в сетку серии идёт
        // ближайшим соседом, и точного соответствия заказанному подставка не
        // даёт. Проверяется то, что маска непуста и путь её принял.
        Assert.True(Count(result.Mask) > 0);
    }

    [Fact]
    public void A_sound_result_says_it_ran_on_one_view()
    {
        // Ослабленный режим сопровождает каждый успешный результат, а не только
        // сомнительные: один аксиальный вид из трёх — свойство этой установки,
        // и в отчёте оно должно быть видно всегда.
        var result = Segment(new FakeLabelling(ventricleVoxels: 400));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(QualityIssueSeverity.Warning, issue.Severity);
        Assert.Equal("ventricleModelSingleAxialView", issue.Parameters["reason"]);
    }

    [Fact]
    public void An_empty_mask_is_refused_as_nothing_found()
    {
        // «Ничего не найдено» и «слишком мало» — разные утверждения. Пустая
        // маска, выданная как годная, однажды уже увела разбор на месяц:
        // индекс отказывал с причиной «не найдены передние рога», хотя рога
        // не найдены не на снимке, а в пустоте.
        var result = Segment(new FakeLabelling(ventricleVoxels: 0));

        Assert.Equal(MeasurementQuality.Unreliable, result.Quality);
        Assert.Equal(
            "ventricularSystemNotFound",
            Assert.Single(result.Issues).Parameters["reason"]);
    }

    [Fact]
    public void An_implausibly_small_mask_is_refused_with_its_volume_named()
    {
        // Два срыва модели на клинических сериях дали 210 и 301 воксель. Здесь
        // 100 вокселей — 2,7 мл, тот же порядок.
        var result = Segment(new FakeLabelling(ventricleVoxels: 100));

        Assert.Equal(MeasurementQuality.Unreliable, result.Quality);

        var issue = Assert.Single(result.Issues);

        Assert.Equal("ventricularSystemImplausiblySmall", issue.Parameters["reason"]);

        // Объём назван и лежит ниже порога. Точное значение не проверяется —
        // оно зависит от переноса в сетку серии, а утверждение теста о порядке
        // величины, а не о его арифметике.
        var millilitres = double.Parse(
            issue.Parameters["millilitres"], CultureInfo.InvariantCulture);

        Assert.True(millilitres > 0 && millilitres < 5);
    }

    [Fact]
    public void A_series_that_is_not_T1_is_refused_before_the_network_runs()
    {
        // На T2 ликвор яркий — обратен тому, на чём модель обучена. Отказ до
        // работы, а не отбрасывание результата после: иначе на отказ уходила бы
        // минута счёта, и соблазн «всё-таки показать число» остался бы в коде.
        var labelling = new FakeLabelling(ventricleVoxels: 400);

        var result = Segment(labelling, SeriesWeighting.T2);

        Assert.Equal(MeasurementQuality.Unreliable, result.Quality);
        Assert.Equal("ventricleModelNeedsT1", Assert.Single(result.Issues).Parameters["reason"]);
        Assert.Equal(0, labelling.Calls);
    }

    [Fact]
    public void A_targeted_block_is_refused_before_the_network_runs()
    {
        // Прицельный блок модель размечает молча и неверно: желудочки в нём
        // обрезаны, и она возвращает правдоподобное малое число вместо отказа.
        var labelling = new FakeLabelling(ventricleVoxels: 400);
        var grid = new VolumeGrid(
            new VolumeDimensions(Size, Size, 20),
            SpacingMillimetres,
            SpacingMillimetres,
            SpacingMillimetres);

        var result = new ModelVentricleSegmentation(labelling, "vinn-axial-2.0.0")
            .Segment(new TestVolume(grid), SeriesWeighting.T1);

        var issue = Assert.Single(result.Issues);

        Assert.Equal("ventricleModelNeedsHeadCoverage", issue.Parameters["reason"]);
        Assert.Equal("60.0", issue.Parameters["coverageMm"]);
        Assert.Equal(0, labelling.Calls);
    }

    [Fact]
    public void The_mask_carries_the_version_of_the_labelling_that_made_it()
    {
        // Версия берётся у разметки, а не у константы одной реализации: иначе
        // смена модели оставила бы в отчёте прежнюю версию, и значения разных
        // моделей стали бы неразличимы.
        var result = Segment(new FakeLabelling(ventricleVoxels: 400));

        Assert.Equal(FakeLabelling.Version, result.Mask.Map.Version);
    }

    [Fact]
    public void Provenance_names_the_method_and_the_model_version()
    {
        var path = new ModelVentricleSegmentation(
            new FakeLabelling(ventricleVoxels: 400), "vinn-axial-2.0.0");

        Assert.Equal("ventricles-model/vinn-axial-2.0.0", path.Provenance);
    }

    [Fact]
    public void A_refusal_also_carries_a_mask_of_the_series_shape()
    {
        // Маска при отказе пуста, но не отсутствует: ниже по конвейеру она
        // идёт в измерения и в просмотрщик, и null там обернулся бы падением
        // вместо названной причины.
        var result = Segment(new FakeLabelling(ventricleVoxels: 0));

        Assert.Equal(Size, result.Mask.Grid.Dimensions.Slices);
        Assert.Equal(0, Count(result.Mask));
    }

    private static long Count(VoxelMask mask)
    {
        long count = 0;

        for (var slice = 0; slice < mask.Grid.Dimensions.Slices; slice++)
        {
            for (var row = 0; row < mask.Grid.Dimensions.Rows; row++)
            {
                for (var column = 0; column < mask.Grid.Dimensions.Columns; column++)
                {
                    if (mask[column, row, slice] != 0)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    private static VentricleSegmentationResult Segment(
        IConformedVolumeLabelling labelling,
        SeriesWeighting weighting = SeriesWeighting.T1) =>
        new ModelVentricleSegmentation(labelling, "vinn-axial-2.0.0")
            .Segment(Volume(), weighting);

    private static TestVolume Volume() => new(
        new VolumeGrid(
            new VolumeDimensions(Size, Size, Size),
            SpacingMillimetres,
            SpacingMillimetres,
            SpacingMillimetres));

    /// <summary>
    /// Подставная разметка: кладёт заданное число вокселей желудочков в середину
    /// приведённого куба, чтобы перенос в сетку серии их не потерял.
    /// </summary>
    private sealed class FakeLabelling(int ventricleVoxels) : IConformedVolumeLabelling
    {
        internal const string Version = "fake-labelling-1";

        public string LabelMapVersion => Version;

        internal int Calls { get; private set; }

        public byte[] Segment(
            byte[] conformed,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls++;

            var size = VolumeConforming.Size;
            var labels = new byte[size * size * size];

            // Куб размечается сплошным блоком у середины: сетка серии здесь
            // грубее приведённой, и разрозненные воксели перенос бы проредил.
            var side = (int)Math.Ceiling(Math.Cbrt(ventricleVoxels * 27.0));
            var start = (size - side) / 2;
            var placed = 0;

            for (var plane = start; plane < start + side && placed < ventricleVoxels * 27; plane++)
            {
                for (var row = start; row < start + side; row++)
                {
                    for (var column = start; column < start + side; column++)
                    {
                        labels[(((plane * size) + row) * size) + column] = 1;
                        placed++;
                    }
                }
            }

            return labels;
        }
    }

    private sealed class TestVolume : IVoxelVolume
    {
        public TestVolume(VolumeGrid grid)
        {
            this.Grid = grid;
            this.Geometry = new SeriesGeometry
            {
                AcquisitionType = MrAcquisitionType.ThreeDimensional,
                SliceThicknessMillimetres = grid.SliceSpacingMillimetres,
                SliceSpacingMillimetres = grid.SliceSpacingMillimetres,
                PixelSpacing = new InPlaneSpacing(
                    grid.ColumnSpacingMillimetres, grid.RowSpacingMillimetres),
                Dimensions = grid.Dimensions,
                RowDirection = new SpatialVector(1, 0, 0),
                ColumnDirection = new SpatialVector(0, 1, 0),
                Origin = default,
            };
        }

        public SeriesGeometry Geometry { get; }

        public VolumeGrid Grid { get; }

        public float Minimum => 0;

        public float Maximum => 1000;

        // Содержимое неважно: разметку даёт подставка, а приведение к
        // однородному объёму устойчиво.
        public float this[int column, int row, int slice] => 500;
    }
}
