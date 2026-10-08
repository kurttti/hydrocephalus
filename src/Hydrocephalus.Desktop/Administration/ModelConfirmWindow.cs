using System.Windows;
using System.Windows.Controls;
using Hydrocephalus.Desktop.Results;

namespace Hydrocephalus.Desktop.Administration;

/// <summary>
/// Подтверждение установки пакета модели.
///
/// Отдельное окно, а не вопрос «вы уверены?», потому что ADR 0008 требует
/// показать до подтверждения версию, идентификатор ключа, статус подписи,
/// карточку модели и отличия от действующей версии. Вопрос без этого
/// заставлял бы подтверждать то, чего администратор не видел.
///
/// Кнопка подтверждения появляется только у принятого пакета: отвергнутый
/// установить нельзя вовсе (ADR 0004 — частичная загрузка запрещена), и
/// выключенная кнопка рядом с причиной отказа читается как «можно настоять».
/// </summary>
public sealed class ModelConfirmWindow : Window
{
    /// <summary>
    /// Создаёт окно подтверждения.
    /// </summary>
    /// <param name="sections">Что показать об осмотренном пакете.</param>
    /// <param name="acceptable">Прошёл ли пакет проверки.</param>
    public ModelConfirmWindow(IReadOnlyList<ResultSection> sections, bool acceptable)
    {
        ArgumentNullException.ThrowIfNull(sections);

        this.Title = "Установка пакета модели";
        this.Width = 760;
        this.Height = 620;
        this.Background = SectionPanel.Background;
        this.FontSize = 13;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(14) };

        var actions = this.BuildActions(acceptable);

        DockPanel.SetDock(actions, Dock.Bottom);

        panel.Children.Add(actions);
        panel.Children.Add(SectionPanel.Build(sections));

        this.Content = panel;
    }

    private StackPanel BuildActions(bool acceptable)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (acceptable)
        {
            var confirm = new Button
            {
                Content = "_Установить и сделать действующей",
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0),
            };

            // Подтверждение не назначено кнопкой по умолчанию намеренно:
            // Enter, нажатый не глядя, не должен менять измерительный
            // инструмент. По умолчанию здесь отмена.
            confirm.Click += (_, _) =>
            {
                this.DialogResult = true;
                this.Close();
            };

            buttons.Children.Add(confirm);
        }

        buttons.Children.Add(new Button
        {
            Content = "_Отмена",
            Padding = new Thickness(14, 5, 14, 5),
            IsDefault = true,
            IsCancel = true,
        });

        return buttons;
    }
}
