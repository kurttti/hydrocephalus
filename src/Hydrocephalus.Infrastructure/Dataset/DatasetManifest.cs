using System.Text.Json;

namespace Hydrocephalus.Infrastructure.Dataset;

/// <summary>
/// Манифест замороженного обезличенного набора: что в нём и в каком виде.
///
/// Записи объявлены здесь, а не в утилите сборки, потому что читателей у них
/// двое: утилита сведения и сам просмотрщик, ведущий очередь измерений. Две
/// копии определения разошлись бы при первой же правке, и разошлись бы молча —
/// поле, которое одна сторона пишет, а другая не читает, ничем себя не выдаёт.
/// </summary>
public sealed record DatasetManifest
{
    /// <summary>Имя файла манифеста в корне набора.</summary>
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Момент сборки.</summary>
    public required DateTimeOffset BuiltAt { get; init; }

    /// <summary>Коммит сборки, которой собран набор.</summary>
    public required string BuiltBy { get; init; }

    /// <summary>Исследования набора.</summary>
    public required IReadOnlyList<DatasetStudy> Studies { get; init; }

    /// <summary>
    /// Читает манифест из корня набора.
    /// </summary>
    /// <param name="root">Корень набора.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Манифест либо <see langword="null"/>, если его нет.</returns>
    public static async Task<DatasetManifest?> ReadAsync(
        string root,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var path = Path.Combine(root, FileName);

        if (!File.Exists(path))
        {
            return null;
        }

        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<DatasetManifest>(content, Options);
    }

    /// <summary>
    /// Записывает манифест в корень набора.
    /// </summary>
    /// <param name="root">Корень набора.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача записи.</returns>
    public async Task WriteAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Directory.CreateDirectory(root);

        // С отступами: манифест читают глазами и сравнивают между сборками.
        await File.WriteAllTextAsync(
            Path.Combine(root, FileName),
            JsonSerializer.Serialize(this, Options),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Одно исследование набора.</summary>
public sealed record DatasetStudy
{
    /// <summary>Группа сравнения, из папки которой взято исследование.</summary>
    public required string Group { get; init; }

    /// <summary>
    /// Псевдоним пациента, выведенный из исходных полей.
    ///
    /// Записан потому, что по копии он не вычисляется: обезличивание переписывает
    /// UID, и разбор набора дал бы псевдоним от псевдонима.
    /// </summary>
    public required string PseudonymousSubjectId { get; init; }

    /// <summary>Псевдоним исследования, выведенный из исходного UID.</summary>
    public required string PseudonymousStudyId { get; init; }

    /// <summary>
    /// Псевдоним пациента, который выходит при разборе самой копии.
    ///
    /// Отчёт об измерении, сделанном по набору, лежит под ним, а не под исходным.
    /// <see langword="null"/>, если копия разобралась не в одно исследование.
    /// </summary>
    public required string? DerivedSubjectId { get; init; }

    /// <summary>Псевдоним исследования, который выходит при разборе самой копии.</summary>
    public required string? DerivedStudyId { get; init; }

    /// <summary>Серии, вошедшие в набор.</summary>
    public required IReadOnlyList<DatasetSeries> Series { get; init; }

    /// <summary>Сколько серий отброшено блокирующим замечанием и в набор не вошло.</summary>
    public required int ExcludedSeries { get; init; }

    /// <summary>Файлы исследования с хешами.</summary>
    public required IReadOnlyList<DatasetFile> Files { get; init; }

    /// <summary>
    /// Каталог исследования внутри набора.
    /// </summary>
    /// <param name="root">Корень набора.</param>
    /// <returns>Полный путь каталога.</returns>
    public string DirectoryIn(string root) =>
        Path.Combine(root, this.Group, this.PseudonymousSubjectId, this.PseudonymousStudyId);

    /// <summary>
    /// Псевдонимы, под которыми может лежать отчёт об этом исследовании.
    ///
    /// Их два: измерение с оригинала ложится под исходным, измерение из набора —
    /// под производным. Который из них встретится, зависит от того, откуда врач
    /// открыл исследование, и заранее это неизвестно.
    /// </summary>
    /// <returns>Один или два псевдонима.</returns>
    public IEnumerable<string> ReportKeys()
    {
        yield return this.PseudonymousStudyId;

        if (!string.IsNullOrEmpty(this.DerivedStudyId)
            && !string.Equals(this.DerivedStudyId, this.PseudonymousStudyId, StringComparison.Ordinal))
        {
            yield return this.DerivedStudyId;
        }
    }
}

/// <summary>Серия набора: чем она полезна, без содержимого.</summary>
public sealed record DatasetSeries
{
    /// <summary>Псевдоним серии.</summary>
    public required string PseudonymousSeriesId { get; init; }

    /// <summary>Распознанная взвешенность.</summary>
    public required string Weighting { get; init; }

    /// <summary>Уровень входа.</summary>
    public required string Tier { get; init; }

    /// <summary>Число срезов.</summary>
    public required int Slices { get; init; }

    /// <summary>Шаг между срезами, мм.</summary>
    public required double SliceSpacingMillimetres { get; init; }
}

/// <summary>Файл набора и его хеш.</summary>
public sealed record DatasetFile
{
    /// <summary>Путь относительно каталога исследования.</summary>
    public required string Path { get; init; }

    /// <summary>SHA-256 содержимого.</summary>
    public required string Sha256 { get; init; }
}
