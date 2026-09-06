using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Классическая сегментация желудочков на фантоме с известным ответом.
///
/// Фантом устроен как голова: тёмный фон, ткань, ликворная полость в центре
/// и — в части случаев — ликвор на периферии, который метод обязан отбросить.
/// Тесты фиксируют и то, что метод находит, и то, чего он заведомо не умеет:
/// граница метода должна быть видна из тестов, а не обнаруживаться на пациенте.
/// </summary>
public sealed class BaselineSegmentationTests
{
    private const int Size = 48;

    private const float Background = 0f;
    private const float Tissue = 800f;
    private const float CsfOnT1 = 100f;
    private const float CsfOnT2 = 1600f;

    [Fact]
    public void The_central_cavity_is_found_on_t1()
    {
        // На T1 ликвор темнее ткани, но светлее фона: одного порога сверху мало.
        var volume = Phantom(csf: CsfOnT1, ventricleRadius: 6);

        var result = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1);

        AssertRecovers(result.Mask, ventricleRadius: 6);
    }

    [Fact]
    public void The_central_cavity_is_found_on_t2()
    {
        // На T2 соотношение обратное, и порог обязан пойти в другую сторону.
        var volume = Phantom(csf: CsfOnT2, ventricleRadius: 6);

        var result = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T2);

        AssertRecovers(result.Mask, ventricleRadius: 6);
    }

    [Fact]
    public void A_larger_cavity_yields_a_larger_mask()
    {
        // Метод должен реагировать на размер, а не выдавать одно и то же:
        // без этой проверки постоянная маска прошла бы все остальные тесты.
        var small = BaselineVentricleSegmentation.Segment(
            Phantom(csf: CsfOnT1, ventricleRadius: 4),
            SeriesWeighting.T1);

        var large = BaselineVentricleSegmentation.Segment(
            Phantom(csf: CsfOnT1, ventricleRadius: 8),
            SeriesWeighting.T1);

        Assert.True(
            Count(large.Mask) > Count(small.Mask) * 2,
            "A cavity of twice the radius must give a markedly larger mask.");
    }

    [Fact]
    public void Peripheral_cerebrospinal_fluid_is_discarded()
    {
        // Ликвор в бороздах по интенсивности неотличим от желудочкового;
        // различает их только расположение.
        var volume = Phantom(csf: CsfOnT1, ventricleRadius: 6, peripheralCsf: true);

        var result = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1);

        AssertRecovers(result.Mask, ventricleRadius: 6);

        Assert.Contains(
            result.Issues,
            issue => issue.Parameters.TryGetValue("reason", out var reason)
                && reason == "peripheralCsfDiscarded");
    }

    [Fact]
    public void The_result_is_always_marked_as_an_unvalidated_baseline()
    {
        // Замечание есть даже когда маска выглядит хорошо: иначе число из неё
        // в отчёте не отличить от проверенного.
        var result = BaselineVentricleSegmentation.Segment(
            Phantom(csf: CsfOnT1, ventricleRadius: 6),
            SeriesWeighting.T1);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == QualityIssueCode.OutOfDistribution
                && issue.Parameters["reason"] == "unvalidatedBaselineSegmentation");

        Assert.Equal(MeasurementQuality.Questionable, result.Quality);
    }

    [Fact]
    public void The_label_map_version_names_the_method()
    {
        // Результат baseline-сегментации не должен сравниваться с результатом
        // модели как одинаковый.
        var result = BaselineVentricleSegmentation.Segment(
            Phantom(csf: CsfOnT1, ventricleRadius: 6),
            SeriesWeighting.T1);

        Assert.Equal(BaselineVentricleSegmentation.LabelMapVersion, result.Mask.Map.Version);
        Assert.StartsWith("baseline-", result.Mask.Map.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_weighting_is_refused_rather_than_guessed()
    {
        // Ошибка направления порога выделит ткань вместо ликвора и даст объём
        // того же порядка с обратным смыслом.
        var volume = Phantom(csf: CsfOnT1, ventricleRadius: 6);

        Assert.Throws<DomainRuleViolationException>(
            () => BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.Unknown));
    }

    [Fact]
    public void A_uniform_volume_is_refused()
    {
        // Разделить нечего, и любой порог был бы выдумкой.
        var volume = new TestVolume(Grid(), (column, row, slice) => Tissue);

        Assert.Throws<DomainRuleViolationException>(
            () => BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1));
    }

    [Fact]
    public void The_mask_lies_on_the_grid_of_the_volume()
    {
        var volume = Phantom(csf: CsfOnT1, ventricleRadius: 6);

        var result = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1);

        Assert.True(result.Mask.Fits(volume));
    }

    [Fact]
    public void The_result_is_the_same_every_time()
    {
        var volume = Phantom(csf: CsfOnT1, ventricleRadius: 6);

        var first = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1);
        var again = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1);

        Assert.Equal(Count(first.Mask), Count(again.Mask));
    }

    [Fact]
    public void The_mask_survives_the_geometric_check()
    {
        var volume = Phantom(csf: CsfOnT1, ventricleRadius: 6);

        var result = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1);

        Assert.Empty(MaskGeometryChecks.Inspect(
            result.Mask,
            [BaselineVentricleSegmentation.VentricularSystem]));
    }

    [Fact]
    public void A_cavity_that_reaches_the_cortex_is_lost_rather_than_overstated()
    {
        // Граница метода, а не дефект. Когда полость дотягивается до поверхности,
        // порог видит её и наружный ликвор одной областью; отличить их
        // по расположению нельзя, и компонента отбрасывается целиком.
        // Так выглядит выраженная атрофия — то есть случай, ради которого
        // дифференциальный диагноз и нужен.
        //
        // Из двух возможных исходов это лучший: метод теряет структуру, а не
        // завышает объём. Отсутствие структуры ловится геометрическим контролем
        // и приводит к отказу, тогда как завышенный объём выглядел бы как находка.
        var volume = Phantom(csf: CsfOnT1, ventricleRadius: 6, connectedPeripheralCsf: true);

        var result = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1);

        Assert.Equal(0, Count(result.Mask));

        Assert.Contains(
            MaskGeometryChecks.Inspect(
                result.Mask,
                [BaselineVentricleSegmentation.VentricularSystem]),
            issue => issue.Severity == QualityIssueSeverity.Blocking
                && issue.Parameters["reason"] == "requiredStructureMissing");
    }

    [Fact]
    public void A_threshold_that_selects_most_of_the_head_is_reported_and_not_used()
    {
        // Внутри головы классов больше двух, и порог может встать между серым
        // и белым веществом. Отдать половину мозга как объём желудочков хуже,
        // чем не отдать ничего.
        var volume = Phantom(csf: CsfOnT1, ventricleRadius: 6);

        var permissive = new BaselineSegmentationOptions { MaxCsfFractionOfHead = 0.001 };

        var result = BaselineVentricleSegmentation.Segment(volume, SeriesWeighting.T1, permissive);

        Assert.Equal(0, Count(result.Mask));
        Assert.Equal(MeasurementQuality.Unreliable, result.Quality);

        Assert.Contains(
            result.Issues,
            issue => issue.Severity == QualityIssueSeverity.Blocking
                && issue.Parameters["reason"] == "thresholdDidNotIsolateCsf");
    }

    private static void AssertRecovers(VoxelMask mask, int ventricleRadius)
    {
        var expected = VoxelsWithin(ventricleRadius);
        var actual = Count(mask);

        // Порог по интенсивности не воспроизводит границу точно: проверяется
        // порядок величины, а не совпадение до отсчёта.
        Assert.InRange(actual, expected * 0.6, expected * 1.6);

        var centre = Size / 2;

        Assert.Equal(1, mask[centre, centre, centre]);
        Assert.Equal(0, mask[2, 2, 2]);
    }

    private static long Count(VoxelMask mask)
    {
        long count = 0;

        for (var slice = 0; slice < Size; slice++)
        {
            for (var row = 0; row < Size; row++)
            {
                for (var column = 0; column < Size; column++)
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

    private static long VoxelsWithin(int radius)
    {
        long count = 0;

        for (var slice = -radius; slice <= radius; slice++)
        {
            for (var row = -radius; row <= radius; row++)
            {
                for (var column = -radius; column <= radius; column++)
                {
                    if ((column * column) + (row * row) + (slice * slice) <= radius * radius)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    private static VolumeGrid Grid() =>
        new(new VolumeDimensions(Size, Size, Size), 1.0, 1.0, 1.0);

    private static TestVolume Phantom(
        float csf,
        int ventricleRadius,
        bool peripheralCsf = false,
        bool connectedPeripheralCsf = false)
    {
        const int Centre = Size / 2;
        const int HeadRadius = 20;

        return new TestVolume(Grid(), (column, row, slice) =>
        {
            var dc = column - Centre;
            var dr = row - Centre;
            var ds = slice - Centre;

            var distance = Math.Sqrt((dc * dc) + (dr * dr) + (ds * ds));

            if (distance > HeadRadius)
            {
                return Background;
            }

            if (distance <= ventricleRadius)
            {
                return csf;
            }

            // Тонкая ликворная прослойка у поверхности: по интенсивности такая же,
            // как желудочковая. Толщина реалистичная — субарахноидальный ликвор
            // занимает проценты внутричерепного объёма, а не его треть.
            if (peripheralCsf && distance is > HeadRadius - 2 and <= HeadRadius - 1)
            {
                return csf;
            }

            // Полость, дотянувшаяся до поверхности: канал от центра наружу,
            // из-за которого желудочек и наружный ликвор становятся одной областью.
            if (connectedPeripheralCsf && dc > 0 && Math.Abs(dr) < 3 && Math.Abs(ds) < 3)
            {
                return csf;
            }

            return Tissue;
        });
    }

    private sealed class TestVolume : IVoxelVolume
    {
        private readonly float[] voxels;

        public TestVolume(VolumeGrid grid, Func<int, int, int, float> value)
        {
            this.Grid = grid;
            this.Geometry = new SeriesGeometry
            {
                AcquisitionType = MrAcquisitionType.ThreeDimensional,
                SliceThicknessMillimetres = 1.0,
                SliceSpacingMillimetres = 1.0,
                PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                Dimensions = grid.Dimensions,
                RowDirection = new SpatialVector(1, 0, 0),
                ColumnDirection = new SpatialVector(0, 1, 0),
                Origin = default,
            };

            this.voxels = new float[Size * Size * Size];

            var minimum = float.PositiveInfinity;
            var maximum = float.NegativeInfinity;

            for (var slice = 0; slice < Size; slice++)
            {
                for (var row = 0; row < Size; row++)
                {
                    for (var column = 0; column < Size; column++)
                    {
                        var sample = value(column, row, slice);

                        this.voxels[(((slice * Size) + row) * Size) + column] = sample;
                        minimum = Math.Min(minimum, sample);
                        maximum = Math.Max(maximum, sample);
                    }
                }
            }

            this.Minimum = minimum;
            this.Maximum = maximum;
        }

        public SeriesGeometry Geometry { get; }

        public VolumeGrid Grid { get; }

        public float Minimum { get; }

        public float Maximum { get; }

        public float this[int column, int row, int slice] =>
            this.voxels[(((slice * Size) + row) * Size) + column];
    }
}
