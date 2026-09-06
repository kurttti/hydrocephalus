using System.Globalization;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Политика хранения рабочих копий (ADR 0006).
/// </summary>
public sealed record WorkingCopyRetentionPolicy
{
    /// <summary>
    /// Срок жизни рабочей копии. Значение по умолчанию — 24 часа.
    ///
    /// Срок задаётся администратором и может удалить данные незавершённого
    /// разбора случая. Это названо в ADR как риск: поведение должно быть
    /// предсказуемым и заранее видимым врачу, а не молчаливым.
    /// </summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Результат уборки.
/// </summary>
/// <param name="Removed">Число удалённых сеансов.</param>
/// <param name="Kept">Число оставленных сеансов.</param>
public readonly record struct RetentionSweep(int Removed, int Kept);

/// <summary>
/// Уборка рабочих копий: по сроку и после прерванных сеансов.
///
/// Выполняется при старте до начала любой новой работы (ADR 0006). Осиротевший
/// каталог остаётся после падения процесса или отключения питания, и сам он
/// не исчезнет: сеанс, который должен был его удалить, уже не выполняется.
///
/// Каталог без ключа удаляется тоже. Данные в нём уже нечитаемы — ключ либо
/// уничтожен, либо не был создан, — но занимать место и выглядеть рабочей
/// копией он не должен.
/// </summary>
public static class WorkingCopyRetention
{
    /// <summary>
    /// Убирает просроченные и осиротевшие рабочие копии.
    /// </summary>
    /// <param name="root">Корень рабочих копий.</param>
    /// <param name="now">Текущий момент.</param>
    /// <param name="policy">Политика хранения; null — значения по умолчанию.</param>
    /// <param name="activeSessionIds">
    /// Идентификаторы сеансов, которые сейчас идут и удалять которые нельзя.
    /// </param>
    /// <returns>Сколько сеансов удалено и сколько оставлено.</returns>
    public static RetentionSweep Sweep(
        string root,
        DateTimeOffset now,
        WorkingCopyRetentionPolicy? policy = null,
        IReadOnlySet<string>? activeSessionIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        policy ??= new WorkingCopyRetentionPolicy();
        activeSessionIds ??= new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(root))
        {
            return new RetentionSweep(0, 0);
        }

        var removed = 0;
        var kept = 0;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var sessionId = Path.GetFileName(directory);

            if (activeSessionIds.Contains(sessionId))
            {
                kept++;
                continue;
            }

            if (ShouldRemove(directory, now, policy.TimeToLive))
            {
                Remove(directory);
                removed++;
            }
            else
            {
                kept++;
            }
        }

        return new RetentionSweep(removed, kept);
    }

    /// <summary>
    /// Читает момент создания сеанса.
    /// </summary>
    /// <param name="directory">Каталог сеанса.</param>
    /// <returns>Момент создания либо <see langword="null"/>, если отметки нет.</returns>
    public static DateTimeOffset? CreatedAtOf(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, WorkingCopySession.StampFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            File.ReadAllText(path),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var createdAt)
            ? createdAt
            : null;
    }

    private static bool ShouldRemove(string directory, DateTimeOffset now, TimeSpan timeToLive)
    {
        if (!File.Exists(Path.Combine(directory, WorkingCopySession.KeyFileName)))
        {
            // Ключа нет — читать нечего. Такой каталог остался от прерванного
            // удаления либо от сеанса, не дошедшего до создания ключа.
            return true;
        }

        var createdAt = CreatedAtOf(directory);

        if (createdAt is null)
        {
            // Отметки нет или она нечитаема: возраст неизвестен. Оставить такой
            // каталог значило бы хранить данные бессрочно, а это ровно то,
            // против чего политика и вводится.
            return true;
        }

        return now - createdAt.Value >= timeToLive;
    }

    private static void Remove(string directory)
    {
        // Ключ первым: если удаление прервётся, данные уже нечитаемы.
        try
        {
            var keyPath = Path.Combine(directory, WorkingCopySession.KeyFileName);

            if (File.Exists(keyPath))
            {
                File.Delete(keyPath);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Каталог занят другим процессом. Ключ, если он был, уже удалён;
            // повторная попытка выполняется при следующем старте.
        }
    }
}
