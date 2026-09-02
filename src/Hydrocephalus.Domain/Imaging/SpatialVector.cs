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
}
