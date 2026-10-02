using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Segmentation;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Маска ликвора на срезе в двух видах.
/// </summary>
/// <param name="Raw">Как выделено по яркости: желудочки, борозды и щель одним куском.</param>
/// <param name="Opened">Она же после размыкания по толщине: тонкие связи разорваны.</param>
public sealed record SliceCsfMasks(VoxelMask Raw, VoxelMask Opened);

/// <summary>
/// Ликвор, выделенный на каждом осевом срезе по отдельности.
///
/// Это **не** сегментация желудочков: разделять желудочковый ликвор и ликвор
/// борозд здесь нечем, и маска содержит и тот и другой. Нужна она линейному
/// измерению, которое само отбирает нужное: индекс Эванса ищет область,
/// подходящую к средней линии с обеих сторон, в заданном окне спереди назад.
///
/// Зачем отдельно от <see cref="BaselineVentricleSegmentation"/>. Та отбирает
/// желудочки связностью в трёх измерениях, и на выборке это не работает:
/// расширенные желудочки соединены с бороздами и цистернами через частичный
/// объём поперёк срезов, вся система выходит одной областью, касающейся
/// поверхности, и правило глубины отбрасывает её целиком — остаются фрагменты
/// 0–4,7 мл (docs/data/README.md).
///
/// Голова на срезе определяется построчно — от первого до последнего отсчёта
/// ярче порога фона. Заливкой от края кадра, как в объёмной сегментации, здесь
/// не пользуются намеренно: она протекает внутрь по любой тёмной щели, и ровно
/// это приходилось закрывать раздуванием.
/// </summary>
public static class SliceCerebrospinalFluid
{
    /// <summary>Метка ликвора в маске.</summary>
    public static readonly AnatomicalLabel CerebrospinalFluid = new("cerebrospinal-fluid");

    /// <summary>Версия карты меток.</summary>
    public const string LabelMapVersion = "slice-csf-1.0.0";

    /// <summary>
    /// Нижняя граница яркости ликвора, долей порога фона.
    ///
    /// «Темнее порога фона внутри головы» — это не только ликвор: так же
    /// выглядят кость и воздух пазух. Объёмная сегментация отсекает их глубиной,
    /// здесь глубины нет, и нижняя граница отделяет их по яркости.
    ///
    /// Величина взята из измеренного: у ликвора желудочков яркость 0,40–0,43
    /// порога фона, у клинических ядер 0,30–0,50, у воздуха пазух и сосцевидных
    /// отростков 0,13–0,16 (docs/data/README.md). Граница 0,25 лежит ниже
    /// ликвора и выше воздуха.
    /// </summary>
    public const double CsfFloorOfBackground = 0.25;

    /// <summary>
    /// Выделяет ликвор на каждом срезе, перпендикулярном оси.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="axis">Ось, поперёк которой лежат осевые срезы.</param>
    /// <param name="darkCsf">Тёмный ли ликвор: так на T1 и FLAIR, не так на T2.</param>
    /// <param name="openingRadiusMillimetres">Радиус размыкания по толщине.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Маски ликвора либо <see langword="null"/>, если головы не нашлось.</returns>
    public static SliceCsfMasks? Build(
        IVoxelVolume volume,
        VolumeAxis axis,
        bool darkCsf,
        double openingRadiusMillimetres,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var grid = volume.Grid;
        var background = IntensityThresholds.Otsu(volume);
        var inside = InsideHead(volume, axis, background, cancellationToken);

        if (!Array.Exists(inside, value => value))
        {
            return null;
        }

        // Верхняя граница у тёмного ликвора — порог фона, а не тканевый. Внутри
        // головы Оцу делит серое и белое вещество, а не ликвор и ткань: классов
        // три, а метод предполагает два. Тканевый порог забирает около шестидесяти
        // процентов головы, и ширина «рогов» выходит во всю голову.
        var ceiling = darkCsf ? background : IntensityThresholds.OtsuWithin(volume, inside);
        var floor = background * CsfFloorOfBackground;
        var labels = new byte[inside.Length];
        var dimensions = grid.Dimensions;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var offset = Offset(dimensions, column, row, slice);

                    if (!inside[offset])
                    {
                        continue;
                    }

                    var value = volume[column, row, slice];
                    var isCsf = darkCsf ? value < ceiling && value > floor : value > ceiling;

                    labels[offset] = isCsf ? (byte)1 : (byte)0;
                }
            }
        }

        var map = new LabelMap { Version = LabelMapVersion, Labels = [CerebrospinalFluid] };
        var openedLabels = (byte[])labels.Clone();

        SliceOpening.Apply(openedLabels, grid, axis, openingRadiusMillimetres, cancellationToken);

        return new SliceCsfMasks(
            new VoxelMask(grid, map, labels),
            new VoxelMask(grid, map, openedLabels));
    }

    /// <summary>
    /// Отсчёты внутри головы: на каждом срезе построчно, от первого яркого до
    /// последнего.
    ///
    /// Строка, а не заливка: заливка от края кадра проходит внутрь по тёмной
    /// щели, дотянувшейся до края, и дальше растекается по субарахноидальному
    /// пространству. Отрезок между крайними яркими отсчётами строки такого
    /// изъяна не имеет, а голова на срезе выпукла настолько, что он её описывает.
    /// </summary>
    private static bool[] InsideHead(
        IVoxelVolume volume,
        VolumeAxis axis,
        double background,
        CancellationToken cancellationToken)
    {
        var grid = volume.Grid;
        var dimensions = grid.Dimensions;
        var extent = PlaneAddressing.ExtentOf(grid, axis);
        var planes = PlaneAddressing.CountAlong(grid, axis);
        var inside = new bool[dimensions.Columns * dimensions.Rows * dimensions.Slices];

        for (var plane = 0; plane < planes; plane++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var y = 0; y < extent.Height; y++)
            {
                var first = -1;
                var last = -1;

                for (var x = 0; x < extent.Width; x++)
                {
                    var (column, row, slice) = PlaneAddressing.Locate(axis, plane, x, y);

                    if (volume[column, row, slice] > background)
                    {
                        first = first < 0 ? x : first;
                        last = x;
                    }
                }

                for (var x = first; x >= 0 && x <= last; x++)
                {
                    var (column, row, slice) = PlaneAddressing.Locate(axis, plane, x, y);

                    inside[Offset(dimensions, column, row, slice)] = true;
                }
            }
        }

        return inside;
    }

    private static int Offset(VolumeDimensions dimensions, int column, int row, int slice) =>
        (((slice * dimensions.Rows) + row) * dimensions.Columns) + column;
}
