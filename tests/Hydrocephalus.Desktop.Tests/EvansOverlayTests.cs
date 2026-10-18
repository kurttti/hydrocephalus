using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Отрезки индекса Эванса в маске разбора метода.
///
/// Врач проверяет индекс по тому, где стоят концы отрезков. Отрезки обязаны
/// лечь в те воксели, по которым посчитаны, и не стереть разбор под собой
/// нигде, кроме самой линии.
/// </summary>
public sealed class EvansOverlayTests
{
    private static readonly VolumeGrid Grid = new(new VolumeDimensions(20, 10, 6), 1.0, 1.0, 1.0);

    [Fact]
    public void Marks_draw_the_clicked_points_and_the_measured_segments()
    {
        // Врач проверяет постановку по снимку: точки своей меткой, отрезки —
        // своими, и ничего больше.
        var marks = EvansOverlay.Marks(Grid, [new VoxelPosition(6, 4, 2)], Segments());

        Assert.Equal(EvansOverlay.MarkedPointLabel, marks[6, 4, 2]);
        Assert.Equal(EvansOverlay.MarkedHornsLabel, marks[9, 4, 2]);
        Assert.Equal(EvansOverlay.MarkedSkullLabel, marks[9, 7, 2]);
        Assert.Equal(0, marks[9, 5, 2]);
        Assert.Equal(0, marks[9, 4, 3]);
    }

    [Fact]
    public void A_clicked_point_stays_visible_where_a_segment_crosses_it()
    {
        // Нажатие важнее линии, проведённой через него: иначе точка исчезает
        // ровно там, где её и надо проверить.
        var marks = EvansOverlay.Marks(Grid, [new VoxelPosition(9, 4, 2)], Segments());

        Assert.Equal(EvansOverlay.MarkedPointLabel, marks[9, 4, 2]);
    }

    [Fact]
    public void Marks_without_four_points_draw_only_the_points()
    {
        // Первые точки показываются сразу, до того как измерение возможно.
        var marks = EvansOverlay.Marks(Grid, [new VoxelPosition(3, 3, 1)], segments: null);

        Assert.Equal(EvansOverlay.MarkedPointLabel, marks[3, 3, 1]);
        Assert.Equal(0, marks[9, 7, 2]);
    }

    [Fact]
    public void A_point_at_the_edge_does_not_run_off_the_frame()
    {
        // Крест вокруг точки у края кадра обрезается, а не падает.
        var marks = EvansOverlay.Marks(Grid, [new VoxelPosition(0, 0, 0)], segments: null);

        Assert.Equal(EvansOverlay.MarkedPointLabel, marks[0, 0, 0]);
        Assert.Equal(EvansOverlay.MarkedPointLabel, marks[1, 0, 0]);
    }

    [Fact]
    public void Marks_name_their_labels_so_the_screen_can_colour_them()
    {
        var marks = EvansOverlay.Marks(Grid, [], Segments());

        Assert.Equal(EvansOverlay.MarkedHorns, marks.Map.StructureOf(EvansOverlay.MarkedHornsLabel));
        Assert.Equal(EvansOverlay.MarkedSkull, marks.Map.StructureOf(EvansOverlay.MarkedSkullLabel));
        Assert.Equal(EvansOverlay.MarkedPoint, marks.Map.StructureOf(EvansOverlay.MarkedPointLabel));
    }

    [Fact]
    public void Segments_are_drawn_on_their_plane_with_their_own_labels()
    {
        var review = Review();

        var drawn = EvansOverlay.Draw(review, Segments());

        for (var column = 6; column <= 12; column++)
        {
            Assert.Equal(EvansOverlay.FrontalHornsLabel, drawn[column, 4, 2]);
        }

        for (var column = 1; column <= 18; column++)
        {
            Assert.Equal(EvansOverlay.InnerSkullLabel, drawn[column, 7, 2]);
        }

        // Разбор вне линий сохранён, соседний срез не тронут.
        Assert.Equal(1, drawn[9, 5, 2]);
        Assert.Equal(0, drawn[9, 4, 3]);
    }

    [Fact]
    public void The_review_itself_is_left_unchanged()
    {
        var review = Review();

        _ = EvansOverlay.Draw(review, Segments());

        Assert.Equal(0, review[9, 4, 2]);
        Assert.Equal(1, review[9, 5, 2]);
    }

    [Fact]
    public void The_label_map_names_the_segments_even_without_a_discarded_label()
    {
        var drawn = EvansOverlay.Draw(Review(), Segments());

        Assert.Equal(EvansOverlay.FrontalHorns, drawn.Map.StructureOf(EvansOverlay.FrontalHornsLabel));
        Assert.Equal(EvansOverlay.InnerSkull, drawn.Map.StructureOf(EvansOverlay.InnerSkullLabel));
        Assert.Equal(BaselineVentricleSegmentation.VentricularSystem, drawn.Map.StructureOf(1));
    }

    private static VoxelMask Review()
    {
        var labels = new byte[20 * 10 * 6];

        // Один воксель выбранного: срез 2, строка 5, столбец 9.
        labels[(((2 * 10) + 5) * 20) + 9] = 1;

        return new VoxelMask(
            Grid,
            new LabelMap
            {
                Version = BaselineVentricleSegmentation.LabelMapVersion,
                Labels = [BaselineVentricleSegmentation.VentricularSystem],
            },
            labels);
    }

    private static EvansSegments Segments() =>
        new(
            VolumeAxis.AcrossSlices,
            2,
            new VoxelPosition(5.5, 4, 2),
            new VoxelPosition(12.5, 4, 2),
            new VoxelPosition(0.5, 7, 2),
            new VoxelPosition(18.5, 7, 2));
}
