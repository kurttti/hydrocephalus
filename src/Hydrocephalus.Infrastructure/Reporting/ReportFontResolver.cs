using PdfSharp.Fonts;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Поиск и встраивание шрифта для отчёта.
///
/// Шрифт встраивается в файл, а не берётся из системы при открытии: отчёт
/// уходит за пределы машины, где создан, и документ, который на другом
/// компьютере подставит другой шрифт, — это документ, который может
/// переразметиться. Для долгого хранения (ADR 0005) встраивание обязательно.
///
/// Список кандидатов, а не один путь: набор шрифтов Windows отличается между
/// редакциями, и отсутствие ровно одного файла не должно ронять экспорт.
/// Если не найден ни один — это отказ с внятной причиной, а не молчаливая
/// замена на шрифт без кириллицы.
/// </summary>
internal sealed class ReportFontResolver : IFontResolver
{
    /// <summary>Имя семейства, которым пользуется рендерер.</summary>
    internal const string FamilyName = "Hydrocephalus Report";

    private static readonly string[] RegularCandidates =
    [
        "arial.ttf",
        "segoeui.ttf",
        "tahoma.ttf",
        "calibri.ttf",
        "DejaVuSans.ttf",
    ];

    private static readonly string[] BoldCandidates =
    [
        "arialbd.ttf",
        "segoeuib.ttf",
        "tahomabd.ttf",
        "calibrib.ttf",
        "DejaVuSans-Bold.ttf",
    ];

    private static readonly string[] Directories =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        "/usr/share/fonts/truetype/dejavu",
        "/usr/share/fonts/truetype/msttcorefonts",
        "/Library/Fonts",
    ];

    /// <summary>Единственный экземпляр: PDFsharp хранит резолвер глобально.</summary>
    internal static ReportFontResolver Instance { get; } = new();

    /// <summary>
    /// Сопоставляет запрошенное начертание с найденным файлом шрифта.
    /// </summary>
    /// <param name="familyName">Запрошенное семейство.</param>
    /// <param name="isBold">Полужирное начертание.</param>
    /// <param name="isItalic">Курсив.</param>
    /// <returns>Описание найденного начертания.</returns>
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "report-bold" : "report-regular");

    /// <summary>
    /// Читает файл шрифта.
    /// </summary>
    /// <param name="faceName">Имя начертания.</param>
    /// <returns>Содержимое файла шрифта.</returns>
    /// <exception cref="InvalidOperationException">Если пригодный шрифт не найден.</exception>
    public byte[] GetFont(string faceName)
    {
        var candidates = string.Equals(faceName, "report-bold", StringComparison.Ordinal)
            ? BoldCandidates
            : RegularCandidates;

        var path = Locate(candidates) ?? Locate(RegularCandidates);

        if (path is null)
        {
            // Отказ, а не подстановка: шрифт без кириллицы превратил бы русский
            // отчёт в набор пустых прямоугольников, и это заметили бы уже
            // на стороне получателя.
            throw new InvalidOperationException(
                "No font with Cyrillic coverage was found for report rendering.");
        }

        return File.ReadAllBytes(path);
    }

    private static string? Locate(string[] candidates)
    {
        foreach (var directory in Directories)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);

                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }
}
