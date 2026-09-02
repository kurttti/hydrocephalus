using FellowOakDicom;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Проверки достоверности технических тегов на приёмке.
/// Значение, заведомо невозможное физически, нормализуется или помечается недостоверным,
/// а не используется как есть (docs/data/README.md).
/// </summary>
internal static class DicomMetadataChecks
{
    /// <summary>Наибольшая напряжённость поля клинического МР-томографа, тесла.</summary>
    private const double MaxPlausibleFieldStrengthTesla = 12.0;

    /// <summary>Наименьшая напряжённость поля, при которой значение считается осмысленным.</summary>
    private const double MinPlausibleFieldStrengthTesla = 0.01;

    /// <summary>Проверяет набор тегов и возвращает найденные замечания.</summary>
    /// <param name="dataset">Набор тегов серии.</param>
    /// <returns>Замечания к серии, возможно пустой список.</returns>
    internal static IEnumerable<QualityIssue> Inspect(DicomDataset dataset)
    {
        if (dataset.TryGetSingleValue<decimal>(DicomTag.MagneticFieldStrength, out var raw))
        {
            var value = (double)raw;

            // На реальной выборке встречались значения, завышенные на порядки
            // из-за путаницы единиц у отдельных производителей (тесла против гаусса).
            if (value is > MaxPlausibleFieldStrengthTesla or (> 0 and < MinPlausibleFieldStrengthTesla))
            {
                yield return new QualityIssue
                {
                    Code = QualityIssueCode.ImplausibleMetadata,
                    Severity = QualityIssueSeverity.Warning,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["tag"] = "MagneticFieldStrength",
                        ["value"] = value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                    },
                };
            }
        }

        if (dataset.GetSingleValueOrDefault(DicomTag.BurnedInAnnotation, string.Empty)
            .Equals("YES", StringComparison.OrdinalIgnoreCase))
        {
            yield return new QualityIssue
            {
                Code = QualityIssueCode.BurnedInAnnotation,
                Severity = QualityIssueSeverity.Blocking,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }
    }
}
