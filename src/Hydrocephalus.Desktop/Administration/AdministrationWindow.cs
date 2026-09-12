using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hydrocephalus.Desktop.Results;

namespace Hydrocephalus.Desktop.Administration;

/// <summary>
/// Окно администрирования: состояние установки и журнал аудита.
///
/// Окно только показывает. Ни одной кнопки, меняющей состояние, здесь нет
/// и не будет: журнал защищён от незаметного редактирования, и средство
/// правки журнала в самом приложении отменило бы смысл этой защиты. Уборка
/// рабочих копий тоже не выносится сюда — она идёт по сроку, а не по команде.
///
/// Текст приходит готовым из <see cref="AdministrationReadout"/>. Окно
/// ничего не формулирует само и потому не может сказать о журнале больше,
/// чем сказала проверка.
/// </summary>
public sealed class AdministrationWindow : Window
{
    private static readonly SolidColorBrush HeadingBrush = new(Color.FromRgb(0x9A, 0x9A, 0xA6));

    private static readonly SolidColorBrush NoteBrush = new(Color.FromRgb(0x86, 0x86, 0x94));

    private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0xC8, 0xC8, 0xD2));

    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(0xE0, 0xB0, 0x50));

    private static readonly SolidColorBrush BlockingBrush = new(Color.FromRgb(0xE0, 0x6A, 0x5A));

    /// <summary>
    /// Создаёт окно администрирования.
    /// </summary>
    /// <param name="sections">Разделы для показа.</param>
    public AdministrationWindow(IReadOnlyList<ResultSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        this.Title = "Администрирование";
        this.Width = 860;
        this.Height = 680;
        this.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14));
        this.FontSize = 13;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(14) };

        var actions = BuildActions();

        DockPanel.SetDock(actions, Dock.Bottom);

        panel.Children.Add(actions);
        panel.Children.Add(BuildBody(sections));

        this.Content = panel;
    }

    private static SolidColorBrush BrushFor(ResultSeverity severity) => severity switch
    {
        ResultSeverity.Blocking => BlockingBrush,
        ResultSeverity.Warning => WarningBrush,
        _ => NeutralBrush,
    };

    private static ScrollViewer BuildBody(IReadOnlyList<ResultSection> sections)
    {
        var stack = new StackPanel();

        foreach (var section in sections)
        {
            stack.Children.Add(new TextBlock
            {
                Text = section.Title,
                Foreground = HeadingBrush,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 14, 0, 6),
            });

            foreach (var row in section.Rows)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = row.Text,
                    Foreground = BrushFor(row.Severity),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2),
                });

                if (row.Note is null)
                {
                    continue;
                }

                stack.Children.Add(new TextBlock
                {
                    Text = row.Note,
                    Foreground = NoteBrush,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }
        }

        // Прокрутка достижима с клавиатуры: журнал длиннее экрана,
        // а работать приложение должно без мыши (docs/windows/README.md).
        return new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
            IsTabStop = true,
            Margin = new Thickness(0, 0, 0, 10),
        };
    }

    private static StackPanel BuildActions()
    {
        var close = new Button
        {
            Content = "_Закрыть",
            Padding = new Thickness(14, 5, 14, 5),
            IsDefault = true,
            IsCancel = true,
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { close },
        };
    }
}
