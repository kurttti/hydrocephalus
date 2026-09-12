using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Уборка рабочих копий за портом <see cref="IWorkingCopyRetention"/>.
///
/// Адаптер держит то, чего не знает домен: где лежат каталоги сеансов и какой
/// срок задан установкой. Сценарий получает от него только итог — сколько
/// сеансов ушло, по какой причине и по какому сроку.
/// </summary>
public sealed class WorkingCopyRetentionService : IWorkingCopyRetention
{
    private readonly string root;
    private readonly WorkingCopyRetentionPolicy policy;

    /// <summary>Создаёт уборку.</summary>
    /// <param name="rootDirectory">Корень рабочих копий.</param>
    /// <param name="policy">Политика хранения.</param>
    public WorkingCopyRetentionService(string rootDirectory, WorkingCopyRetentionPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        this.root = rootDirectory;
        this.policy = policy;
    }

    /// <summary>
    /// Убирает просроченные и осиротевшие рабочие копии.
    /// </summary>
    /// <param name="now">Текущий момент.</param>
    /// <returns>Что было убрано и по какому сроку.</returns>
    public WorkingCopyRetentionOutcome Sweep(DateTimeOffset now)
    {
        var sweep = WorkingCopyRetention.Sweep(this.root, now, this.policy);

        return new WorkingCopyRetentionOutcome(
            sweep.Expired,
            sweep.Orphaned,
            sweep.Failed,

            // Срок пишется в журнал вместе с итогом: без него запись отвечает
            // «сколько удалено», но не «по какому правилу», а правило задаёт
            // установка и оно может отличаться от значения по умолчанию.
            this.policy.TimeToLive.TotalHours);
    }
}
