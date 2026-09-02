namespace Hydrocephalus.Domain.Quality;

/// <summary>
/// Результат входного контроля качества серии или исследования.
/// Пригодность выводится из состава найденных проблем, а не выставляется отдельным флагом:
/// иначе можно было бы объявить QC пройденным при блокирующей проблеме.
/// </summary>
public sealed record QualityAssessment
{
    /// <summary>Выявленные проблемы. Пустой список означает чистый QC.</summary>
    public required IReadOnlyList<QualityIssue> Issues { get; init; }

    /// <summary>
    /// Признак того, что анализ допустим. Любая блокирующая проблема делает QC непройденным.
    /// </summary>
    public bool IsAcceptable => !Issues.Any(issue => issue.Severity == QualityIssueSeverity.Blocking);

    /// <summary>Проблемы, из-за которых анализ невозможен.</summary>
    public IReadOnlyList<QualityIssue> BlockingIssues =>
        Issues.Where(issue => issue.Severity == QualityIssueSeverity.Blocking).ToList();

    /// <summary>Создаёт результат QC без замечаний.</summary>
    /// <returns>Пройденный контроль качества.</returns>
    public static QualityAssessment Clean() => new() { Issues = [] };
}
