using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Measurements;

/// <summary>
/// Точка в сетке объёма. Дробная, а не целая: врач ставит метку курсором,
/// и округление до узла сетки на срезе 0.5мм даёт ошибку в полмиллиметра
/// на каждом конце отрезка.
/// </summary>
/// <param name="Column">Координата по столбцам.</param>
/// <param name="Row">Координата по строкам.</param>
/// <param name="Slice">Координата по срезам.</param>
public readonly record struct VoxelPosition(double Column, double Row, double Slice);

/// <summary>
/// Перевод координат сетки в систему координат пациента и измерения в ней.
///
/// Все расстояния и углы считаются в миллиметрах системы пациента, а не
/// в индексах вокселей. Разница не поправочный коэффициент: при анизотропном
/// вокселе отрезок одной длины в индексах имеет разную длину в миллиметрах
/// в зависимости от направления, а угол между отрезками в индексной системе
/// вообще не равен углу в анатомии. Индекс Эванса — отношение, поэтому на
/// изотропной сетке ошибка сокращается и не видна; на анизотропной — нет.
/// </summary>
public static class PatientSpace
{
    /// <summary>
    /// Переводит точку сетки в координаты пациента.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="position">Точка сетки.</param>
    /// <returns>Точка в миллиметрах системы координат пациента.</returns>
    public static SpatialVector ToPatient(IVoxelVolume volume, VoxelPosition position)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var geometry = volume.Geometry;
        var grid = volume.Grid;

        var row = geometry.RowDirection.Normalized();
        var column = geometry.ColumnDirection.Normalized();
        var normal = geometry.SliceNormal;

        // Направления берутся по соглашению DICOM: первая тройка направляющих
        // косинусов — рост номера столбца, вторая — рост номера строки.
        return Add(
            geometry.Origin,
            Add(
                Scale(row, position.Column * grid.ColumnSpacingMillimetres),
                Add(
                    Scale(column, position.Row * grid.RowSpacingMillimetres),
                    Scale(normal, position.Slice * grid.SliceSpacingMillimetres))));
    }

    /// <summary>
    /// Измеряет расстояние между точками.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="first">Первая точка.</param>
    /// <param name="second">Вторая точка.</param>
    /// <returns>Расстояние в миллиметрах.</returns>
    public static double DistanceMillimetres(
        IVoxelVolume volume,
        VoxelPosition first,
        VoxelPosition second)
    {
        var start = ToPatient(volume, first);
        var end = ToPatient(volume, second);

        return Subtract(end, start).Length;
    }

    /// <summary>
    /// Измеряет угол при вершине между двумя лучами.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="vertex">Вершина угла.</param>
    /// <param name="first">Точка на первом луче.</param>
    /// <param name="second">Точка на втором луче.</param>
    /// <returns>Угол в градусах от 0 до 180.</returns>
    /// <exception cref="DomainRuleViolationException">Если луч вырожден в точку.</exception>
    public static double AngleDegrees(
        IVoxelVolume volume,
        VoxelPosition vertex,
        VoxelPosition first,
        VoxelPosition second)
    {
        var origin = ToPatient(volume, vertex);
        var left = Subtract(ToPatient(volume, first), origin);
        var right = Subtract(ToPatient(volume, second), origin);

        if (left.Length == 0 || right.Length == 0)
        {
            // Совпавшие точки означают, что метка поставлена не туда;
            // вернуть здесь ноль градусов значило бы выдать измерение,
            // которого не делали.
            throw new DomainRuleViolationException(
                "An angle needs two rays of non-zero length.");
        }

        var cosine = left.Dot(right) / (left.Length * right.Length);

        // Прижатие к [-1, 1]: накопленная ошибка округления выводит косинус
        // за границы области определения арккосинуса и даёт NaN на совершенно
        // корректных входах.
        return double.RadiansToDegrees(Math.Acos(Math.Clamp(cosine, -1.0, 1.0)));
    }

    private static SpatialVector Add(SpatialVector first, SpatialVector second) =>
        new(first.X + second.X, first.Y + second.Y, first.Z + second.Z);

    private static SpatialVector Subtract(SpatialVector first, SpatialVector second) =>
        new(first.X - second.X, first.Y - second.Y, first.Z - second.Z);

    private static SpatialVector Scale(SpatialVector vector, double factor) =>
        new(vector.X * factor, vector.Y * factor, vector.Z * factor);
}
