using Hydrocephalus.Desktop.Administration;
using Hydrocephalus.Desktop.Results;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Экран администрирования: журнал аудита и состояние установки.
///
/// Проверяется не вёрстка, а три утверждения, без которых экран вреден.
///
/// Первое: о целостности цепочки сказано прямо. Журнал, показанный без
/// проверки, выдаёт подделанный за настоящий — ровно то, от чего цепочка
/// защищает.
///
/// Второе: у целостности названа её граница. «Цепочка цела» не означает
/// «журнал полон»: обрезанный хвост оставляет остаток корректным, и без
/// внешнего якоря это не обнаруживается.
///
/// Третье: непроверенное не выдаётся за целое. Записи после разрыва отличаются
/// на экране и от проверенных, и от изменённой.
/// </summary>
public sealed class AdministrationReadoutTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_intact_chain_is_stated_as_such()
    {
        var rows = Section(Describe(Journal(Verified(1), Verified(2))), "Целостность журнала");

        Assert.Contains(rows, row => row.Text.Contains("сходится", StringComparison.Ordinal));
    }

    [Fact]
    public void An_intact_chain_still_names_what_it_does_not_cover()
    {
        // Обрезанный хвост оставляет цепочку корректной. Экран, говорящий
        // «цепочка цела» и молчащий об этом, делает заявление, которого
        // проверка не делает.
        var rows = Section(Describe(Journal(Verified(1))), "Целостность журнала");

        var anchor = Assert.Single(rows, row => row.Text.Contains("Хеш последней проверенной", StringComparison.Ordinal));

        Assert.Contains("aaaa", anchor.Text, StringComparison.Ordinal);
        Assert.Contains("вне журнала", anchor.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_chain_names_the_record_and_blocks()
    {
        var journal = new AuditJournal
        {
            Records = [Verified(1), Broken(2), Unverifiable(3)],
            IsIntact = false,
            LatestHash = new string('a', 64),
            TailIsIncomplete = false,
        };

        var row = Assert.Single(
            Section(Describe(journal), "Целостность журнала"),
            item => item.Text.Contains("разошлась", StringComparison.Ordinal));

        Assert.Contains("№2", row.Text, StringComparison.Ordinal);
        Assert.Equal(ResultSeverity.Blocking, row.Severity);
    }

    [Fact]
    public void What_follows_a_break_is_marked_unverifiable_rather_than_altered()
    {
        // «Запись изменена» и «об этой записи сказать нечего» — разные
        // утверждения, и одинаковый вид обвинил бы в подделке то,
        // чего никто не трогал.
        var journal = new AuditJournal
        {
            Records = [Verified(1), Broken(2), Unverifiable(3)],
            IsIntact = false,
            LatestHash = new string('a', 64),
            TailIsIncomplete = false,
        };

        var events = Section(Describe(journal), "События");

        var broken = Assert.Single(events, row => row.Text.Contains("№2", StringComparison.Ordinal));
        var beyond = Assert.Single(events, row => row.Text.Contains("№3", StringComparison.Ordinal));

        Assert.Equal(ResultSeverity.Blocking, broken.Severity);
        Assert.Equal(ResultSeverity.Warning, beyond.Severity);
        Assert.NotEqual(broken.Note, beyond.Note);
    }

    [Fact]
    public void An_unreadable_record_has_no_doubled_space()
    {
        var journal = Journal(new AuditRecord
        {
            Number = 1,
            Integrity = AuditRecordIntegrity.Broken,
            IsReadable = false,
        });

        var row = Assert.Single(Section(Describe(journal), "События"));

        Assert.DoesNotContain("  ", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_record_stays_visible()
    {
        // Пропустить нечитаемую запись значило бы скрыть то самое место,
        // где что-то не так.
        var journal = Journal(new AuditRecord
        {
            Number = 1,
            Integrity = AuditRecordIntegrity.Broken,
            IsReadable = false,
        });

        var row = Assert.Single(Section(Describe(journal), "События"));

        Assert.Contains("не читается", row.Text, StringComparison.Ordinal);
        Assert.Equal(ResultSeverity.Blocking, row.Severity);
    }

    [Fact]
    public void The_most_recent_event_comes_first()
    {
        // Журнал растёт всю жизнь установки, и то, что ищут, почти всегда
        // произошло недавно.
        var events = Section(Describe(Journal(Verified(1), Verified(2), Verified(3))), "События");

        Assert.Contains("№3", events[0].Text, StringComparison.Ordinal);
        Assert.Contains("№1", events[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_beyond_the_limit_are_counted_rather_than_dropped()
    {
        var many = Enumerable
            .Range(1, AdministrationReadout.MaxShownRecords + 3)
            .Select(Verified)
            .ToArray();

        var events = Section(Describe(Journal(many)), "События");

        var notice = events[0];

        Assert.Contains(
            AdministrationReadout.MaxShownRecords.ToString(System.Globalization.CultureInfo.CurrentCulture),
            notice.Text,
            StringComparison.Ordinal);

        Assert.Contains(
            many.Length.ToString(System.Globalization.CultureInfo.CurrentCulture),
            notice.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_sweep_shows_what_it_failed_to_delete()
    {
        // «Данные пациента должны были исчезнуть и не исчезли» — ровно то,
        // ради чего журнал ведётся.
        var journal = Journal(Verified(1) with
        {
            Code = AuditEventCode.WorkingCopiesSwept,
            PseudonymousStudyId = null,
            Retention = new WorkingCopyRetentionOutcome(2, 1, 3, 24.0),
        });

        var row = Assert.Single(Section(Describe(journal), "События"));

        Assert.Contains("не удалось удалить 3", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sweep_that_failed_nothing_does_not_mention_failures()
    {
        var journal = Journal(Verified(1) with
        {
            Code = AuditEventCode.WorkingCopiesSwept,
            PseudonymousStudyId = null,
            Retention = new WorkingCopyRetentionOutcome(1, 0, 0, 24.0),
        });

        var row = Assert.Single(Section(Describe(journal), "События"));

        Assert.DoesNotContain("не удалось", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_export_names_its_variant_in_words()
    {
        // «Отчёт ушёл» без указания варианта не отвечает на главный вопрос —
        // ушли ли наружу идентификаторы пациента.
        var journal = Journal(Verified(1) with
        {
            Code = AuditEventCode.ReportExported,
            ReportExportVariant = ReportExportVariant.Clinical,
            PseudonymousActorId = "actor-42",
        });

        var row = Assert.Single(Section(Describe(journal), "События"));

        Assert.Contains("клинический", row.Text, StringComparison.Ordinal);
        Assert.Contains("actor-42", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ReportExportVariant.Clinical), row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_journal_says_so()
    {
        // «Событий не было» и «журнал не прочитан» выглядели бы одинаково
        // при пустом списке, а читаются по-разному.
        var row = Assert.Single(Section(Describe(Journal()), "События"));

        Assert.Contains("Событий в журнале нет", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_torn_tail_is_not_called_tampering()
    {
        var journal = Journal(Verified(1)) with { TailIsIncomplete = true };

        var row = Assert.Single(
            Section(Describe(journal), "Целостность журнала"),
            item => item.Text.Contains("не завершена", StringComparison.Ordinal));

        Assert.Equal(ResultSeverity.Neutral, row.Severity);
    }

    [Fact]
    public void Where_the_files_live_is_shown()
    {
        // Обслуживание установки — это работа с её файлами; на экране
        // расположение, а не содержимое.
        var rows = Section(Describe(Journal()), "Хранение");

        Assert.Contains(rows, row => row.Text.Contains("working-copies", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("audit.log", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unconfigured_registry_is_named_as_a_reservation()
    {
        var rows = Section(Describe(Journal()), "Установка");

        var row = Assert.Single(rows, item => item.Text.Contains("Реестр", StringComparison.Ordinal));

        Assert.Equal(ResultSeverity.Warning, row.Severity);
        Assert.Contains("не подключён", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unimplemented_pipeline_stages_are_named_rather_than_versioned()
    {
        // Номер версии в поле нереализованного этапа — заявка
        // на воспроизводимость, которой не существует.
        var rows = Section(Describe(Journal()), "Установка");

        var row = Assert.Single(rows, item => item.Text.Contains("Сборка приложения", StringComparison.Ordinal));

        Assert.Contains("предобработка", row.Note ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("label map", row.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void After_a_break_the_anchor_hash_is_not_called_the_end_of_the_file()
    {
        // При разрыве последняя проверенная запись и последняя запись файла —
        // разные записи, и именно тогда хеш сравнивают с якорем.
        var journal = new AuditJournal
        {
            Records = [Verified(1), Broken(2)],
            IsIntact = false,
            LatestHash = new string('a', 64),
            TailIsIncomplete = false,
        };

        var anchor = Assert.Single(
            Section(Describe(journal), "Целостность журнала"),
            row => row.Text.Contains("Хеш последней проверенной", StringComparison.Ordinal));

        Assert.Contains("до разрыва", anchor.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_under_the_profile_do_not_carry_the_account_name()
    {
        // Каталоги приложения лежат в профиле пользователя, а имя учётной записи
        // в клинике обычно образовано от фамилии сотрудника. На этом экране оно
        // стояло бы в трёх строках от псевдонимов инициаторов.
        var paths = Composition.ApplicationPaths.UnderLocalApplicationData();

        var sections = AdministrationReadout.Describe(Snapshot(Journal()) with
        {
            AuditLogPath = paths.AuditLogPath,
            WorkingCopyRoot = paths.WorkingCopyRoot,
            ReportRoot = paths.ReportRoot,
            ReportExportRoot = paths.ReportExportRoot,
            DatasetManifestRoot = paths.DatasetManifestRoot,
        });

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var row in sections.SelectMany(section => section.Rows))
        {
            Assert.DoesNotContain(profile, row.Text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            Section(sections, "Хранение"),
            row => row.Text.Contains("%LOCALAPPDATA%", StringComparison.Ordinal));
    }

    [Fact]
    public void A_named_code_unknown_to_this_version_keeps_its_name()
    {
        // Более новая версия пишет код, которого эта не знает. Разбор даёт
        // Unspecified, и без исходного имени запись стала бы «событием
        // неизвестного кода» без указания какого.
        var journal = Journal(Verified(1) with
        {
            Code = AuditEventCode.Unspecified,
            CodeName = "ModelPackageLoaded",
        });

        var row = Assert.Single(Section(Describe(journal), "События"));

        Assert.Contains("Событие ModelPackageLoaded", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_unknown_to_this_version_is_not_shown_as_a_known_one()
    {
        // Запись, сделанная более новой версией приложения, должна остаться
        // видимой, а не притвориться чем-то знакомым.
        var journal = Journal(Verified(1) with { Code = (AuditEventCode)97 });

        var row = Assert.Single(Section(Describe(journal), "События"));

        Assert.Contains("Событие 97", row.Text, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ResultRow> Section(IReadOnlyList<ResultSection> sections, string title) =>
        sections.Single(section => string.Equals(section.Title, title, StringComparison.Ordinal)).Rows;

    private static IReadOnlyList<ResultSection> Describe(AuditJournal journal) =>
        AdministrationReadout.Describe(Snapshot(journal));

    private static AdministrationSnapshot Snapshot(AuditJournal journal) =>
        new()
        {
            Journal = journal,
            AuditLogPath = @"C:\data\audit\audit.log",
            WorkingCopyRoot = @"C:\data\working-copies",
            ReportRoot = @"C:\data\reports",
            ReportExportRoot = @"C:\data\report-exports",
            DatasetManifestRoot = @"C:\data\dataset-manifests",
            Retention = new WorkingCopyRetentionPolicy(),
            PatientRegistryConfigured = false,
            Pipeline = new PipelineIdentity
            {
                PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
                FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,
                LabelMapVersion = PipelineIdentity.NotImplementedVersion,
                ApplicationCommitSha = "0123456789abcdef",
            },
        };

    private static AuditJournal Journal(params AuditRecord[] records) => new()
    {
        Records = records,
        IsIntact = true,
        LatestHash = new string('a', 64),
        TailIsIncomplete = false,
    };

    private static AuditRecord Verified(int number) => new()
    {
        Number = number,
        Integrity = AuditRecordIntegrity.Verified,
        IsReadable = true,
        Code = AuditEventCode.StudyImported,
        OccurredAt = Moment.AddMinutes(number),
        PseudonymousStudyId = "study-0001",
    };

    private static AuditRecord Broken(int number) =>
        Verified(number) with { Integrity = AuditRecordIntegrity.Broken };

    private static AuditRecord Unverifiable(int number) =>
        Verified(number) with { Integrity = AuditRecordIntegrity.Unverifiable };
}
