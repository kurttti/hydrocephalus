using System.Globalization;
using FellowOakDicom;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Возраст и пол пациента — те самые два поля, которые DICOM PS3.15 выделяет
/// в опцию Retain Patient Characteristics.
///
/// Профиль обезличивания их по-прежнему удаляет: в файлах набора ни возраста,
/// ни пола нет. Читаются они из источника до обезличивания и живут в манифесте
/// набора, потому что без них сравнение групп нечем поправить — размер
/// желудочков растёт с возрастом сам, и возрастной сдвиг между группами
/// невозможно отличить от болезни (решение владельца данных 2026-10-17,
/// docs/clinical/README.md).
/// </summary>
/// <param name="AgeYears">
/// Полных лет на момент исследования либо <see langword="null"/>, если ни тега
/// возраста, ни даты рождения в источнике нет. Значение <see cref="AgeCap"/>
/// означает «столько или больше».
/// </param>
/// <param name="Sex">«M», «F», «O» либо <see langword="null"/>.</param>
internal readonly record struct PatientCharacteristics(int? AgeYears, string? Sex)
{
    /// <summary>
    /// Предел, выше которого возраст не записывается точно: в пожилом конце
    /// распределения точный год сам становится признаком, по которому человека
    /// можно найти. Граница взята та же, что в HIPAA Safe Harbour.
    /// </summary>
    internal const int AgeCap = 90;

    /// <summary>Наибольший возраст, который считается правдоподобным.</summary>
    private const int ImplausibleAge = 120;

    /// <summary>Пустые характеристики.</summary>
    internal static PatientCharacteristics None => new(null, null);

    /// <summary>Есть ли хоть одно значение.</summary>
    internal bool IsEmpty => this.AgeYears is null && this.Sex is null;

    /// <summary>
    /// Читает характеристики из исходного набора тегов.
    ///
    /// Возраст берётся из <c>PatientAge</c> (0010,1010), а если его нет — из даты
    /// рождения и даты исследования. Второй путь работает только до
    /// обезличивания: оно удаляет дату рождения и сдвигает дату исследования,
    /// поэтому по копии возраст не восстанавливается никак.
    /// </summary>
    /// <param name="dataset">Исходный набор тегов.</param>
    /// <returns>Характеристики; отсутствующие поля — <see langword="null"/>.</returns>
    internal static PatientCharacteristics Read(DicomDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var age = ParseAge(dataset.GetSingleValueOrDefault(DicomTag.PatientAge, string.Empty))
            ?? AgeFromDates(
                dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
                dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty));

        return new PatientCharacteristics(
            age is null ? null : Math.Min(age.Value, AgeCap),
            NormaliseSex(dataset.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty)));
    }

    /// <summary>
    /// Разбирает значение DICOM AS: три цифры и единица — годы, месяцы, недели
    /// или дни. Единицу пишут не все, а ведущие нули пишут не всегда, поэтому
    /// разбор снисходителен к виду и строг к смыслу: месяцы, недели и дни дают
    /// возраст только в полных годах, то есть у взрослых — ноль.
    /// </summary>
    private static int? ParseAge(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        var unit = char.ToUpperInvariant(trimmed[^1]);
        var digits = char.IsAsciiDigit(unit) ? trimmed : trimmed[..^1];

        if (!int.TryParse(digits, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            return null;
        }

        var years = unit switch
        {
            'Y' => number,
            'M' => number / 12,
            'W' => number / 52,
            'D' => number / 365,
            _ => char.IsAsciiDigit(unit) ? number : -1,
        };

        return years is >= 0 and <= ImplausibleAge ? years : null;
    }

    /// <summary>Считает полные годы между датой рождения и датой исследования.</summary>
    private static int? AgeFromDates(string birthDate, string studyDate)
    {
        if (!TryParseDate(birthDate, out var born) || !TryParseDate(studyDate, out var taken))
        {
            return null;
        }

        var years = taken.Year - born.Year;

        if (taken < born.AddYears(years))
        {
            years--;
        }

        return years is >= 0 and <= ImplausibleAge ? years : null;
    }

    private static bool TryParseDate(string value, out DateTime parsed) =>
        DateTime.TryParseExact(
            value.Trim(),
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out parsed);

    /// <summary>
    /// Приводит пол к «M», «F» либо «O». Всё остальное — пустое значение,
    /// а не догадка: пол, выведенный из имени, был бы выдумкой.
    /// </summary>
    private static string? NormaliseSex(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "M" => "M",
            "F" => "F",
            "O" => "O",
            _ => null,
        };
}
