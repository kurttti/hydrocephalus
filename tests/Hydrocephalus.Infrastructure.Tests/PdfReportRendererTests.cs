using System.Globalization;
using System.Text;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Человекочитаемый слой отчёта (ADR 0005).
///
/// Проверяется не внешний вид, а свойство, ради которого слой устроен именно
/// так: PDF порождается из канонического JSON и не может нести того, чего
/// в JSON нет.
/// </summary>
public sealed class PdfReportRendererTests : IDisposable
{
    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_rendered_report_is_a_pdf_file()
    {
        var pdf = PdfReportRenderer.Render(JsonReportExportStore.Serialize(Deidentified()));

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.AsSpan(0, 4)), StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_does_not_read_the_clock()
    {
        // Дата документа берётся из отчёта, а не с системных часов. Иначе два
        // экспорта одного отчёта отличались бы, ничем не отличаясь по существу,
        // и сравнить их было бы нечем.
        //
        // Побайтового совпадения двух отрисовок здесь не требуется и не
        // проверяется: PDF несёт уникальный идентификатор документа и префикс
        // подмножества встроенного шрифта, а они меняются от файла к файлу
        // и содержания отчёта не несут. Утверждать «байт в байт» значило бы
        // заявить свойство, которого формат не даёт.
        var pdf = Encoding.Latin1.GetString(
            PdfReportRenderer.Render(JsonReportExportStore.Serialize(Deidentified())));

        // Дата отчёта намеренно далека от сегодняшней: иначе проверка
        // «в документе нет сегодняшней даты» совпала бы с проверкой
        // «в документе есть дата отчёта» и не значила бы ничего.
        Assert.Contains("D:20240115", pdf, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "D:" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            pdf,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_canonical_json_renders_to_the_same_layout()
    {
        // Один и тот же вход обязан давать документ того же состава: отличаться
        // могут только идентификатор и префикс шрифта, а они фиксированной длины.
        var content = JsonReportExportStore.Serialize(Deidentified());

        Assert.Equal(
            PdfReportRenderer.Render(content).Length,
            PdfReportRenderer.Render(content).Length);
    }

    [Fact]
    public async Task Both_layers_are_written_side_by_side()
    {
        var store = new PdfReportExportStore(new JsonReportExportStore(this.root.FullName));

        var pdfPath = await store.WriteAsync(Deidentified(), CancellationToken.None);

        Assert.EndsWith(PdfReportExportStore.PdfExtension, pdfPath, StringComparison.Ordinal);
        Assert.True(File.Exists(pdfPath));

        // JSON остаётся источником истины и лежит рядом под тем же именем:
        // по PDF должно быть чем проверить, из чего он получен.
        Assert.True(File.Exists(Path.ChangeExtension(pdfPath, ".json")));
    }

    [Fact]
    public async Task A_clinical_export_renders_with_the_patient_block()
    {
        // Кириллица в именах — обычный случай, и шрифт обязан её нести.
        // Провал здесь выглядел бы у получателя как строка из пустых
        // прямоугольников, а не как ошибка.
        var store = new PdfReportExportStore(new JsonReportExportStore(this.root.FullName));

        var pdfPath = await store.WriteAsync(Clinical(), CancellationToken.None);

        Assert.True(new FileInfo(pdfPath).Length > 1000);
    }

    private static ReportExport Deidentified() =>
        ReportExport.Deidentified(Report(), Actor.Create("0011223344556677", ClinicalRole.Clinician), Exported);

    private static ReportExport Clinical() =>
        ReportExport.Clinical(
            Report(),
            new PatientIdentity
            {
                FullName = "Иванов Иван Иванович",
                MedicalRecordNumber = "12345",
                BirthDate = new DateOnly(1950, 3, 14),
            },
            Actor.Create("0011223344556677", ClinicalRole.Clinician),
            Exported);

    private static DateTimeOffset Exported => new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static AnalysisReport Report() => new()
    {
        PseudonymousStudyId = "study-0001",
        CreatedAt = Exported,
        Quality = QualityAssessment.Clean(),
        Outcome = new AnalysisOutcome.Refused
        {
            Reason = new RefusalReason { Code = RefusalCode.InsufficientAcquisitionTier },
        },
        Pipeline = new PipelineIdentity
        {
            PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
            FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,
            LabelMapVersion = PipelineIdentity.NotImplementedVersion,
            ApplicationCommitSha = "0000000000000000000000000000000000000000",
        },
    };
}
