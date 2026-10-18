using System.Globalization;
using System.Text;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Чтение разметки из хранилища отчётов.
///
/// Разметка нужна, чтобы врач увидел, куда ставил точки в прошлый раз. Поэтому
/// проверяется не только успешное чтение, но и каждый случай, в котором
/// разметка могла бы молча подмениться: отчёт без неё, отчёт другого метода,
/// повреждённый файл, отчёт прежней схемы.
/// </summary>
public sealed class ReportMarkingsTests : IDisposable
{
    /// <summary>
    /// UTF-8 без метки порядка байтов: канонический отчёт пишется именно так, а
    /// <c>JsonDocument</c> на метке спотыкается. Тест, писавший её, проверял бы
    /// то, чего в хранилище не бывает.
    /// </summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private const string Study = "study-0001";
    private const string Method = "evans-index-manual";

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "hydro-marks-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_marking_of_the_named_method_is_read_back()
    {
        Write("20261018T100000Z", "2026-10-18T10:00:00.0000000Z", Marking(Method, 16, [(38, 44), (61, 44)]));

        var marking = await ReportMarkings.ReadLatestAsync(this.root, Study, Method, CancellationToken.None);

        Assert.NotNull(marking);
        Assert.Equal(Method, marking.Value.Marking.MethodCode);
        Assert.Equal(VolumeAxis.AcrossSlices, marking.Value.Marking.MarkedAcross);
        Assert.Equal(16, marking.Value.Marking.PlaneIndex);
        Assert.Equal(2, marking.Value.Marking.Points.Count);
        Assert.Equal(38, marking.Value.Marking.Points[0].Column);
        Assert.Equal(44, marking.Value.Marking.Points[0].Row);
    }

    [Fact]
    public async Task The_newest_version_that_holds_a_marking_wins()
    {
        Write("20261018T100000Z", "2026-10-18T10:00:00.0000000Z", Marking(Method, 10, [(10, 10)]));
        Write("20261018T110000Z", "2026-10-18T11:00:00.0000000Z", Marking(Method, 20, [(20, 20)]));

        var marking = await ReportMarkings.ReadLatestAsync(this.root, Study, Method, CancellationToken.None);

        Assert.Equal(20, marking!.Value.Marking.PlaneIndex);
    }

    [Fact]
    public async Task A_later_version_without_a_marking_does_not_erase_the_earlier_one()
    {
        // Повторное открытие исследования сохраняет отчёт без ручной отметки, и
        // самая свежая версия разметки не содержит. Иначе она терялась бы от
        // простого просмотра.
        Write("20261018T100000Z", "2026-10-18T10:00:00.0000000Z", Marking(Method, 16, [(38, 44)]));
        Write("20261018T120000Z", "2026-10-18T12:00:00.0000000Z", "[]");

        var marking = await ReportMarkings.ReadLatestAsync(this.root, Study, Method, CancellationToken.None);

        Assert.Equal(16, marking!.Value.Marking.PlaneIndex);
    }

    [Fact]
    public async Task A_marking_of_another_method_is_not_returned()
    {
        Write("20261018T100000Z", "2026-10-18T10:00:00.0000000Z", Marking("callosal-angle-manual", 8, [(5, 5)]));

        var marking = await ReportMarkings.ReadLatestAsync(this.root, Study, Method, CancellationToken.None);

        Assert.Null(marking);
    }

    [Fact]
    public async Task A_report_of_an_older_schema_simply_has_no_marking()
    {
        // До схемы 1.3.0 блока markings не было вовсе. Это «не записано», а не
        // «измерено без точек», и отказом быть не должно.
        Directory.CreateDirectory(Path.Combine(this.root, Study));
        File.WriteAllText(
            this.VersionPath("20261017T100000Z"),
            """{"schemaVersion":"1.2.0","pseudonymousStudyId":"study-0001","createdAt":"2026-10-17T10:00:00.0000000Z"}""",
            Utf8);

        var marking = await ReportMarkings.ReadLatestAsync(this.root, Study, Method, CancellationToken.None);

        Assert.Null(marking);
    }

    [Fact]
    public async Task A_broken_version_does_not_hide_a_readable_one()
    {
        Write("20261018T100000Z", "2026-10-18T10:00:00.0000000Z", Marking(Method, 16, [(38, 44)]));
        File.WriteAllText(this.VersionPath("20261018T110000Z"), "{не json", Encoding.UTF8);

        var marking = await ReportMarkings.ReadLatestAsync(this.root, Study, Method, CancellationToken.None);

        Assert.Equal(16, marking!.Value.Marking.PlaneIndex);
    }

    [Fact]
    public async Task A_study_without_reports_has_no_marking()
    {
        var marking = await ReportMarkings.ReadLatestAsync(this.root, "unknown-study", Method, CancellationToken.None);

        Assert.Null(marking);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private static string Marking(string method, int plane, (double Column, double Row)[] points)
    {
        var placed = string.Join(
            ',',
            points.Select(point => string.Concat(
                "{\"column\":",
                Number(point.Column),
                ",\"row\":",
                Number(point.Row),
                ",\"slice\":",
                Number(plane),
                "}")));

        return string.Concat(
            "[{\"method\":\"",
            method,
            "\",\"markedAcross\":\"AcrossSlices\",\"planeIndex\":",
            Number(plane),
            ",\"points\":[",
            placed,
            "]}]");
    }

    private static string Number(double value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private string VersionPath(string name) =>
        Path.Combine(this.root, Study, name + "-0000000000000000.json");

    private void Write(string name, string createdAt, string markings)
    {
        Directory.CreateDirectory(Path.Combine(this.root, Study));

        File.WriteAllText(
            this.VersionPath(name),
            string.Concat(
                "{\"schemaVersion\":\"1.3.0\",\"pseudonymousStudyId\":\"",
                Study,
                "\",\"createdAt\":\"",
                createdAt,
                "\",\"markings\":",
                markings,
                "}"),
            Utf8);
    }
}
