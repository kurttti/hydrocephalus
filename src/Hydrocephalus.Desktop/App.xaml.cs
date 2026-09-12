using System.Windows;
using Hydrocephalus.Desktop.Composition;

namespace Hydrocephalus.Desktop;

/// <summary>
/// Composition root приложения: здесь собираются реализации портов.
/// Базовый тип указан полным именем намеренно — короткое <c>Application</c> разрешается
/// в пространство имён <see cref="Hydrocephalus.Application"/>, а не в тип WPF.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>Собранное приложение; доступно после запуска.</summary>
    internal CompositionRoot? Composition { get; private set; }

    /// <summary>
    /// Собирает приложение при запуске.
    /// </summary>
    /// <param name="e">Аргументы запуска.</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            this.Composition = await CompositionRoot
                .CreateAsync(ApplicationPaths.UnderLocalApplicationData(), CancellationToken.None)
                .ConfigureAwait(true);

            // Окно создаётся по StartupUri до того, как сборка завершится,
            // поэтому роль в него передаётся, а не читается им при загрузке.
            if (this.MainWindow is MainWindow window)
            {
                window.ShowActor(this.Composition.Actor);
                window.ShowRetention(RetentionSettings.Describe(this.Composition.Retention));
            }
        }
        catch (Exception exception)
        {
            // Сборка не удалась — как правило, недоступна соль псевдонимизации.
            // Работать без неё нельзя: импорт не построит ни одного псевдонима.
            // Показывается категория по типу исключения, а не его текст: текст
            // исключения ввода-вывода содержит путь, а путь профиля — имя учётной
            // записи (см. ErrorReadout).
            MessageBox.Show(
                ErrorReadout.Describe(exception),
                "Не удалось запустить приложение",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            this.Shutdown(1);
        }
    }

    /// <summary>
    /// Освобождает ресурсы при завершении.
    /// </summary>
    /// <param name="e">Аргументы завершения.</param>
    protected override void OnExit(ExitEventArgs e)
    {
        this.Composition?.Dispose();
        base.OnExit(e);
    }
}
