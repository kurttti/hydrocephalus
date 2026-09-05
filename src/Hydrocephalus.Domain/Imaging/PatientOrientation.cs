namespace Hydrocephalus.Domain.Imaging;

/// <summary>
/// Анатомическое направление в системе координат пациента.
/// </summary>
public enum AnatomicalDirection
{
    /// <summary>Направление не определено.</summary>
    Unknown = 0,

    /// <summary>Вправо от пациента.</summary>
    Right = 1,

    /// <summary>Влево от пациента.</summary>
    Left = 2,

    /// <summary>Вперёд (к лицу).</summary>
    Anterior = 3,

    /// <summary>Назад (к затылку).</summary>
    Posterior = 4,

    /// <summary>Вверх (к темени).</summary>
    Superior = 5,

    /// <summary>Вниз (к шее).</summary>
    Inferior = 6,
}

/// <summary>
/// Плоскость получения серии.
/// </summary>
public enum ImagingPlane
{
    /// <summary>Плоскость не определена.</summary>
    Unknown = 0,

    /// <summary>Аксиальная: нормаль идёт вдоль оси «вверх-вниз».</summary>
    Axial = 1,

    /// <summary>Корональная: нормаль идёт вдоль оси «вперёд-назад».</summary>
    Coronal = 2,

    /// <summary>Сагиттальная: нормаль идёт вдоль оси «влево-вправо».</summary>
    Sagittal = 3,
}

/// <summary>
/// Подписи сторон изображения.
///
/// Каждая подпись означает направление, в котором находится соответствующая часть
/// тела при движении к этому краю изображения. Путаница сторон клинически значима
/// (ADR 0007), поэтому подписи выводятся из направляющих косинусов, а не задаются
/// по типу серии.
/// </summary>
/// <param name="Left">Направление у левого края изображения.</param>
/// <param name="Right">Направление у правого края изображения.</param>
/// <param name="Top">Направление у верхнего края изображения.</param>
/// <param name="Bottom">Направление у нижнего края изображения.</param>
/// <param name="IsOblique">
/// Признак того, что плоскость заметно наклонена и подписи приблизительны.
/// </param>
public readonly record struct EdgeLabels(
    AnatomicalDirection Left,
    AnatomicalDirection Right,
    AnatomicalDirection Top,
    AnatomicalDirection Bottom,
    bool IsOblique);

/// <summary>
/// Определение сторон изображения по направляющим косинусам DICOM.
///
/// Система координат пациента в DICOM — LPS: ось X растёт влево от пациента,
/// ось Y — назад, ось Z — вверх. Отсюда и берутся подписи; выводить их из типа
/// серии («аксиальная — значит так») нельзя, потому что серия может быть
/// перевёрнута или наклонена, и подпись обязана следовать за геометрией.
/// </summary>
public static class PatientOrientation
{
    /// <summary>
    /// Наименьшая доля преобладающей компоненты, при которой плоскость считается
    /// близкой к канонической. Ниже — подписи остаются приблизительными, и это
    /// нужно показать врачу, а не скрыть уверенной буквой.
    /// </summary>
    public const double ObliqueThreshold = 0.9;

    /// <summary>
    /// Определяет анатомическое направление вектора по преобладающей компоненте.
    /// </summary>
    /// <param name="direction">Вектор в системе координат пациента.</param>
    /// <returns>Анатомическое направление.</returns>
    public static AnatomicalDirection Of(SpatialVector direction)
    {
        var length = direction.Length;

        if (length == 0)
        {
            return AnatomicalDirection.Unknown;
        }

        var x = Math.Abs(direction.X);
        var y = Math.Abs(direction.Y);
        var z = Math.Abs(direction.Z);

        if (x >= y && x >= z)
        {
            return direction.X > 0 ? AnatomicalDirection.Left : AnatomicalDirection.Right;
        }

        if (y >= z)
        {
            return direction.Y > 0 ? AnatomicalDirection.Posterior : AnatomicalDirection.Anterior;
        }

        return direction.Z > 0 ? AnatomicalDirection.Superior : AnatomicalDirection.Inferior;
    }

    /// <summary>
    /// Определяет плоскость серии по нормали её срезов.
    ///
    /// Плоскость выводится из геометрии, а не из описания серии: описание —
    /// свободный текст производителя, и оно не обязано совпадать с тем,
    /// как серия получена на самом деле.
    /// </summary>
    /// <param name="sliceNormal">Нормаль плоскости среза.</param>
    /// <returns>Плоскость получения.</returns>
    public static ImagingPlane PlaneOf(SpatialVector sliceNormal) => Of(sliceNormal) switch
    {
        AnatomicalDirection.Superior or AnatomicalDirection.Inferior => ImagingPlane.Axial,
        AnatomicalDirection.Anterior or AnatomicalDirection.Posterior => ImagingPlane.Coronal,
        AnatomicalDirection.Left or AnatomicalDirection.Right => ImagingPlane.Sagittal,
        _ => ImagingPlane.Unknown,
    };

    /// <summary>
    /// Возвращает противоположное направление.
    /// </summary>
    /// <param name="direction">Направление.</param>
    /// <returns>Противоположное направление.</returns>
    public static AnatomicalDirection Opposite(AnatomicalDirection direction) => direction switch
    {
        AnatomicalDirection.Right => AnatomicalDirection.Left,
        AnatomicalDirection.Left => AnatomicalDirection.Right,
        AnatomicalDirection.Anterior => AnatomicalDirection.Posterior,
        AnatomicalDirection.Posterior => AnatomicalDirection.Anterior,
        AnatomicalDirection.Superior => AnatomicalDirection.Inferior,
        AnatomicalDirection.Inferior => AnatomicalDirection.Superior,
        _ => AnatomicalDirection.Unknown,
    };

    /// <summary>
    /// Строит подписи сторон изображения.
    /// </summary>
    /// <param name="rowDirection">
    /// Направляющий косинус строки: куда смещается точка при росте номера столбца,
    /// то есть направление к правому краю изображения.
    /// </param>
    /// <param name="columnDirection">
    /// Направляющий косинус столбца: куда смещается точка при росте номера строки,
    /// то есть направление к нижнему краю изображения.
    /// </param>
    /// <returns>Подписи четырёх сторон.</returns>
    public static EdgeLabels ForImagePlane(SpatialVector rowDirection, SpatialVector columnDirection)
    {
        var right = Of(rowDirection);
        var bottom = Of(columnDirection);

        return new EdgeLabels(
            Left: Opposite(right),
            Right: right,
            Top: Opposite(bottom),
            Bottom: bottom,
            IsOblique: IsOblique(rowDirection) || IsOblique(columnDirection));
    }

    private static bool IsOblique(SpatialVector direction)
    {
        var length = direction.Length;

        if (length == 0)
        {
            return true;
        }

        var dominant = Math.Max(
            Math.Abs(direction.X),
            Math.Max(Math.Abs(direction.Y), Math.Abs(direction.Z))) / length;

        return dominant < ObliqueThreshold;
    }
}
