using System.Globalization;
using System.Security.Cryptography;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Хранилище канонических отчётов в файлах (ADR 0005).
///
/// Сохранённый отчёт неизменен: смена версии модели не пересчитывает прошлые
/// отчёты. Неизменность обеспечивается способом записи, а не соглашением —
/// перезапись существующего файла другим содержимым не проходит молча.
/// </summary>
public sealed class JsonReportStore : IReportStore
{
    /// <summary>Длина различителя, добавляемого к имени файла, в шестнадцатеричных знаках.</summary>
    private const int DiscriminatorLength = 16;

    private readonly string rootDirectory;

    /// <summary>Создаёт хранилище.</summary>
    /// <param name="rootDirectory">Каталог, в котором складываются отчёты.</param>
    public JsonReportStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Сохраняет отчёт.
    ///
    /// Повторное сохранение того же отчёта ничего не меняет и ошибкой не является:
    /// содержимое уже на диске совпадает побайтово. Ошибкой является попытка
    /// положить по тому же пути другое содержимое.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача сохранения.</returns>
    /// <exception cref="IOException">Если по этому пути уже лежит другой отчёт.</exception>
    public async Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var content = CanonicalReportJson.Serialize(report);
        var path = this.PathFor(report, content);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

            if (existing.AsSpan().SequenceEqual(content))
            {
                return;
            }

            throw new IOException(
                $"A different report is already stored at '{Path.GetFileName(path)}'.");
        }

        // CreateNew, а не Create: между проверкой и записью файл мог появиться,
        // и затирать его нельзя — сохранённый отчёт мог уже уйти врачу.
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Строит путь файла отчёта.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <returns>Полный путь файла.</returns>
    public string PathFor(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return this.PathFor(report, CanonicalReportJson.Serialize(report));
    }

    /// <summary>
    /// Строит путь файла по уже сериализованному содержимому.
    ///
    /// Имя складывается из псевдонима исследования, момента формирования и хеша
    /// содержимого. Одного времени мало: системные часы Windows идут шагами около
    /// 15мс, поэтому два разбора одного исследования подряд получили бы одинаковую
    /// отметку, и второй не сохранился бы вовсе. Хеш содержимого разводит их,
    /// а два побайтово одинаковых отчёта, наоборот, остаются одним файлом.
    /// Обе части непрозрачны и PHI не содержат.
    /// </summary>
    private string PathFor(AnalysisReport report, byte[] content)
    {
        var stamp = report.CreatedAt
            .ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);

        var discriminator = Convert.ToHexStringLower(SHA256.HashData(content))[..DiscriminatorLength];

        return Path.Combine(
            this.rootDirectory,
            report.PseudonymousStudyId,
            $"{stamp}-{discriminator}.json");
    }
}
