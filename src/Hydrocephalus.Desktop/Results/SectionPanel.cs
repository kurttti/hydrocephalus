using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hydrocephalus.Desktop.Results;

/// <summary>
/// Отрисовка разделов с готовым текстом.
///
/// Вынесена из окна администрирования, когда экранов таких стало два: цвет
/// оговорки и цвет препятствия должны совпадать везде, иначе один и тот же
/// отказ читается на одном экране как замечание, а на другом как запрет.
///
/// Здесь нет ни одной формулировки: текст приходит готовым с уровня чтения
/// состояния (ADR 0005), и окно не может сказать больше, чем сказала проверка.
/// </summary>
public static class SectionPanel
{
    private static readonly SolidColorBrush HeadingBrush = new(Color.FromRgb(0x9A, 0x9A, 0xA6));

    private static readonly SolidColorBrush NoteBrush = new(Color.FromRgb(0x86, 0x86, 0x94));

    private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0xC8, 0xC8, 0xD2));

    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(0xE0, 0xB0, 0x50));

    private static readonly SolidColorBrush BlockingBrush = new(Color.FromRgb(0xE0, 0x6A, 0x5A));

    /// <summary>Цвет фона окон обслуживания установки.</summary>
    public static SolidColorBrush Background { get; } = new(Color.FromRgb(0x10, 0x10, 0x14));

    /// <summary>
    /// Собирает прокручиваемую область с разделами.
    /// </summary>
    /// <param name="sections">Разделы для показа.</param>
    /// <returns>Готовая область.</returns>
    public static ScrollViewer Build(IReadOnlyList<ResultSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

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

    private static SolidColorBrush BrushFor(ResultSeverity severity) => severity switch
    {
        ResultSeverity.Blocking => BlockingBrush,
        ResultSeverity.Warning => WarningBrush,
        _ => NeutralBrush,
    };
}
