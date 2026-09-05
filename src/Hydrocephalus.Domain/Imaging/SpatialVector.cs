namespace Hydrocephalus.Domain.Imaging;

/// <summary>
/// Вектор в системе координат пациента (мм). Используется для направляющих косинусов
/// и положения среза; double, а не float, потому что DICOM хранит геометрию
/// десятичными строками и потеря точности искажает ресэмплинг.
/// </summary>
/// <param name="X">Компонента вдоль оси X пациента.</param>
/// <param name="Y">Компонента вдоль оси Y пациента.</param>
/// <param name="Z">Компонента вдоль оси Z пациента.</param>
public readonly record struct SpatialVector(double X, double Y, double Z)
{
    /// <summary>
    /// Евклидова длина вектора.
    /// </summary>
    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    /// <summary>Скалярное произведение.</summary>
    /// <param name="other">Второй вектор.</param>
    /// <returns>Скалярное произведение.</returns>
    public double Dot(SpatialVector other) =>
        (X * other.X) + (Y * other.Y) + (Z * other.Z);

    /// <summary>
    /// Векторное произведение. Нормаль среза получается из направляющих косинусов
    /// строки и столбца именно так; брать вместо неё ось Z нельзя — наклонные
    /// и корональные серии дадут неверный шаг между срезами.
    /// </summary>
    /// <param name="other">Второй вектор.</param>
    /// <returns>Векторное произведение.</returns>
    public SpatialVector Cross(SpatialVector other) =>
        new(
            (Y * other.Z) - (Z * other.Y),
            (Z * other.X) - (X * other.Z),
            (X * other.Y) - (Y * other.X));

    /// <summary>Возвращает вектор единичной длины либо нулевой вектор.</summary>
    /// <returns>Нормированный вектор.</returns>
    public SpatialVector Normalized()
    {
        var length = Length;

        return length > 0 ? new SpatialVector(X / length, Y / length, Z / length) : default;
    }
}
