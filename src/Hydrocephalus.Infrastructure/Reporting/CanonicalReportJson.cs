using System.Globalization;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Predictions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Канонический JSON-слой отчёта (ADR 0005): единственный источник истины,
/// из которого потом строится человекочитаемое представление.
///
/// Запись выполняется вручную, поле за полем, а не отражением по типу. Порядок
/// ключей тогда задан кодом и не меняется от версии среды выполнения, а
/// побайтовое сравнение в golden-тестах остаётся осмысленным: расхождение
/// означает изменение результата, а не перестановку полей.
///
/// В JSON хранятся коды и значения, а не переведённые строки: локализация
/// происходит на слое представления (ADR 0005).
/// </summary>
public static class CanonicalReportJson
{
    /// <summary>
    /// Версия схемы отчёта. Меняется вместе с составом полей — правило связки
    /// с версионированием конвейера из CONTRIBUTING.md.
    /// </summary>
    public const string SchemaVersion = "1.0.0";

    /// <summary>
    /// Формат отметок времени: UTC с фиксированным числом знаков.
    /// Локальное время сделало бы отчёт зависящим от машины, на которой он собран.
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly JsonWriterOptions Options = new()
    {
        // Без отступов: перевод строки при форматировании зависит от платформы,
        // а канонический слой обязан быть побайтово одинаковым везде.
        Indented = false,
        SkipValidation = false,
    };

    /// <summary>
    /// Сериализует отчёт в канонический JSON.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <returns>Байты JSON в UTF-8.</returns>
    public static byte[] Serialize(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            WriteReport(writer, report);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Сериализует отчёт в строку.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <returns>JSON.</returns>
    public static string SerializeToString(AnalysisReport report) =>
        Encoding.UTF8.GetString(Serialize(report));

    private static void WriteReport(Utf8JsonWriter writer, AnalysisReport report)
    {
        writer.WriteStartObject();

        writer.WriteString("schemaVersion", SchemaVersion);
        writer.WriteString("pseudonymousStudyId", report.PseudonymousStudyId);
        writer.WriteString("createdAt", Timestamp(report.CreatedAt));

        WritePipeline(writer, report.Pipeline);
        WriteQuality(writer, report.Quality);
        WriteOutcome(writer, report.Outcome);
        WriteBiomarkers(writer, report.Biomarkers);
        WriteSegmentation(writer, report);
        WriteAnnotations(writer, report.ClinicianAnnotations);

        writer.WriteEndObject();
    }

    private static void WritePipeline(Utf8JsonWriter writer, PipelineIdentity pipeline)
    {
        writer.WriteStartObject("pipeline");
        writer.WriteString("preprocessingVersion", pipeline.PreprocessingVersion);
        writer.WriteString("featureSchemaVersion", pipeline.FeatureSchemaVersion);
        writer.WriteString("labelMapVersion", pipeline.LabelMapVersion);
        writer.WriteString("applicationCommitSha", pipeline.ApplicationCommitSha);
        writer.WriteEndObject();
    }

    private static void WriteQuality(Utf8JsonWriter writer, QualityAssessment quality)
    {
        writer.WriteStartObject("quality");

        // Пригодность пишется как вычисленное значение, а не как отдельный флаг
        // из входных данных: в домене она выводится из состава проблем.
        writer.WriteBoolean("acceptable", quality.IsAcceptable);

        writer.WriteStartArray("issues");

        foreach (var issue in quality.Issues)
        {
            WriteIssue(writer, issue);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteIssue(Utf8JsonWriter writer, QualityIssue issue)
    {
        writer.WriteStartObject();
        writer.WriteString("code", issue.Code.ToString());
        writer.WriteString("severity", issue.Severity.ToString());

        writer.WriteStartObject("parameters");

        // Ключи упорядочены: порядок обхода словаря не гарантирован,
        // и без сортировки два одинаковых отчёта отличались бы побайтово.
        foreach (var key in issue.Parameters.Keys.OrderBy(item => item, StringComparer.Ordinal))
        {
            writer.WriteString(key, issue.Parameters[key]);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteOutcome(Utf8JsonWriter writer, AnalysisOutcome outcome)
    {
        writer.WriteStartObject("outcome");

        switch (outcome)
        {
            case AnalysisOutcome.Completed completed:
                writer.WriteString("kind", "completed");
                WritePrediction(writer, completed.Prediction);
                break;

            case AnalysisOutcome.Refused refused:
                // Отказ не несёт ни вероятностей, ни классов — ни в домене,
                // ни здесь: иначе его можно было бы прочитать как заключение.
                writer.WriteString("kind", "refused");
                WriteRefusal(writer, refused.Reason);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown analysis outcome '{outcome.GetType().Name}'.");
        }

        writer.WriteEndObject();
    }

    private static void WriteRefusal(Utf8JsonWriter writer, RefusalReason reason)
    {
        writer.WriteStartObject("refusal");
        writer.WriteString("code", reason.Code.ToString());

        writer.WriteStartArray("contributingIssues");

        foreach (var issue in reason.ContributingIssues)
        {
            WriteIssue(writer, issue);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePrediction(Utf8JsonWriter writer, Prediction prediction)
    {
        writer.WriteStartObject("prediction");

        writer.WriteStartObject("model");
        writer.WriteString("name", prediction.Model.Name);
        writer.WriteString("version", prediction.Model.Version);
        writer.WriteString("packageSha256", prediction.Model.PackageSha256);
        writer.WriteString("calibrationVersion", prediction.Model.CalibrationVersion);

        writer.WriteStartArray("supportedClasses");

        foreach (var supported in prediction.Model.SupportedClasses)
        {
            writer.WriteStringValue(supported.Code);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteStartArray("probabilities");

        foreach (var probability in prediction.Probabilities)
        {
            writer.WriteStartObject();
            writer.WriteString("class", probability.Class.Code);
            writer.WriteNumber("value", probability.Probability);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartObject("uncertainty");
        writer.WriteNumber("lowerBound", prediction.Uncertainty.LowerBound);
        writer.WriteNumber("upperBound", prediction.Uncertainty.UpperBound);
        writer.WriteNumber("confidenceLevel", prediction.Uncertainty.ConfidenceLevel);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteBiomarkers(Utf8JsonWriter writer, IReadOnlyList<Biomarker> biomarkers)
    {
        writer.WriteStartArray("biomarkers");

        foreach (var biomarker in biomarkers)
        {
            writer.WriteStartObject();
            writer.WriteString("code", biomarker.Method.Code);
            writer.WriteString("definitionVersion", biomarker.Method.DefinitionVersion);
            writer.WriteString("requiredTier", biomarker.Method.RequiredTier.ToString());
            writer.WriteString("reference", biomarker.Method.Reference);
            writer.WriteNumber("value", biomarker.Value);
            writer.WriteString("unit", biomarker.Unit.ToString());
            writer.WriteString("quality", biomarker.Quality.ToString());

            writer.WriteStartObject("allowedRange");
            writer.WriteNumber("minimum", biomarker.AllowedRange.Minimum);
            writer.WriteNumber("maximum", biomarker.AllowedRange.Maximum);
            writer.WriteEndObject();

            // Признак выхода за диапазон пишется рядом со значением: читателю
            // отчёта не должно требоваться повторять сравнение самому.
            writer.WriteBoolean("outOfRange", biomarker.IsOutOfRange);

            writer.WriteStartArray("sourceLabels");

            foreach (var label in biomarker.SourceLabels)
            {
                writer.WriteStringValue(label.Code);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteSegmentation(Utf8JsonWriter writer, AnalysisReport report)
    {
        if (report.Segmentation is not { } segmentation)
        {
            writer.WriteNull("segmentation");
            return;
        }

        writer.WriteStartObject("segmentation");
        writer.WriteString("labelMapVersion", segmentation.LabelMapVersion);
        writer.WriteString("maskReference", segmentation.MaskReference);
        writer.WriteBoolean("passedGeometricQualityControl", segmentation.PassedGeometricQualityControl);

        writer.WriteStartArray("labels");

        foreach (var label in segmentation.Labels)
        {
            writer.WriteStringValue(label.Code);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAnnotations(
        Utf8JsonWriter writer,
        IReadOnlyList<ClinicianAnnotation> annotations)
    {
        writer.WriteStartArray("clinicianAnnotations");

        foreach (var annotation in annotations)
        {
            writer.WriteStartObject();
            writer.WriteString("pseudonymousAuthorId", annotation.PseudonymousAuthorId);
            writer.WriteString("createdAt", Timestamp(annotation.CreatedAt));
            writer.WriteString("text", annotation.Text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
