using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hydrocephalus.Desktop.Composition;
using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Infrastructure.Volumes;
using Microsoft.Win32;

namespace Hydrocephalus.Desktop;

/// <summary>
/// Главное окно: три плоскости одного объёма, окно/уровень и оверлей маски.
///
/// Окно не выполняет медицинских вычислений и не читает DICOM напрямую:
/// оно получает готовое состояние просмотра от composition root и рисует его.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<PlaneSurface> surfaces = [];

    private StudyView? study;

    private Actor? actor;

    private bool hasOpenStudy;

    /// <summary>Создаёт главное окно.</summary>
    public MainWindow()
    {
        this.InitializeComponent();
    }

    private static App Current => (App)System.Windows.Application.Current;

    /// <summary>
    /// Показывает роль, с которой запущено приложение.
    ///
    /// Вызывается сборкой, а не читается окном: окно не решает, откуда берётся
    /// роль, иначе рядом с настройкой установки завёлся бы второй источник.
    /// </summary>
    /// <param name="value">Инициатор операций приложения.</param>
    internal void ShowActor(Actor value)
    {
        this.actor = value;
        this.RoleText.Text = RoleReadout.Describe(value);
        this.UpdateExportAvailability();
    }

    private async void OnOpenClick(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Каталог с DICOM-файлами исследования",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await this.OpenAsync(dialog.FolderName).ConfigureAwait(true);
    }

    private async Task OpenAsync(string directory)
    {
        var composition = Current.Composition;

        if (composition is null)
        {
            this.StatusText.Text = "Приложение не собрано; просмотр недоступен.";
            return;
        }

        this.OpenButton.IsEnabled = false;
        this.StatusText.Text = "Импорт и деидентификация…";

        try
        {
            // Импорт и загрузка объёма уходят с потока интерфейса: на реальной
            // серии это сотни файлов, и подвисшее окно выглядит как отказ.
            var opened = await Task.Run(
                () => composition.OpenForViewingAsync(directory, CancellationToken.None))
                .ConfigureAwait(true);

            this.Show(opened.View);
            this.hasOpenStudy = true;
            this.UpdateExportAvailability();
        }
        catch (AccessDeniedException)
        {
            // Отдельно от прочих отказов: у сообщения сценария технический
            // текст на английском, а врачу нужно понять, что дело не в данных
            // и не в файлах, а в том, как настроена установка.
            this.StatusText.Text =
                "Роль, заданная при установке, не даёт права на анализ исследования.";

            this.hasOpenStudy = false;
            this.UpdateExportAvailability();
        }
        catch (Exception exception)
        {
            // В сообщение попадает только техническая причина: ни имён файлов,
            // ни идентификаторов исследования в тексте ошибки быть не должно.
            this.StatusText.Text = "Открыть не удалось: " + exception.Message;

            // Предыдущее исследование к этому моменту уже освобождено сборкой,
            // и оставить кнопки экспорта включёнными значило бы предложить
            // выгрузить отчёт, которого больше нет.
            this.hasOpenStudy = false;
            this.UpdateExportAvailability();
        }
        finally
        {
            this.OpenButton.IsEnabled = true;
        }
    }

    private async void OnExportDeidentifiedClick(object sender, RoutedEventArgs e) =>
        await this.ExportAsync(
            "обезличенный отчёт",
            composition => composition.ExportReportAsync(
                ReportExportVariant.Deidentified,
                CancellationToken.None))
            .ConfigureAwait(true);

    private async void OnExportClinicalClick(object sender, RoutedEventArgs e) =>
        await this.ExportAsync(
            "клинический отчёт",
            composition => composition.ExportReportAsync(
                ReportExportVariant.Clinical,
                CancellationToken.None))
            .ConfigureAwait(true);

    private async void OnExportManifestClick(object sender, RoutedEventArgs e) =>
        await this.ExportAsync(
            "манифест датасета",
            composition => composition.ExportDatasetManifestAsync(CancellationToken.None))
            .ConfigureAwait(true);

    private async Task ExportAsync(string what, Func<CompositionRoot, Task<string>> export)
    {
        var composition = Current.Composition;

        if (composition is null)
        {
            this.StatusText.Text = "Приложение не собрано; экспорт недоступен.";
            return;
        }

        try
        {
            var directory = await export(composition).ConfigureAwait(true);

            // Показывается каталог, а не файл: имя файла содержит псевдоним
            // исследования, а строка состояния видна на экране в кабинете.
            this.StatusText.Text = $"Экспортирован {what}. Каталог: {directory}";
        }
        catch (AccessDeniedException)
        {
            // Отдельно от прочих ошибок: «вам нельзя» и «с данными что-то
            // не так» — разные сообщения. Отказ уже записан в аудит сценарием.
            this.StatusText.Text = $"Роль не даёт права на {what}.";
        }
        catch (Exception exception)
        {
            this.StatusText.Text = $"Экспорт не удался ({what}): " + exception.Message;
        }
    }

    /// <summary>
    /// Включает те действия экспорта, которые возможны прямо сейчас.
    ///
    /// Это удобство, а не защита: права проверяет сценарий и пишет отказ
    /// в аудит. Экран лишь не предлагает того, что заведомо не состоится, —
    /// кнопка, которая всегда падает, хуже выключенной кнопки с причиной.
    /// </summary>
    private void UpdateExportAvailability()
    {
        var current = this.actor;

        Configure(
            this.ExportDeidentifiedButton,
            current?.Can(Capability.ExportDeidentifiedReport) == true,
            registryNeeded: false,
            this.hasOpenStudy);

        Configure(
            this.ExportClinicalButton,
            current?.Can(Capability.ExportClinicalReport) == true,
            registryNeeded: true,
            this.hasOpenStudy);

        Configure(
            this.ExportManifestButton,
            current?.Can(Capability.ExportDatasetManifest) == true,
            registryNeeded: false,
            this.hasOpenStudy);

        static void Configure(Button button, bool granted, bool registryNeeded, bool hasStudy)
        {
            var blockedByRegistry = registryNeeded && !CompositionRoot.PatientRegistryConfigured;

            button.IsEnabled = granted && hasStudy && !blockedByRegistry;

            button.ToolTip = !granted
                ? "Роль, заданная при установке, не даёт этого права."
                : blockedByRegistry
                    ? "Внешний реестр идентификаторов пациента не подключён в этой установке."
                    : hasStudy
                        ? null
                        : "Исследование не открыто.";
        }
    }

    private void Show(StudyView opened)
    {
        this.study = opened;
        this.surfaces.Clear();
        this.PlaneGrid.Children.Clear();

        for (var index = 0; index < opened.Planes.Count; index++)
        {
            var surface = new PlaneSurface(opened.Planes[index]);

            Grid.SetColumn(surface.Root, index);
            this.PlaneGrid.Children.Add(surface.Root);
            this.surfaces.Add(surface);
        }

        this.ConfigureWindowSliders(opened);

        this.StatusText.Text = opened.HasMask
            ? "Открыто. Маска получена baseline-методом и не является проверенной сегментацией."
            : "Открыто. Маска не построена: взвешенность серии не распознана.";

        foreach (var surface in this.surfaces)
        {
            surface.Redraw();
        }
    }

    private void ConfigureWindowSliders(StudyView opened)
    {
        var window = opened.Window;

        this.CenterSlider.ValueChanged -= this.OnWindowChanged;
        this.WidthSlider.ValueChanged -= this.OnWindowChanged;

        this.CenterSlider.Minimum = window.Center - (window.Width * 2);
        this.CenterSlider.Maximum = window.Center + (window.Width * 2);
        this.CenterSlider.Value = window.Center;

        this.WidthSlider.Minimum = WindowLevel.MinWidth;
        this.WidthSlider.Maximum = Math.Max(window.Width * 4, WindowLevel.MinWidth + 1);
        this.WidthSlider.Value = window.Width;

        this.CenterSlider.IsEnabled = true;
        this.WidthSlider.IsEnabled = true;

        this.CenterSlider.ValueChanged += this.OnWindowChanged;
        this.WidthSlider.ValueChanged += this.OnWindowChanged;

        this.ShowWindowReadout(window);
    }

    private void OnWindowChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (this.study is null)
        {
            return;
        }

        var window = new WindowLevel(this.CenterSlider.Value, this.WidthSlider.Value);

        this.study.Window = window;
        this.ShowWindowReadout(window);

        foreach (var surface in this.surfaces)
        {
            surface.Redraw();
        }
    }

    private void ShowWindowReadout(WindowLevel window) =>
        this.WindowReadout.Text = window.ToString();

    private void OnOverlayChanged(object sender, RoutedEventArgs e)
    {
        if (this.study is null)
        {
            return;
        }

        this.study.ShowOverlay = this.OverlayToggle.IsChecked == true;

        foreach (var surface in this.surfaces)
        {
            surface.Redraw();
        }
    }

    /// <summary>
    /// Один вид: заголовок, изображение, подписи сторон и ползунок среза.
    ///
    /// Собирается кодом, а не разметкой: три вида отличаются только данными,
    /// и три почти одинаковых блока XAML разошлись бы при первой же правке —
    /// а расходиться им нельзя, подписи сторон клинически значимы.
    /// </summary>
    private sealed class PlaneSurface
    {
        private static readonly SolidColorBrush LabelBrush =
            new(Color.FromRgb(0xE6, 0xE6, 0xEA));

        private readonly PlaneView view;
        private readonly Image image = new()
        {
            Stretch = Stretch.Uniform,

            // Ближайший сосед: масштабирование сглаживанием подрисовывает
            // промежуточные значения, которых в данных нет.
            SnapsToDevicePixels = true,
        };

        private readonly TextBlock title = new() { Foreground = LabelBrush, Margin = new Thickness(4) };
        private readonly TextBlock left = Label(HorizontalAlignment.Left, VerticalAlignment.Center);
        private readonly TextBlock right = Label(HorizontalAlignment.Right, VerticalAlignment.Center);
        private readonly TextBlock top = Label(HorizontalAlignment.Center, VerticalAlignment.Top);
        private readonly TextBlock bottom = Label(HorizontalAlignment.Center, VerticalAlignment.Bottom);
        private readonly Slider slider = new() { Margin = new Thickness(4) };
        private readonly TextBlock position = new() { Foreground = LabelBrush, Margin = new Thickness(4, 0, 4, 4) };

        internal PlaneSurface(PlaneView view)
        {
            this.view = view;

            RenderOptions.SetBitmapScalingMode(this.image, BitmapScalingMode.NearestNeighbor);

            this.slider.Minimum = 0;
            this.slider.Maximum = view.Count - 1;
            this.slider.Value = view.Index;
            this.slider.ValueChanged += this.OnSliderChanged;

            var canvas = new Grid { Background = Brushes.Black, ClipToBounds = true };
            canvas.Children.Add(this.image);
            canvas.Children.Add(this.left);
            canvas.Children.Add(this.right);
            canvas.Children.Add(this.top);
            canvas.Children.Add(this.bottom);

            // Колесо мыши листает срезы — привычный для станции жест.
            canvas.MouseWheel += this.OnMouseWheel;

            var panel = new DockPanel { Margin = new Thickness(4) };

            DockPanel.SetDock(this.title, Dock.Top);
            DockPanel.SetDock(this.slider, Dock.Bottom);
            DockPanel.SetDock(this.position, Dock.Bottom);

            panel.Children.Add(this.title);
            panel.Children.Add(this.position);
            panel.Children.Add(this.slider);
            panel.Children.Add(canvas);

            this.Root = panel;
        }

        internal FrameworkElement Root { get; }

        internal void Redraw()
        {
            var plane = this.view.Image;
            var composed = PlaneComposer.Compose(plane, this.view.Overlay);

            var bitmap = new WriteableBitmap(
                composed.Width,
                composed.Height,
                96,
                96,
                PixelFormats.Bgra32,
                palette: null);

            bitmap.WritePixels(
                new Int32Rect(0, 0, composed.Width, composed.Height),
                composed.Bgra,
                composed.Width * 4,
                0);

            this.image.Source = bitmap;

            // Пропорции задаются физическим размером пикселя, а не их числом.
            // Растяжение «по содержимому» сохраняет отношение сторон в пикселях,
            // а у анизотропного объёма пиксель не квадратный: без поправки
            // анатомия выходит вытянутой, а по ней потом измеряют.
            var pixelWidth = composed.DisplayWidthMillimetres / composed.Width;
            var pixelHeight = composed.DisplayHeightMillimetres / composed.Height;

            this.image.LayoutTransform = pixelWidth >= pixelHeight
                ? new ScaleTransform(pixelWidth / pixelHeight, 1)
                : new ScaleTransform(1, pixelHeight / pixelWidth);

            var labels = this.view.Labels;

            this.title.Text = OrientationLetters.NameOf(this.view.Plane, labels.IsOblique);
            this.left.Text = OrientationLetters.Of(labels.Left);
            this.right.Text = OrientationLetters.Of(labels.Right);
            this.top.Text = OrientationLetters.Of(labels.Top);
            this.bottom.Text = OrientationLetters.Of(labels.Bottom);

            this.position.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"Срез {this.view.Index + 1} из {this.view.Count}");
        }

        private static TextBlock Label(HorizontalAlignment horizontal, VerticalAlignment vertical) => new()
        {
            Foreground = LabelBrush,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(6),
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
        };

        private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            this.view.Index = (int)Math.Round(e.NewValue);
            this.Redraw();
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            this.view.Step(e.Delta > 0 ? 1 : -1);
            this.slider.Value = this.view.Index;

            e.Handled = true;
        }
    }
}
