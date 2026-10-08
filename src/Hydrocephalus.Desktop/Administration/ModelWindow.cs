using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hydrocephalus.Application;
using Hydrocephalus.Desktop.Composition;
using Hydrocephalus.Desktop.Results;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Infrastructure.Models;
using Microsoft.Win32;

namespace Hydrocephalus.Desktop.Administration;

/// <summary>
/// Экран модели: какие пакеты установлены, какой действует, установка и откат.
///
/// Требование ADR 0008: доставка только ручная и офлайн, установка — явное
/// действие администратора с показом версии, ключа, подписи и карточки до
/// подтверждения, откат — переключение метки на другую установленную версию.
/// Приложение не скачивает пакеты и не проверяет обновления по сети, и этого
/// окна тоже это касается: здесь есть только выбор файла на диске.
///
/// Откат отдельной кнопкой не выделен: по ADR 0008 это та же операция —
/// «сделать действующей» для одной из установленных версий. Две кнопки для
/// одного действия пришлось бы держать в согласии, а разошлись бы они молча.
///
/// Переключение вступает в силу со следующего запуска: способ разметки
/// собирается один раз при сборке приложения. Так и выполняется запрет менять
/// версию во время анализа — подменить способ у уже собранного конвейера
/// нечем, и отчёт не может сослаться на версию, которой он не получен.
/// </summary>
public sealed class ModelWindow : Window
{
    private static readonly SolidColorBrush ControlBackground = new(Color.FromRgb(0x1A, 0x1A, 0x22));

    private static readonly SolidColorBrush ControlForeground = new(Color.FromRgb(0xC8, 0xC8, 0xD2));

    private readonly CompositionRoot composition;

    private readonly DockPanel panel;

    private readonly ListBox versions;

    private readonly TextBlock status;

    private readonly Button install;

    private readonly Button activate;

    private ScrollViewer body;

    /// <summary>
    /// Создаёт экран модели.
    /// </summary>
    /// <param name="composition">Собранное приложение.</param>
    public ModelWindow(CompositionRoot composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        this.composition = composition;

        this.Title = "Модель";
        this.Width = 820;
        this.Height = 640;
        this.Background = SectionPanel.Background;
        this.FontSize = 13;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        this.versions = new ListBox
        {
            Height = 112,
            Background = ControlBackground,
            Foreground = ControlForeground,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
        };

        this.status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = ControlForeground,
            Margin = new Thickness(0, 0, 0, 8),
        };

        this.install = new Button
        {
            Content = "_Установить…",
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0),
        };

        this.activate = new Button
        {
            Content = "_Сделать действующей",
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0),
        };

        this.install.Click += async (_, _) => await this.InstallAsync().ConfigureAwait(true);
        this.activate.Click += async (_, _) => await this.ActivateAsync().ConfigureAwait(true);

        this.body = SectionPanel.Build([]);
        this.panel = new DockPanel { Margin = new Thickness(14) };

        var bottom = new StackPanel();

        bottom.Children.Add(new TextBlock
        {
            Text = "Установленные версии",
            Foreground = ControlForeground,
            Margin = new Thickness(0, 8, 0, 4),
        });
        bottom.Children.Add(this.versions);
        bottom.Children.Add(this.status);
        bottom.Children.Add(this.BuildActions());

        DockPanel.SetDock(bottom, Dock.Bottom);

        this.panel.Children.Add(bottom);
        this.panel.Children.Add(this.body);

        this.Content = this.panel;

        this.Refresh();
    }

    private StackPanel BuildActions()
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
            Children = { this.install, this.activate, close },
        };
    }

    /// <summary>
    /// Перечитывает состояние установки и перерисовывает окно.
    ///
    /// После каждого действия: показанное состояние иначе расходится с тем,
    /// что на диске, и администратор судит о результате по устаревшему экрану.
    /// </summary>
    private void Refresh()
    {
        var state = this.composition.ModelState();

        this.panel.Children.Remove(this.body);
        this.body = SectionPanel.Build(ModelReadout.Describe(state));
        this.panel.Children.Add(this.body);

        var selected = this.versions.SelectedItem as string;

        this.versions.Items.Clear();

        foreach (var version in state.InstalledVersions)
        {
            this.versions.Items.Add(version);
        }

        if (selected is not null && state.InstalledVersions.Contains(selected, StringComparer.Ordinal))
        {
            this.versions.SelectedItem = selected;
        }

        // Кнопки выключаются там, где действие невозможно, и причина названа
        // строкой в разделе: кнопка, которая всегда падает, хуже выключенной.
        var allowed = state.TrustKeyConfigured && !state.StudyOpen;

        this.install.IsEnabled = allowed;
        this.activate.IsEnabled = allowed && state.InstalledVersions.Count > 0;
    }

    private async Task InstallAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите пакет модели",
            Filter = "Пакет модели (*" + InstalledModelStore.PackageExtension + ")|*"
                + InstalledModelStore.PackageExtension,
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = dialog.FileName;

        await this.RunAsync(async () =>
        {
            var inspection = await this.composition
                .InspectModelPackageAsync(path, CancellationToken.None)
                .ConfigureAwait(true);

            var confirmed = new ModelConfirmWindow(
                ModelReadout.DescribeInspection(
                    inspection,
                    this.composition.ModelState().ActiveManifest),
                inspection.Accepted)
            { Owner = this }
                .ShowDialog();

            if (confirmed != true)
            {
                // Отказ администратора ничего не изменил и в журнал не идёт:
                // запись о пакете означала бы установку, которой не было.
                return new ResultRow
                {
                    Text = "Установка отменена.",
                    Severity = ResultSeverity.Neutral,
                };
            }

            // Проверка выполняется заново внутри сценария: между показом
            // карточки и подтверждением файл остаётся доступным для записи.
            var installation = await this.composition
                .InstallModelPackageAsync(path, CancellationToken.None)
                .ConfigureAwait(true);

            if (installation.Outcome != ModelInstallOutcome.Installed)
            {
                return ModelReadout.Describe(installation);
            }

            var activation = await this.composition
                .ActivateModelVersionAsync(installation.ModelVersion, CancellationToken.None)
                .ConfigureAwait(true);

            return ModelReadout.Describe(activation);
        }).ConfigureAwait(true);
    }

    private async Task ActivateAsync()
    {
        if (this.versions.SelectedItem is not string version)
        {
            this.Show(new ResultRow
            {
                Text = "Выберите версию в списке.",
                Severity = ResultSeverity.Warning,
            });

            return;
        }

        await this.RunAsync(async () =>
            ModelReadout.Describe(
                await this.composition
                    .ActivateModelVersionAsync(version, CancellationToken.None)
                    .ConfigureAwait(true)))
            .ConfigureAwait(true);
    }

    private async Task RunAsync(Func<Task<ResultRow>> action)
    {
        this.install.IsEnabled = false;
        this.activate.IsEnabled = false;

        try
        {
            this.Show(await action().ConfigureAwait(true));
        }
        catch (AccessDeniedException)
        {
            // Право проверяет сценарий, а не это окно: роль задаётся установкой,
            // и окно показывает отказ, а не решает его.
            this.Show(new ResultRow
            {
                Text = "Роль не даёт права на установку модели.",
                Severity = ResultSeverity.Blocking,
            });
        }
        catch (Exception exception)
        {
            this.Show(new ResultRow
            {
                Text = "Не удалось: " + ErrorReadout.Describe(exception),
                Severity = ResultSeverity.Blocking,
            });
        }
        finally
        {
            // Состояние перечитывается в любом случае: после неудачи оно тоже
            // могло измениться — например, пакет лёг в хранилище, а метка нет.
            this.Refresh();
        }
    }

    private void Show(ResultRow row)
    {
        this.status.Text = row.Note is null ? row.Text : row.Text + " " + row.Note;
        this.status.Foreground = row.Severity switch
        {
            ResultSeverity.Blocking => new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x5A)),
            ResultSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x50)),
            _ => ControlForeground,
        };
    }
}
