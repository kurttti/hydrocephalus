namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Освобождение рабочей копии.
///
/// Отдельный порт, а не метод импорта: удаляет копию не тот, кто её создал,
/// а тот, кто закончил с ней работать. Сценарий анализа обязан освободить её
/// в finally — на успехе, ошибке и отмене (ADR 0006), и отсутствие очистки
/// считается дефектом, а не шумом.
/// </summary>
public interface IWorkingCopyLifetime
{
    /// <summary>
    /// Уничтожает рабочую копию.
    /// </summary>
    /// <param name="volumeReference">Ссылка на рабочую копию.</param>
    /// <returns>Задача уничтожения.</returns>
    Task ReleaseAsync(string volumeReference);
}
