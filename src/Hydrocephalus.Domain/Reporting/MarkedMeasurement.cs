using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Domain.Reporting;

/// <summary>
/// Точка разметки в непрерывных координатах сетки объёма.
///
/// Координаты, а не миллиметры системы пациента: разметка восстанавливается на
/// том же изображении, где поставлена, и перевод туда-обратно добавил бы к ней
/// погрешность, которой при постановке не было. Перевод в миллиметры делает
/// измеритель, и он один.
/// </summary>
/// <param name="Column">Отсчёт по столбцам.</param>
/// <param name="Row">Отсчёт по строкам.</param>
/// <param name="Slice">Отсчёт по срезам.</param>
public readonly record struct MarkedPoint(double Column, double Row, double Slice);

/// <summary>
/// Точки, которыми врач отметил измерение.
///
/// Зачем в отчёте. До этого отчёт нёс только значение, и проверить постановку
/// точек было нечем: индекс 0,33 одинаково выходит из верно отмеченных рогов,
/// из точек, съехавших на борозду, и из перепутанного порядка нажатий. Разметка
/// же — единственное, по чему расхождение ручного и автоматического измерения
/// разбирается на причину: не сошлись точки или не сошёлся метод.
///
/// Почему в отчёте, а не рядом с ним. Отчёт неизменен и версионируется, и
/// значение в нём получено **из этих** точек. Отдельное хранилище разметки
/// разошлось бы с ним при первом же повторном измерении: значение одной версии,
/// точки другой, и узнать это было бы невозможно.
///
/// Персональных данных здесь нет: это отсчёты сетки, а не анатомия и не
/// идентификаторы.
/// </summary>
public sealed record MarkedMeasurement
{
    /// <summary>
    /// Создаёт разметку.
    /// </summary>
    /// <param name="MethodCode">Код метода, которому принадлежат точки.</param>
    /// <param name="MarkedAcross">Ось объёма, поперёк которой лежит плоскость разметки.</param>
    /// <param name="PlaneIndex">Номер плоскости вдоль этой оси.</param>
    /// <param name="Points">Точки в порядке постановки.</param>
    public MarkedMeasurement(
        string MethodCode,
        VolumeAxis MarkedAcross,
        int PlaneIndex,
        IReadOnlyList<MarkedPoint> Points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(MethodCode);
        ArgumentNullException.ThrowIfNull(Points);
        ArgumentOutOfRangeException.ThrowIfNegative(PlaneIndex);

        if (Points.Count == 0)
        {
            // Разметка без точек не описывает ничего и при чтении выглядела бы
            // как «измерено без разметки» — а это другое утверждение.
            throw new DomainRuleViolationException(
                $"The marking of '{MethodCode}' holds no points.");
        }

        this.MethodCode = MethodCode;
        this.MarkedAcross = MarkedAcross;
        this.PlaneIndex = PlaneIndex;
        this.Points = [.. Points];
    }

    /// <summary>Код метода, которому принадлежат точки.</summary>
    public string MethodCode { get; }

    /// <summary>Ось объёма, поперёк которой лежит плоскость разметки.</summary>
    public VolumeAxis MarkedAcross { get; }

    /// <summary>Номер плоскости вдоль этой оси.</summary>
    public int PlaneIndex { get; }

    /// <summary>Точки в порядке постановки. Порядок значим: он задаёт ось измерения.</summary>
    public IReadOnlyList<MarkedPoint> Points { get; }
}
