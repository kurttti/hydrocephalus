using System.Globalization;
using System.Text.Json;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Человекочитаемый слой отчёта: PDF, порождённый из канонического JSON.
///
/// Рендерер принимает **байты уже сериализованного JSON**, а не объект отчёта.
/// Это не деталь удобства: ADR 0005 требует, чтобы PDF не мог содержать того,
/// чего нет в JSON, и чтобы никакие значения не вычислялись при отрисовке.
/// Если бы рендерер получал доменный объект, он мог бы посчитать что-нибудь
/// сам — например, «норма/не норма» по порогу, — и в отчёте появилось бы
/// утверждение, которого нет в источнике истины. Приняв на вход разобранный
/// JSON, он такой возможности не имеет: печатать нечего, кроме того, что
/// в нём лежит.
///
/// Соответствие PDF/A **не заявляется и не проверялось**. Шрифты встраиваются,
/// метаданные проставляются, но формальная проверка на профиль PDF/A-2b
/// не выполняется. Называть результат PDF/A без такой проверки означало бы
/// заявить свойство долговременного хранения, которого никто не подтверждал.
/// </summary>
public static class PdfReportRenderer
{
    private const double PageMargin = 56;
    private const double LineHeight = 15;
    private const double SectionGap = 10;

    private static bool fontsConfigured;

    /// <summary>
    /// Строит PDF по каноническому JSON экспорта.
    /// </summary>
    /// <param name="canonicalJson">Байты JSON, записываемого как канонический слой.</param>
    /// <returns>Содержимое PDF-файла.</returns>
    /// <exception cref="JsonException">Если содержимое не является JSON.</exception>
    public static byte[] Render(byte[] canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);

        ConfigureFonts();

        using var json = JsonDocument.Parse(canonicalJson);

        using var document = new PdfDocument();

        var exportedAt = ReadTimestamp(json.RootElement);

        document.Info.Title = "Отчёт анализа";
        document.Info.Author = "Hydrocephalus";
        document.Info.Subject = Text(json.RootElement, "variant");

        // Даты берутся из отчёта, а не из системных часов: один и тот же JSON
        // обязан давать один и тот же PDF, иначе «порождён из JSON» перестаёт
        // быть проверяемым утверждением.
        document.Info.CreationDate = exportedAt;
        document.Info.ModificationDate = exportedAt;

        var writer = new PageWriter(document);

        writer.Heading("Отчёт анализа");

        // Раскладка общая с экраном предпросмотра: врач решает, отправлять ли
        // файл, глядя на его содержимое, и вторая раскладка разошлась бы
        // с первой незаметно.
        foreach (var line in ReportOutline.Build(canonicalJson))
        {
            writer.Write(line);
        }

        using var stream = new MemoryStream();

        document.Save(stream);

        return stream.ToArray();
    }

    private static void ConfigureFonts()
    {
        if (fontsConfigured)
        {
            return;
        }

        // Резолвер глобален в PDFsharp; повторная установка выбрасывает.
        GlobalFontSettings.FontResolver ??= ReportFontResolver.Instance;
        fontsConfigured = true;
    }

    private static DateTime ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("exportedAt", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            ? parsed.UtcDateTime
            : DateTime.UnixEpoch;

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Постраничная укладка текста с переносом на новую страницу.</summary>
    private sealed class PageWriter
    {
        private readonly PdfDocument document;
        private readonly XFont regular = new(ReportFontResolver.FamilyName, 9.5);
        private readonly XFont bold = new(ReportFontResolver.FamilyName, 9.5, XFontStyleEx.Bold);
        private readonly XFont title = new(ReportFontResolver.FamilyName, 15, XFontStyleEx.Bold);

        private XGraphics graphics;
        private double y;
        private double width;

        internal PageWriter(PdfDocument document)
        {
            this.document = document;
            this.graphics = this.NewPage();
        }

        internal void Heading(string text)
        {
            this.graphics.DrawString(text, this.title, XBrushes.Black, new XPoint(PageMargin, this.y));
            this.y += LineHeight * 2;
        }

        internal void Write(ReportLine line)
        {
            this.Line(line.Text, line.IsHeading ? this.bold : this.regular, line.Depth);

            if (line.GapAfter)
            {
                this.y += SectionGap;
            }
        }

        private XGraphics NewPage()
        {
            var page = this.document.AddPage();

            page.Size = PdfSharp.PageSize.A4;

            this.y = PageMargin;
            this.width = page.Width.Point - (PageMargin * 2);

            return XGraphics.FromPdfPage(page);
        }

        private void Line(string text, XFont font, int depth)
        {
            var indent = PageMargin + (depth * 14);
            var available = this.width - (depth * 14);

            foreach (var piece in Wrap(text, font, available))
            {
                if (this.y > 800)
                {
                    this.graphics.Dispose();
                    this.graphics = this.NewPage();
                }

                this.graphics.DrawString(piece, font, XBrushes.Black, new XPoint(indent, this.y));
                this.y += LineHeight;
            }
        }

        private IEnumerable<string> Wrap(string text, XFont font, double available)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 0)
            {
                yield return string.Empty;
                yield break;
            }

            var line = words[0];

            foreach (var word in words.Skip(1))
            {
                var candidate = line + " " + word;

                if (this.graphics.MeasureString(candidate, font).Width > available)
                {
                    yield return line;
                    line = word;
                    continue;
                }

                line = candidate;
            }

            yield return line;
        }
    }
}
