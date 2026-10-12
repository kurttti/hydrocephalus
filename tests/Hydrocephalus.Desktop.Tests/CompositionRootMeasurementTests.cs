using System.IO;
using Hydrocephalus.Desktop.Composition;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Связка «выбранная серия — отчёт — записанное измерение» на собранном приложении.
///
/// Проверяется то, что до сих пор было проверено только чтением кода: угол
/// мозолистого тела размечают на корональной серии, а для анализа ранжирование
/// выбирает аксиальную, и если переключение серии не пересобирает текущий
/// отчёт, отметка ляжет в отчёт **другой** серии и будет выглядеть правильной.
/// Заметить такую ошибку по работающему приложению нечем: число на месте,
/// подпись на месте, и расходятся они молча.
///
/// Тест сценарный: настоящая сборка над временными каталогами и настоящие
/// DICOM-файлы. Подменять здесь нечего — проверяется именно то, как части
/// соединены, а на подставных объектах соединение проверить нельзя.
///
/// **Чего он не покрывает:** нажатий в окне. Вызов
/// <c>MainWindow.OnAngleRecord</c> по-прежнему проверен только чтением.
/// </summary>
public sealed class CompositionRootMeasurementTests : IDisposable
{
    private const string StudyUid = "1.2.826.0.1.3680043.9.7133.4.1";
    private const string AxialSeriesUid = "1.2.826.0.1.3680043.9.7133.4.2";
    private const string CoronalSeriesUid = "1.2.826.0.1.3680043.9.7133.4.3";

    private readonly DirectoryInfo root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hydro-" + Guid.NewGuid().ToString("N")));

    private CompositionRoot? composition;

    public void Dispose()
    {
        this.composition?.Dispose();

        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_ranked_series_is_the_one_analysed_on_opening()
    {
        // Предусловие остальных проверок: ранжирование берёт объёмную
        // аксиальную. Если бы оно брало корональную, всё дальнейшее
        // подтверждалось бы само собой и не проверяло ничего.
        var opened = await this.OpenAsync();

        Assert.Equal(ImagingPlane.Axial, opened.Analysed.Analysed.Geometry.Plane);
        Assert.NotNull(opened.Report.Series);
        Assert.Equal(
            opened.Analysed.Analysed.PseudonymousSeriesId,
            opened.Report.Series!.PseudonymousSeriesId);
    }

    [Fact]
    public async Task Switching_the_series_rebuilds_the_report_for_it()
    {
        await this.OpenAsync();

        var coronal = await this.ShowCoronalAsync();

        Assert.NotNull(coronal.Report.Series);
        Assert.Equal(ImagingPlane.Coronal, coronal.Report.Series!.Plane);
        Assert.Equal(
            coronal.Analysed.Analysed.PseudonymousSeriesId,
            coronal.Report.Series.PseudonymousSeriesId);
    }

    [Fact]
    public async Task An_angle_marked_after_the_switch_is_recorded_into_the_coronal_report()
    {
        await this.OpenAsync();

        var coronal = await this.ShowCoronalAsync();

        // Угол считается по тому самому объёму, который отдан на экран. Возьми
        // тест другой объём — разметка отказала бы названной причиной
        // (PlaneIsNotCoronal), и связь «что показано» с «куда записано»
        // осталась бы непроверенной.
        var measured = Angle(coronal.View.Volume);

        var recorded = await this.composition!
            .RecordMeasurementAsync(measured, CancellationToken.None);

        // Сверка с показанной серией, а не с серией отчёта: отчёт — как раз то,
        // что проверяется, и сравнение его с самим собой прошло бы и тогда,
        // когда он собран не по той серии.
        Assert.NotNull(recorded.Series);
        Assert.Equal(
            coronal.Analysed.Analysed.PseudonymousSeriesId,
            recorded.Series!.PseudonymousSeriesId);
        Assert.Equal(ImagingPlane.Coronal, recorded.Series.Plane);

        Assert.Contains(
            recorded.Biomarkers,
            item => string.Equals(
                item.Method.Code,
                LinearBiomarkers.ManualCallosalAngleCode,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_stored_angle_names_the_coronal_series_it_was_marked_on()
    {
        // Утверждение о диске, а не о возвращённом объекте: читать отчёты будет
        // сведение по набору, и ему достанется то, что записано.
        var opened = await this.OpenAsync();
        var coronal = await this.ShowCoronalAsync();

        await this.composition!
            .RecordMeasurementAsync(Angle(coronal.View.Volume), CancellationToken.None);

        var readout = await ManualMeasurementIndex.ReadAsync(
            ApplicationPaths.UnderRoot(this.root.FullName).ReportRoot,
            CancellationToken.None);

        var angle = Assert.Contains(opened.Report.PseudonymousStudyId, readout.CallosalAngle);

        Assert.NotNull(angle.Series);
        Assert.Equal(
            coronal.Analysed.Analysed.PseudonymousSeriesId,
            angle.Series!.Value.PseudonymousSeriesId);
        Assert.Equal(nameof(ImagingPlane.Coronal), angle.Series.Value.Plane);
    }

    [Fact]
    public async Task A_measurement_recorded_before_the_switch_stays_with_the_axial_series()
    {
        // Переключение серии не правит прежнюю версию отчёта, а оставляет её на
        // месте: индекс Эванса, записанный до переключения, остаётся измерением
        // аксиальной серии, а не переезжает к корональной.
        var opened = await this.OpenAsync();

        var index = await this.composition!
            .RecordMeasurementAsync(Index(opened.View.Volume), CancellationToken.None);

        // Сверка с показанной серией, а не с серией отчёта: отчёт здесь —
        // прежний, с дописанным измерением, и сравнение его с самим собой
        // прошло бы при любой серии.
        Assert.Equal(
            opened.Analysed.Analysed.PseudonymousSeriesId,
            index.Series!.PseudonymousSeriesId);
        Assert.Equal(ImagingPlane.Axial, index.Series.Plane);

        var coronal = await this.ShowCoronalAsync();

        Assert.DoesNotContain(
            coronal.Report.Biomarkers,
            item => string.Equals(
                item.Method.Code,
                LinearBiomarkers.ManualEvansIndexCode,
                StringComparison.Ordinal));

        // И на диске — тоже за аксиальной: переключение серии прежнюю версию
        // отчёта не правит.
        var readout = await ManualMeasurementIndex.ReadAsync(
            ApplicationPaths.UnderRoot(this.root.FullName).ReportRoot,
            CancellationToken.None);

        var stored = Assert.Contains(opened.Report.PseudonymousStudyId, readout.Manual);

        Assert.NotNull(stored.Series);
        Assert.Equal(
            opened.Analysed.Analysed.PseudonymousSeriesId,
            stored.Series!.Value.PseudonymousSeriesId);
        Assert.Equal(nameof(ImagingPlane.Axial), stored.Series.Value.Plane);
    }

    private async Task<OpenedStudy> OpenAsync()
    {
        var source = Directory.CreateDirectory(Path.Combine(this.root.FullName, "source"));

        // Объёмная аксиальная T1: тонкие срезы, расширенный уровень входа.
        WriteSeries(
            source.FullName,
            AxialSeriesUid,
            prefix: "ax",
            description: "T1 MPRAGE",
            acquisitionType: "3D",
            thickness: 1.0m,
            slices: 110,
            orientation: SyntheticStudyFiles.Axial);

        // Толстосрезовая корональная T2: то, на чём угол и меряют.
        WriteSeries(
            source.FullName,
            CoronalSeriesUid,
            prefix: "cor",
            description: "T2 cor",
            acquisitionType: "2D",
            thickness: 5.0m,
            slices: 24,
            orientation: SyntheticStudyFiles.Coronal);

        this.composition = await CompositionRoot.CreateAsync(
            ApplicationPaths.UnderRoot(this.root.FullName),
            CancellationToken.None,
            this.Settings());

        return await this.composition.OpenForViewingAsync(source.FullName, CancellationToken.None);
    }

    private async Task<OpenedStudy> ShowCoronalAsync()
    {
        var coronal = this.composition!.ScannedStudies
            .SelectMany(study => study.Series)
            .Single(series => series.Geometry.Plane == ImagingPlane.Coronal);

        return await this.composition.ShowSeriesAsync(
            coronal.PseudonymousSeriesId,
            CancellationToken.None);
    }

    /// <summary>
    /// Пишет файл роли во временный каталог настроек.
    ///
    /// Рядом со сборкой тестов его класть нельзя: файл там один на весь проект,
    /// и тест, который его создаёт, менял бы условия соседних.
    /// </summary>
    /// <returns>Каталог настроек установки.</returns>
    private string Settings()
    {
        var directory = Directory.CreateDirectory(Path.Combine(this.root.FullName, "settings"));

        File.WriteAllText(
            Path.Combine(directory.FullName, ActorSettings.FileName),
            "{\"role\":\"Clinician\"}");

        return directory.FullName;
    }

    private static void WriteSeries(
        string directory,
        string seriesUid,
        string prefix,
        string description,
        string acquisitionType,
        decimal thickness,
        int slices,
        decimal[] orientation)
    {
        for (var index = 0; index < slices; index++)
        {
            SyntheticStudyFiles.WriteSlice(
                Path.Combine(directory, $"{prefix}{index:D4}.dcm"),
                studyUid: StudyUid,
                seriesUid: seriesUid,
                patientId: "P-1",
                seriesDescription: description,
                acquisitionType: acquisitionType,
                sliceThickness: thickness,
                slicePosition: index * thickness,
                orientation: orientation,
                phantom: true,
                sliceIndex: index,
                sliceCount: slices);
        }
    }

    private static IReadOnlyList<Biomarker> Angle(IVoxelVolume volume)
    {
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(128, 140, 12));
        marking.Add(new VoxelPosition(108, 120, 12));
        marking.Add(new VoxelPosition(148, 120, 12));

        var result = marking.Measure(volume);

        Assert.Null(result.Refusal);

        return result.Measurements;
    }

    private static IReadOnlyList<Biomarker> Index(IVoxelVolume volume)
    {
        var marking = new ManualEvansMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(48, 128, 55));
        marking.Add(new VoxelPosition(208, 128, 55));
        marking.Add(new VoxelPosition(108, 128, 55));
        marking.Add(new VoxelPosition(148, 128, 55));

        var result = marking.Measure(volume);

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Biomarker);
        Assert.NotNull(result.Rotation);

        return [result.Biomarker!, result.Rotation!];
    }
}
