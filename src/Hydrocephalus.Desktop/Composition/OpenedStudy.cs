using Hydrocephalus.Desktop.Results;
using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Открытое исследование: то, что показано на экране, и отчёт по нему.
///
/// Изображение и отчёт выдаются вместе, потому что получены из одной рабочей
/// копии. Разделить их значило бы допустить, что на экране одна серия,
/// а в экспортируемом отчёте — другая, и заметить это было бы нечем.
/// </summary>
public sealed record OpenedStudy
{
    /// <summary>Состояние экрана просмотра.</summary>
    public required StudyView View { get; init; }

    /// <summary>Отчёт по тому же исследованию.</summary>
    public required AnalysisReport Report { get; init; }

    /// <summary>
    /// Состав исследования: что было в выгрузке, что взято в работу и что нет.
    ///
    /// Нужен экрану результата: отчёт не содержит ни свойств серии — без них
    /// отсутствие измерений не объяснить ничем, кроме молчания, — ни следа
    /// серий, которые до отчёта вообще не дошли.
    /// </summary>
    public required AnalysedStudy Analysed { get; init; }
}
