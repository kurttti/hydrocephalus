using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Геометрический контроль маски.
///
/// Истины здесь нет, поэтому проверяется внутренняя правдоподобность: числа
/// из неправдоподобной маски выглядят ровно так же, как из хорошей, и заметить
/// разницу по самому объёму невозможно.
/// </summary>
public sealed class MaskGeometryCheckTests
{
    private static readonly AnatomicalLabel Ventricles = new("lateral-ventricles");
    private static readonly AnatomicalLabel Brain = new("brain");

    private static readonly VolumeGrid Grid =
        new(new VolumeDimensions(20, 20, 20), 1.0, 1.0, 1.0);

    [Fact]
    public void A_plausible_mask_produces_no_findings()
    {
        // Обратная проверка: без неё «замечаний нет» может означать лишь то,
        // что проверка ничего не ищет.
        var mask = Mask(Blob(centre: 10, radius: 4, label: 1));

        Assert.Empty(MaskGeometryChecks.Inspect(mask, [Ventricles]));
    }

    [Fact]
    public void A_missing_required_structure_blocks()
    {
        // Подставить ноль вместо объёма значило бы выдать отсутствие
        // сегментации за отсутствие ткани.
        var mask = Mask(Blob(centre: 10, radius: 4, label: 2));

        var issue = Assert.Single(MaskGeometryChecks.Inspect(mask, [Ventricles]));

        Assert.Equal(QualityIssueCode.InconsistentGeometry, issue.Code);
        Assert.Equal(QualityIssueSeverity.Blocking, issue.Severity);
        Assert.Equal("requiredStructureMissing", issue.Parameters["reason"]);
        Assert.Equal(Ventricles.Code, issue.Parameters["structure"]);
    }

    [Fact]
    public void A_structure_that_is_absent_but_not_required_is_not_reported()
    {
        var mask = Mask(Blob(centre: 10, radius: 4, label: 1));

        Assert.Empty(MaskGeometryChecks.Inspect(mask, [Ventricles]));
    }

    [Fact]
    public void A_fragmented_structure_warns_without_blocking()
    {
        // Распад на десятки кусков означает шум сегментации, а не анатомию,
        // но анализ на этом не останавливается: маска может быть годной в целом.
        var labels = Blob(centre: 4, radius: 2, label: 1);

        Paint(labels, Blob(centre: 10, radius: 2, label: 1));
        Paint(labels, Blob(centre: 16, radius: 2, label: 1));

        var issue = Assert.Single(
            MaskGeometryChecks.Inspect(Mask(labels), [Ventricles]),
            item => item.Parameters.TryGetValue("reason", out var reason)
                && reason == "fragmentedStructure");

        Assert.Equal(QualityIssueSeverity.Warning, issue.Severity);
        Assert.Equal("3", issue.Parameters["components"]);
    }

    [Fact]
    public void Two_components_are_normal_for_a_paired_structure()
    {
        // Боковые желудочки — две компоненты; сообщать об этом было бы шумом.
        var labels = Blob(centre: 5, radius: 2, label: 1);

        Paint(labels, Blob(centre: 14, radius: 2, label: 1));

        Assert.Empty(MaskGeometryChecks.Inspect(Mask(labels), [Ventricles]));
    }

    [Fact]
    public void Speckle_below_the_size_limit_is_not_counted_as_a_component()
    {
        // Одиночные отсчёты на границе — обычный шум; считать их компонентами
        // значило бы сообщать о распаде на каждой маске.
        var labels = Blob(centre: 10, radius: 4, label: 1);

        labels[Offset(1, 1, 1)] = 1;
        labels[Offset(18, 18, 18)] = 1;

        Assert.DoesNotContain(
            MaskGeometryChecks.Inspect(Mask(labels), [Ventricles]),
            item => item.Parameters.TryGetValue("reason", out var reason)
                && reason == "fragmentedStructure");
    }

    [Fact]
    public void A_structure_touching_the_volume_boundary_warns()
    {
        // Обрезанная полем обзора структура даёт заниженный объём,
        // и по самому числу это не видно.
        var labels = new byte[20 * 20 * 20];

        for (var row = 8; row < 12; row++)
        {
            for (var slice = 8; slice < 12; slice++)
            {
                for (var column = 0; column < 4; column++)
                {
                    labels[Offset(column, row, slice)] = 1;
                }
            }
        }

        var issue = Assert.Single(
            MaskGeometryChecks.Inspect(Mask(labels), [Ventricles]),
            item => item.Code == QualityIssueCode.HeadTruncated);

        Assert.Equal(QualityIssueSeverity.Warning, issue.Severity);
        Assert.Equal(Ventricles.Code, issue.Parameters["structure"]);
    }

    [Fact]
    public void Components_are_counted_by_face_connectivity()
    {
        // Два куба, касающиеся только углом. По связности через грани это две
        // структуры; по связности через углы разделённые доли слились бы в одну.
        var labels = new byte[20 * 20 * 20];

        for (var offset = 0; offset < 3; offset++)
        {
            for (var second = 0; second < 3; second++)
            {
                for (var third = 0; third < 3; third++)
                {
                    labels[Offset(2 + offset, 2 + second, 2 + third)] = 1;
                    labels[Offset(5 + offset, 5 + second, 5 + third)] = 1;
                }
            }
        }

        Assert.Equal(2, MaskGeometryChecks.CountComponents(Mask(labels), 1, minComponentVoxels: 1));
    }

    [Fact]
    public void A_single_connected_structure_is_one_component()
    {
        Assert.Equal(
            1,
            MaskGeometryChecks.CountComponents(Mask(Blob(centre: 10, radius: 5, label: 1)), 1));
    }

    [Fact]
    public void Limits_are_configurable()
    {
        var labels = Blob(centre: 5, radius: 2, label: 1);

        Paint(labels, Blob(centre: 14, radius: 2, label: 1));

        var strict = new MaskGeometryLimits { MaxComponentsPerStructure = 1 };

        Assert.Contains(
            MaskGeometryChecks.Inspect(Mask(labels), [Ventricles], strict),
            item => item.Parameters.TryGetValue("reason", out var reason)
                && reason == "fragmentedStructure");
    }

    private static int Offset(int column, int row, int slice) =>
        (((slice * 20) + row) * 20) + column;

    private static byte[] Blob(int centre, int radius, byte label)
    {
        var labels = new byte[20 * 20 * 20];

        for (var slice = 0; slice < 20; slice++)
        {
            for (var row = 0; row < 20; row++)
            {
                for (var column = 0; column < 20; column++)
                {
                    var distance = Math.Max(
                        Math.Abs(column - centre),
                        Math.Max(Math.Abs(row - centre), Math.Abs(slice - centre)));

                    if (distance <= radius)
                    {
                        labels[Offset(column, row, slice)] = label;
                    }
                }
            }
        }

        return labels;
    }

    private static void Paint(byte[] target, byte[] source)
    {
        for (var index = 0; index < target.Length; index++)
        {
            if (source[index] != 0)
            {
                target[index] = source[index];
            }
        }
    }

    private static VoxelMask Mask(byte[] labels) => new(
        Grid,
        new LabelMap { Version = "1.0.0", Labels = [Ventricles, Brain] },
        labels);
}
