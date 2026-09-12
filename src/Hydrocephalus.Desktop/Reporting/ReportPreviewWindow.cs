using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Desktop.Reporting;

/// <summary>
/// Окно предпросмотра отчёта перед экспортом.
///
/// Экспорт — действие с последствиями вовне, и его нельзя выполнить не глядя:
/// врач сначала видит содержимое, и только потом решает, отправлять ли его.
/// Поэтому предпросмотр не отдельная кнопка рядом с экспортом, а обязательный
/// шаг перед ним — кнопку «посмотреть» можно не нажать.
///
/// Текст приходит готовым из <see cref="ReportOutline"/> — той же раскладкой,
/// которая уходит в PDF. Окно ничего не формулирует само и не может показать
/// ни больше, ни меньше записываемого файла.
/// </summary>
public sealed class ReportPreviewWindow : Window
{
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(0xE6, 0xE6, 0xEA));

    private static readonly SolidColorBrush HeadingBrush = new(Color.FromRgb(0x9A, 0x9A, 0xA6));

    /// <summary>
    /// Создаёт окно предпросмотра.
    /// </summary>
    /// <param name="what">Название варианта экспорта для заголовка.</param>
    /// <param name="lines">Строки отчёта.</param>
    public ReportPreviewWindow(string what, IReadOnlyList<ReportLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(lines);

        this.Title = "Предпросмотр: " + what;
        this.Width = 720;
        this.Height = 640;
        this.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14));
        this.FontSize = 13;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(14) };

        var warning = BuildWarning();
        var actions = this.BuildActions(what);

        DockPanel.SetDock(warning, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);

        panel.Children.Add(warning);
        panel.Children.Add(actions);
        panel.Children.Add(BuildBody(lines));

        this.Content = panel;
    }

    /// <summary>Подтвердил ли пользователь экспорт.</summary>
    public bool Confirmed { get; private set; }

    private static ScrollViewer BuildBody(IReadOnlyList<ReportLine> lines)
    {
        var stack = new StackPanel();

        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line.Text,
                Foreground = line.IsHeading ? HeadingBrush : TextBrush,
                FontWeight = line.IsHeading ? FontWeights.Bold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(line.Depth * 16, 0, 0, line.GapAfter ? 10 : 2),
            });
        }

        if (lines.Count == 0)
        {
            // Пустой предпросмотр — это состояние, а не отсутствие окна:
            // молча показать пустоту значило бы предложить отправить неизвестно что.
            stack.Children.Add(new TextBlock
            {
                Text = "Отчёт пуст.",
                Foreground = HeadingBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Прокрутка достижима с клавиатуры: содержимое длиннее экрана,
        // а работать окно должно без мыши (docs/windows/README.md).
        return new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
            IsTabStop = true,
            Margin = new Thickness(0, 10, 0, 10),
        };
    }

    private static TextBlock BuildWarning() => new TextBlock
    {
        Text = "Это содержимое будет записано в файл. Проверьте его до экспорта.",
        Foreground = HeadingBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private StackPanel BuildActions(string what)
    {
        var export = new Button
        {
            Content = "_Экспортировать",
            Padding = new Thickness(14, 5, 14, 5),
            IsDefault = true,
            ToolTip = "Записать " + what + " в файл",
        };

        export.Click += (_, _) =>
        {
            this.Confirmed = true;
            this.Close();
        };

        // Отмена по Esc и она же по умолчанию при закрытии крестиком:
        // закрытое окно означает «не отправлять», а не «отправить молча».
        var cancel = new Button
        {
            Content = "О_тмена",
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(14, 5, 14, 5),
            IsCancel = true,
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { export, cancel },
        };
    }
}
