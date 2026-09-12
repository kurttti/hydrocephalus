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
///
/// Причины удаления различаются: срок жизни и уборка за прерванным сеансом —
/// разные требования ADR 0006, и одно число на двоих не отвечает ни на один
/// из двух вопросов. Неудавшееся удаление считается отдельно и не попадает
/// в удалённые: каталог остался на диске, и называть его удалённым значило бы
/// записать в журнал неправду.
/// </summary>
/// <param name="Expired">Сеансов удалено по истечении срока жизни.</param>
/// <param name="Orphaned">Сеансов удалено как осиротевшие.</param>
/// <param name="Kept">Сеансов оставлено.</param>
/// <param name="Failed">Сеансов, которые не удалось удалить.</param>
public readonly record struct RetentionSweep(int Expired, int Orphaned, int Kept, int Failed)
{
    /// <summary>Всего удалено сеансов.</summary>
    public int Removed => Expired + Orphaned;
}

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
            return default;
        }

        var expired = 0;
        var orphaned = 0;
        var kept = 0;
        var failed = 0;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var sessionId = Path.GetFileName(directory);

            if (activeSessionIds.Contains(sessionId))
            {
                kept++;
                continue;
            }

            var reason = ReasonToRemove(directory, now, policy.TimeToLive);

            if (reason is null)
            {
                kept++;
                continue;
            }

            if (!Remove(directory))
            {
                // Каталог занят другим процессом. Ключ уже удалён, поэтому
                // содержимое нечитаемо, но каталог на диске остался — и считать
                // его удалённым значило бы записать в журнал неправду.
                failed++;
                continue;
            }

            if (reason == RemovalReason.Expired)
            {
                expired++;
            }
            else
            {
                orphaned++;
            }
        }

        return new RetentionSweep(expired, orphaned, kept, failed);
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

    private static RemovalReason? ReasonToRemove(
        string directory,
        DateTimeOffset now,
        TimeSpan timeToLive)
    {
        if (!File.Exists(Path.Combine(directory, WorkingCopySession.KeyFileName)))
        {
            // Ключа нет — читать нечего. Такой каталог остался от прерванного
            // удаления либо от сеанса, не дошедшего до создания ключа.
            return RemovalReason.Orphaned;
        }

        var createdAt = CreatedAtOf(directory);

        if (createdAt is null)
        {
            // Отметки нет или она нечитаема: возраст неизвестен. Оставить такой
            // каталог значило бы хранить данные бессрочно, а это ровно то,
            // против чего политика и вводится. Это не истёкший срок, а сеанс,
            // о котором ничего не известно, — и в журнале это разные строки.
            return RemovalReason.Orphaned;
        }

        return now - createdAt.Value >= timeToLive ? RemovalReason.Expired : null;
    }

    private static bool Remove(string directory)
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

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Каталог занят другим процессом. Ключ, если он был, уже удалён;
            // повторная попытка выполняется при следующем старте.
            return false;
        }
    }

    private enum RemovalReason
    {
        Expired,
        Orphaned,
    }
}
