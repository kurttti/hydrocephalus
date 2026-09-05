using FellowOakDicom;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Выбор окна и уровня.
///
/// Порядок задан ADR 0007: значения из DICOM, потому что их выбрал тот, кто снимал,
/// и врач ожидает привычную картинку; расчёт по гистограмме — откат, когда тегов
/// нет или их значения ничего не показывают на этом объёме.
/// </summary>
public sealed class WindowLevelTests : IDisposable
{
    private const int Columns = 8;
    private const int Rows = 8;

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Mapping_follows_the_dicom_linear_voi_formula()
    {
        var window = new WindowLevel(Center: 100, Width: 200);

        Assert.Equal(0, window.Map(0));
        Assert.Equal(255, window.Map(200));
        Assert.Equal(128, window.Map(100));
    }

    [Fact]
    public void Values_outside_the_window_saturate_rather_than_wrap()
    {
        var window = new WindowLevel(Center: 100, Width: 50);

        Assert.Equal(0, window.Map(-5000));
        Assert.Equal(255, window.Map(5000));
    }

    [Fact]
    public void Degenerate_window_is_not_usable()
    {
        Assert.False(new WindowLevel(100, 0).IsUsable);
        Assert.False(new WindowLevel(double.NaN, 100).IsUsable);
        Assert.True(new WindowLevel(100, 1).IsUsable);
    }

    [Fact]
    public async Task Window_from_the_tags_is_preferred()
    {
        await this.WriteRampAsync(window: (Center: 300m, Width: 400m));

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(new WindowLevel(300, 400), volume.SuggestedWindow);
        Assert.Equal(new WindowLevel(300, 400), WindowLevelSelector.For(volume));
    }

    [Fact]
    public async Task Missing_tags_fall_back_to_the_histogram()
    {
        await this.WriteRampAsync(window: null);

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Null(volume.SuggestedWindow);

        var window = WindowLevelSelector.For(volume);

        Assert.True(window.IsUsable);
        Assert.True(window.Center > volume.Minimum && window.Center < volume.Maximum);
    }

    [Fact]
    public async Task Window_that_shows_nothing_is_replaced_by_the_histogram()
    {
        // Окно, целиком лежащее вне диапазона значений, даёт равномерно чёрный
        // экран — это выглядит как отсутствие данных, а не как ошибка настройки.
        await this.WriteRampAsync(window: (Center: 50000m, Width: 10m));

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(new WindowLevel(50000, 10), volume.SuggestedWindow);
        Assert.False(WindowLevelSelector.IsPlausible(volume.SuggestedWindow!.Value, volume));

        var window = WindowLevelSelector.For(volume);

        Assert.NotEqual(volume.SuggestedWindow, window);
        Assert.True(WindowLevelSelector.IsPlausible(window, volume));
    }

    [Fact]
    public async Task A_single_outlier_does_not_wash_out_the_window()
    {
        // Металлический артефакт или дефектный воксель растянул бы окно так,
        // что вся ткань стала бы одинаково серой.
        var values = new short[Columns * Rows];

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (short)(100 + (index % 20));
        }

        values[0] = 20000;

        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            Columns,
            Rows,
            positionMillimetres: 0,
            values);

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        var window = WindowLevelSelector.FromHistogram(volume);

        Assert.Equal(20000, volume.Maximum);
        Assert.True(window.Width < 1000, $"Window {window} was stretched by the outlier.");
    }

    [Fact]
    public async Task Uniform_volume_gets_a_usable_window_instead_of_a_degenerate_one()
    {
        // Растягивать шум однородного объёма до полной шкалы вредно, но и окно
        // нулевой ширины использовать нельзя.
        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            Columns,
            Rows,
            positionMillimetres: 0,
            new short[Columns * Rows]);

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.True(WindowLevelSelector.FromHistogram(volume).IsUsable);
    }

    [Fact]
    public async Task Only_the_first_preset_is_taken_from_multi_valued_tags()
    {
        // Теги допускают набор предустановок; выбирать между ними — работа врача,
        // а не молчаливое решение загрузчика.
        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            Columns,
            Rows,
            positionMillimetres: 0,
            Ramp(),
            customize: dataset =>
            {
                dataset.AddOrUpdate(DicomTag.WindowCenter, "120", "900");
                dataset.AddOrUpdate(DicomTag.WindowWidth, "240", "1800");
            });

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(new WindowLevel(120, 240), volume.SuggestedWindow);
    }

    private static short[] Ramp()
    {
        var values = new short[Columns * Rows];

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (short)(index * 8);
        }

        return values;
    }

    private async Task WriteRampAsync((decimal Center, decimal Width)? window)
    {
        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            Columns,
            Rows,
            positionMillimetres: 0,
            Ramp(),
            customize: window is null
                ? null
                : dataset =>
                {
                    dataset.AddOrUpdate(DicomTag.WindowCenter, window.Value.Center);
                    dataset.AddOrUpdate(DicomTag.WindowWidth, window.Value.Width);
                });

        await Task.CompletedTask;
    }
}
