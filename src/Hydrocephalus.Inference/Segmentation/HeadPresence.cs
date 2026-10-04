using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Голова ли на снимке — по размеру самой анатомии.
///
/// Нужна, потому что по тегам этого не узнать. В выборке нашлась абдоминальная
/// МРТ, у которой `BodyPartExamined` пуст, а `StudyDescription` объявляет
/// `TEMP^HEAD`, то есть снимок называет себя головой. Приёмка его принимала, и
/// отказывала лишь сегментация — с причиной «желудочки неправдоподобно малы»:
/// формально верной, по существу обманывающей, ведь желудочков в животе нет.
///
/// Поле зрения признаком не служит: в наборе есть головные серии с полем до
/// 470 мм, и тег у них тоже пуст. Касание края кадра — тоже: вокруг живота
/// чёрная рамка, он края не касается. А вот **размер анатомии** разделяет:
/// голова взрослого поперёк не превышает примерно 200 мм, туловище — за 300.
///
/// Порог взят из измерения: по 385 сериям набора, все головные, поперечник
/// укладывается в 282 мм; найденная абдоминальная серия даёт 323 мм.
/// </summary>
public static class HeadPresence
{
    /// <summary>
    /// Наибольший поперечник анатомии, при котором снимок считается головой, мм.
    ///
    /// Между наибольшей головой выборки (282 мм) и абдоминальной серией (323 мм)
    /// с запасом в обе стороны. Запас нужен: голова с шеей и плечами в кадре
    /// крупнее обычной, а туловище у худого пациента уже обычного.
    /// </summary>
    public const double MaxHeadExtentMillimetres = 300.0;

    /// <summary>
    /// Есть ли в объёме разброс яркости.
    /// </summary>
    private static bool HasContrast(
        IVoxelVolume volume,
        VolumeDimensions dimensions,
        CancellationToken cancellationToken)
    {
        var first = volume[0, 0, 0];

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    if (volume[column, row, slice] != first)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Похож ли объём на снимок головы.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Поперечник анатомии в миллиметрах и вердикт.</returns>
    public static (double ExtentMillimetres, bool LooksLikeHead) Measure(
        IVoxelVolume volume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var grid = volume.Grid;
        var dimensions = grid.Dimensions;

        // Однородный кадр: порога между воздухом и тканью в нём нет, и судить
        // не о чем. Это тоже изъян, но ловить его — дело других проверок;
        // здесь он не повод объявить снимок не головой.
        if (!HasContrast(volume, dimensions, cancellationToken))
        {
            return (0.0, true);
        }

        var background = IntensityThresholds.Otsu(volume);

        var firstColumn = int.MaxValue;
        var lastColumn = -1;
        var firstRow = int.MaxValue;
        var lastRow = -1;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    if (volume[column, row, slice] <= background)
                    {
                        continue;
                    }

                    firstColumn = Math.Min(firstColumn, column);
                    lastColumn = Math.Max(lastColumn, column);
                    firstRow = Math.Min(firstRow, row);
                    lastRow = Math.Max(lastRow, row);
                }
            }
        }

        if (lastColumn < 0)
        {
            // Кадр пуст: головы в нём нет, но и туловища тоже. Решать такое
            // должны проверки качества, а не эта.
            return (0.0, true);
        }

        var across = (lastColumn - firstColumn + 1) * grid.ColumnSpacingMillimetres;
        var alongRows = (lastRow - firstRow + 1) * grid.RowSpacingMillimetres;
        var extent = Math.Max(across, alongRows);

        return (extent, extent <= MaxHeadExtentMillimetres);
    }
}
