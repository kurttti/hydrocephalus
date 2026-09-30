using System.Text.Json;
using Hydrocephalus.Infrastructure.Dataset;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Рабочий список измерений и чтение того, что уже измерено.
///
/// Здесь решается, предложит ли приложение исследование снова. Ошибка не падает
/// и не отказывает: врач либо мерит одно и то же дважды, либо не домеривает
/// выборку и узнаёт об этом при сведении — то есть после того, как отработал.
/// </summary>
public sealed class MeasurementWorklistTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "hydro-worklist-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_measurement_left_only_in_an_older_version_still_counts()
    {
        // Повторное открытие исследования заново запускает анализ и сохраняет
        // отчёт без ручной отметки. Искать её в последней версии значило бы
        // предлагать измерять заново то, что уже измерено.
        this.WriteReport("s1", "2026-09-30T10:00:00.0000000Z", evans: 0.25);
        this.WriteReport("s1", "2026-09-30T11:00:00.0000000Z", evans: null);

        var readout = await ManualMeasurementIndex.ReadAsync(this.Reports, CancellationToken.None);

        Assert.Equal(0.25, Assert.Contains("s1", readout.Manual).EvansIndex, precision: 9);
    }

    [Fact]
    public async Task The_latest_of_several_measurements_wins()
    {
        this.WriteReport("s1", "2026-09-30T10:00:00.0000000Z", evans: 0.25);
        this.WriteReport("s1", "2026-09-30T12:00:00.0000000Z", evans: 0.31);
        this.WriteReport("s1", "2026-09-30T11:00:00.0000000Z", evans: 0.28);

        var readout = await ManualMeasurementIndex.ReadAsync(this.Reports, CancellationToken.None);

        Assert.Equal(0.31, Assert.Contains("s1", readout.Manual).EvansIndex, precision: 9);
    }

    [Fact]
    public async Task An_implausible_value_is_not_a_measurement()
    {
        // Записать такое сейчас нельзя, но отчёты прежних версий остаются на
        // диске навсегда, и принять их за измерение значило бы не домерить.
        this.WriteReport("s1", "2026-09-30T10:00:00.0000000Z", evans: 3.02, outOfRange: true);

        var readout = await ManualMeasurementIndex.ReadAsync(this.Reports, CancellationToken.None);

        Assert.Empty(readout.Manual);
    }

    [Fact]
    public async Task An_unreadable_report_is_counted_rather_than_passed_over()
    {
        Directory.CreateDirectory(Path.Combine(this.Reports, "s1"));
        await File.WriteAllTextAsync(Path.Combine(this.Reports, "s1", "broken.json"), "{не json");

        this.WriteReport("s2", "2026-09-30T10:00:00.0000000Z", evans: 0.25);

        var readout = await ManualMeasurementIndex.ReadAsync(this.Reports, CancellationToken.None);

        Assert.Single(readout.Manual);
        Assert.Equal(1, readout.UnreadableReports);
    }

    [Fact]
    public async Task No_report_store_means_nothing_has_been_measured()
    {
        var readout = await ManualMeasurementIndex.ReadAsync(
            Path.Combine(this.root.FullName, "нет-такого"), CancellationToken.None);

        Assert.Empty(readout.Manual);
        Assert.Empty(readout.Automatic);
    }

    [Fact]
    public async Task A_measurement_made_from_the_dataset_counts_for_the_same_study()
    {
        // Отчёт по измерению из набора лежит под производным псевдонимом, а не
        // под исходным: разбор копии даёт другой. Не связав их, список предложил
        // бы измерять это исследование бесконечно.
        await this.WriteManifestAsync(("A", "subject-1", "s1", "derived-1"));
        this.WriteReport("derived-1", "2026-09-30T10:00:00.0000000Z", evans: 0.27);

        var worklist = await this.OpenAsync();

        Assert.True(Assert.Single(worklist!.Studies).IsMeasured);
        Assert.Null(worklist.NextUnmeasured(afterDirectory: null));
    }

    [Fact]
    public async Task The_next_study_is_the_first_unmeasured_one()
    {
        await this.WriteManifestAsync(
            ("A", "subject-1", "s1", "d1"),
            ("A", "subject-2", "s2", "d2"),
            ("Б", "subject-3", "s3", "d3"));

        this.WriteReport("s1", "2026-09-30T10:00:00.0000000Z", evans: 0.25);

        var worklist = await this.OpenAsync();

        Assert.Equal(3, worklist!.Studies.Count);
        Assert.Equal(1, worklist.MeasuredCount);
        Assert.Equal("s2", worklist.NextUnmeasured(afterDirectory: null)!.Value.Study.PseudonymousStudyId);
    }

    [Fact]
    public async Task The_open_study_is_passed_over_even_when_it_is_unmeasured()
    {
        // Иначе «следующее» возвращало бы то же самое, пока врач не запишет
        // измерение, — и кнопка выглядела бы сломанной.
        await this.WriteManifestAsync(
            ("A", "subject-1", "s1", "d1"),
            ("A", "subject-2", "s2", "d2"));

        var worklist = await this.OpenAsync();
        var first = worklist!.Studies[0];

        Assert.Equal(
            "s2",
            worklist.NextUnmeasured(first.Directory)!.Value.Study.PseudonymousStudyId);
    }

    [Fact]
    public async Task The_search_wraps_around_to_what_was_skipped()
    {
        // Врач мог пропустить исследование и уйти дальше. Дойдя до конца, список
        // обязан вернуться к пропущенному, а не объявить работу законченной.
        await this.WriteManifestAsync(
            ("A", "subject-1", "s1", "d1"),
            ("A", "subject-2", "s2", "d2"));

        this.WriteReport("s2", "2026-09-30T10:00:00.0000000Z", evans: 0.25);

        var worklist = await this.OpenAsync();
        var second = worklist!.Studies[1];

        Assert.Equal(
            "s1",
            worklist.NextUnmeasured(second.Directory)!.Value.Study.PseudonymousStudyId);
    }

    [Fact]
    public async Task A_group_can_be_worked_through_on_its_own()
    {
        await this.WriteManifestAsync(
            ("A", "subject-1", "s1", "d1"),
            ("Б", "subject-2", "s2", "d2"));

        var worklist = await this.OpenAsync(group: "Б");

        Assert.Equal("s2", Assert.Single(worklist!.Studies).Study.PseudonymousStudyId);
    }

    [Fact]
    public async Task Nothing_left_to_measure_is_said_rather_than_guessed()
    {
        await this.WriteManifestAsync(("A", "subject-1", "s1", "d1"));
        this.WriteReport("s1", "2026-09-30T10:00:00.0000000Z", evans: 0.25);

        var worklist = await this.OpenAsync();

        Assert.Null(worklist!.NextUnmeasured(afterDirectory: null));
    }

    private string Reports => Path.Combine(this.root.FullName, "reports");

    private string Dataset => Path.Combine(this.root.FullName, "dataset");

    private Task<MeasurementWorklist?> OpenAsync(string? group = null) =>
        MeasurementWorklist.OpenAsync(this.Dataset, this.Reports, group, CancellationToken.None);

    private async Task WriteManifestAsync(
        params (string Group, string Subject, string Study, string? Derived)[] studies)
    {
        var manifest = new DatasetManifest
        {
            BuiltAt = DateTimeOffset.UnixEpoch,
            BuiltBy = "test",
            Studies = [.. studies.Select(study => new DatasetStudy
            {
                Group = study.Group,
                PseudonymousSubjectId = study.Subject,
                PseudonymousStudyId = study.Study,
                DerivedSubjectId = study.Derived,
                DerivedStudyId = study.Derived,
                Series = [],
                ExcludedSeries = 0,
                Files = [],
            })],
        };

        await manifest.WriteAsync(this.Dataset, CancellationToken.None);
    }

    private void WriteReport(string studyId, string moment, double? evans, bool outOfRange = false)
    {
        var directory = Path.Combine(this.Reports, studyId);

        Directory.CreateDirectory(directory);

        var biomarkers = evans is null
            ? "[]"
            : JsonSerializer.Serialize(new[]
            {
                new
                {
                    code = ManualMeasurementIndex.ManualEvansCode,
                    value = evans.Value,
                    outOfRange,
                },
            });

        File.WriteAllText(
            Path.Combine(directory, moment.Replace(":", string.Empty, StringComparison.Ordinal) + ".json"),
            $$"""{"createdAt":"{{moment}}","biomarkers":{{biomarkers}}}""");
    }
}
