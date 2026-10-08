using System.Windows;
using System.Windows.Controls;
using Hydrocephalus.Desktop.Results;

namespace Hydrocephalus.Desktop.Administration;

/// <summary>
/// Окно администрирования: состояние установки и журнал аудита.
///
/// Журнал здесь только показывается. Ни одной кнопки, меняющей журнал, в этом
/// окне нет и не будет: он защищён от незаметного редактирования, и средство
/// правки журнала в самом приложении отменило бы смысл этой защиты. Уборка
/// рабочих копий тоже не выносится сюда — она идёт по сроку, а не по команде.
///
/// Единственная кнопка, кроме закрытия, открывает экран модели. Сама она
/// ничего не меняет; установка и переключение версии требуют подтверждения
/// там, и это требование ADR 0008, а не вольность: смена модели меняет то,
/// что приложение измеряет у всех последующих пациентов, и выполняться должна
/// явным действием администратора.
///
/// Текст приходит готовым из <see cref="AdministrationReadout"/>. Окно
/// ничего не формулирует само и потому не может сказать о журнале больше,
/// чем сказала проверка.
/// </summary>
public sealed class AdministrationWindow : Window
{
    /// <summary>
    /// Создаёт окно администрирования.
    /// </summary>
    /// <param name="sections">Разделы для показа.</param>
    /// <param name="openModels">
    /// Чем открыть экран модели; <see langword="null"/>, если открывать нечем —
    /// тогда кнопки нет. Выключенная кнопка без причины хуже её отсутствия.
    /// </param>
    public AdministrationWindow(
        IReadOnlyList<ResultSection> sections,
        Action<Window>? openModels = null)
    {
        ArgumentNullException.ThrowIfNull(sections);

        this.Title = "Администрирование";
        this.Width = 860;
        this.Height = 680;
        this.Background = SectionPanel.Background;
        this.FontSize = 13;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(14) };

        var actions = this.BuildActions(openModels);

        DockPanel.SetDock(actions, Dock.Bottom);

        panel.Children.Add(actions);
        panel.Children.Add(SectionPanel.Build(sections));

        this.Content = panel;
    }

    private StackPanel BuildActions(Action<Window>? openModels)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (openModels is not null)
        {
            var models = new Button
            {
                Content = "_Модель…",
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0),
            };

            // Владельцем экрана модели становится это окно: иначе он остался бы
            // висеть, когда администрирование закрыли.
            models.Click += (_, _) => openModels(this);

            buttons.Children.Add(models);
        }

        buttons.Children.Add(new Button
        {
            Content = "_Закрыть",
            Padding = new Thickness(14, 5, 14, 5),
            IsDefault = true,
            IsCancel = true,
        });

        return buttons;
    }
}
