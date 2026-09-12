using System.Text;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Раскладка канонического JSON в строки для человека.
///
/// Читателей у неё двое — файл, уходящий вовне, и экран предпросмотра, на
/// котором врач решает, отправлять ли его. Поэтому проверяется не вёрстка,
/// а то, что раскладка ничего не прибавляет к JSON и ничего из него не теряет:
/// расхождение между показанным и записанным отменяет смысл предпросмотра.
/// </summary>
public sealed class ReportOutlineTests
{
    [Fact]
    public void Field_names_are_shown_in_words()
    {
        // Врач читает отчёт, а не схему: «pseudonymousStudyId» ему не адресовано.
        var lines = Build("""{"pseudonymousStudyId":"study-1"}""");

        Assert.Contains("Псевдоним исследования", Assert.Single(lines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_without_a_caption_keeps_its_name()
    {
        // Поле, добавленное в отчёт и забытое здесь, обязано остаться видимым:
        // пропустить его значило бы показать отчёт, в котором его нет.
        var lines = Build("""{"somethingNew":"value"}""");

        Assert.Contains("somethingNew", Assert.Single(lines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_list_says_that_it_is_empty()
    {
        // «Замечаний нет» и «о замечаниях ничего не сказано» читаются
        // по-разному, а выглядели бы одинаково.
        var lines = Build("""{"issues":[]}""");

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].IsHeading);
        Assert.Contains("нет", lines[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_is_kept_as_depth()
    {
        var lines = Build("""{"quality":{"issues":[{"code":"HeadTruncated"}]}}""");

        Assert.Equal(0, lines[0].Depth);
        Assert.True(lines[0].IsHeading);
        Assert.Contains(lines, line => line.Depth > 0 && line.Text.Contains("HeadTruncated", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_value_is_a_dash_rather_than_a_blank()
    {
        // Пустое место читается как недосмотр вёрстки, а не как отсутствие
        // значения, и врач пойдёт искать, куда оно делось.
        var lines = Build("""{"modelVersion":null}""");

        Assert.Contains("—", Assert.Single(lines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_are_not_reformatted()
    {
        // Раскладка ничего не вычисляет: значение уходит в текст тем же,
        // каким записано в каноническом слое.
        var lines = Build("""{"value":42.3}""");

        Assert.Contains("42.3", Assert.Single(lines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_shown_for_an_empty_report()
    {
        Assert.Empty(Build("{}"));
    }

    private static IReadOnlyList<ReportLine> Build(string json) =>
        ReportOutline.Build(Encoding.UTF8.GetBytes(json));
}
