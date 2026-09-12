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

        // Подписка до сборки: исключение из обработчика, не обёрнутого в try,
        // иначе дошло бы до стандартного окна WPF, а оно показывает текст
        // исключения вместе со стеком — то есть путь к исходному файлу,
        // в котором бывает фамилия пациента.
        this.DispatcherUnhandledException += this.OnDispatcherUnhandledException;

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
    /// Необработанное исключение в потоке интерфейса.
    ///
    /// Показывается категория по типу, а не текст исключения (см. ErrorReadout).
    /// Приложение после этого закрывается, а не продолжает работу: состояние,
    /// в котором оно оказалось, неизвестно, а при закрытии рабочая копия
    /// с данными пациента уничтожается — продолжать с неизвестным состоянием
    /// значило бы оставить её на диске неопределённо долго.
    ///
    /// Исключения фоновых потоков сюда не приходят и остаются за Windows;
    /// все длительные операции окна выполняются через await в потоке интерфейса
    /// и обёрнуты в обработчики с той же раскладкой.
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        MessageBox.Show(
            ErrorReadout.Describe(e.Exception),
            "Непредвиденная ошибка — приложение будет закрыто",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        this.Shutdown(2);
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
