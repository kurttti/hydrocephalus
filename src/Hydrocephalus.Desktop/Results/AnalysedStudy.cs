using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Desktop.Results;

/// <summary>
/// Исследование глазами экрана результата: что было в выгрузке, что взято
/// в работу и что не взято.
///
/// Отдельный тип, а не три параметра: состав исследования, выбранная серия
/// и отброшенные серии описывают одно и то же исследование, и передавать их
/// порознь означало бы допустить набор, собранный из разных.
/// </summary>
public sealed record AnalysedStudy
{
    /// <summary>Серии, попавшие в рабочую копию.</summary>
    public required ImagingStudy Study { get; init; }

    /// <summary>Серия, по которой выполнены просмотр и анализ.</summary>
    public required ImagingSeries Analysed { get; init; }

    /// <summary>Серии, не попавшие в рабочую копию, с причинами.</summary>
    public IReadOnlyList<ExcludedSeries> Excluded { get; init; } = [];
}
