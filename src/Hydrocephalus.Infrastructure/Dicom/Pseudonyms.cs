using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Псевдонимизация идентификаторов: согласованная замена значений на устойчивые
/// непрозрачные строки. Одинаковый вход при одной соли даёт одинаковый выход,
/// поэтому ссылочная целостность внутри пациента сохраняется (ADR 0003).
/// </summary>
internal static class Pseudonyms
{
    /// <summary>Вычисляет псевдоним для значения в указанной области имён.</summary>
    /// <param name="salt">Соль псевдонимизации.</param>
    /// <param name="scope">Область имён, например "series" или "subject".</param>
    /// <param name="value">Исходное значение.</param>
    /// <returns>Устойчивая непрозрачная строка.</returns>
    internal static string Derive(string salt, string scope, string value)
    {
        var payload = Encoding.UTF8.GetBytes(Encode(salt, scope, value));
        var digest = SHA256.HashData(payload);

        return Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Вычисляет псевдонимный идентификатор пациента по правилу дедупликации
    /// из docs/data/README.md.
    ///
    /// Обнаруженный на реальных данных случай: примерно у половины файлов
    /// PatientID, PatientName и PatientBirthDate пусты одновременно. Наивное
    /// хеширование пустой строки склеило бы разных пациентов в одного, что
    /// разрушает patient-level split. Поэтому при полностью пустых полях
    /// идентификатором становится исследование: ложное разделение безопаснее
    /// ложного объединения.
    /// </summary>
    /// <param name="salt">Соль псевдонимизации.</param>
    /// <param name="patientId">Значение PatientID, возможно пустое.</param>
    /// <param name="patientName">Значение PatientName, возможно пустое.</param>
    /// <param name="birthDate">Значение PatientBirthDate, возможно пустое.</param>
    /// <param name="studyInstanceUid">
    /// StudyInstanceUID — источник уникальности, когда опознать пациента нечем.
    /// </param>
    /// <returns>Псевдонимный идентификатор пациента.</returns>
    internal static string DeriveSubjectId(
        string salt,
        string? patientId,
        string? patientName,
        string? birthDate,
        string studyInstanceUid)
    {
        var identifiers = new[] { patientId, patientName, birthDate }
            .Select(value => value?.Trim() ?? string.Empty)
            .ToArray();

        if (identifiers.All(string.IsNullOrEmpty))
        {
            // Ни одного непустого поля: запись считается уникальным пациентом
            // и никогда не сливается с другой.
            return Derive(salt, "subject-unmatched", studyInstanceUid);
        }

        var normalized = Encode(
            identifiers[0].ToUpperInvariant(),
            identifiers[1].ToUpperInvariant(),
            identifiers[2].ToUpperInvariant());

        return Derive(salt, "subject", normalized);
    }

    /// <summary>Приводит дату рождения к устойчивому виду для сравнения.</summary>
    /// <param name="value">Дата или <see langword="null"/>.</param>
    /// <returns>Дата в формате yyyyMMdd либо пустая строка.</returns>
    internal static string NormalizeDate(DateTime? value) =>
        value?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Однозначно кодирует набор полей в одну строку. Каждое поле предваряется своей
    /// длиной, поэтому разные наборы значений не могут дать одинаковую строку —
    /// без этого разделитель, случайно встретившийся внутри значения, склеил бы
    /// разных пациентов.
    /// </summary>
    private static string Encode(string first, string second, string third)
    {
        var builder = new StringBuilder();

        foreach (var field in new[] { first, second, third })
        {
            builder.Append(field.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(field);
            builder.Append('|');
        }

        return builder.ToString();
    }
}
