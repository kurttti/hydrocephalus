using System.Security.Cryptography;
using System.Text;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Шифрование рабочей копии и уничтожение сеанса (ADR 0006).
///
/// План проверки ADR: во время анализа в рабочем каталоге нет читаемых
/// DICOM-сигнатур, а на успехе, ошибке и отмене каталог остаётся пустым.
/// </summary>
public sealed class WorkingCopyProtectionTests : IDisposable
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Encryption_round_trips()
    {
        var key = RandomNumberGenerator.GetBytes(WorkingCopyCipher.KeyLengthBytes);
        var plaintext = Encoding.UTF8.GetBytes("DICM содержимое среза");

        var encrypted = WorkingCopyCipher.Encrypt(key, plaintext);

        Assert.Equal(plaintext, WorkingCopyCipher.Decrypt(key, encrypted));
    }

    [Fact]
    public void The_same_content_encrypts_differently_every_time()
    {
        // Одноразовый вектор случаен: повторный вектор при том же ключе
        // разрушает GCM и раскрывает разность открытых текстов.
        var key = RandomNumberGenerator.GetBytes(WorkingCopyCipher.KeyLengthBytes);
        var plaintext = Encoding.UTF8.GetBytes("одно и то же");

        Assert.NotEqual(
            WorkingCopyCipher.Encrypt(key, plaintext),
            WorkingCopyCipher.Encrypt(key, plaintext));
    }

    [Fact]
    public void A_tampered_block_is_refused_rather_than_decrypted()
    {
        // Отдать «расшифрованные как получилось» байты значило бы подать
        // на анализ то, чего в исходных данных не было.
        var key = RandomNumberGenerator.GetBytes(WorkingCopyCipher.KeyLengthBytes);
        var encrypted = WorkingCopyCipher.Encrypt(key, Encoding.UTF8.GetBytes("данные"));

        encrypted[^1] ^= 0xFF;

        // Именно несовпадение тега, а не любая ошибка шифрования:
        // тип исключения называет, что произошло с файлом.
        Assert.Throws<AuthenticationTagMismatchException>(
            () => WorkingCopyCipher.Decrypt(key, encrypted));
    }

    [Fact]
    public void Another_key_does_not_open_the_block()
    {
        var encrypted = WorkingCopyCipher.Encrypt(
            RandomNumberGenerator.GetBytes(WorkingCopyCipher.KeyLengthBytes),
            Encoding.UTF8.GetBytes("данные"));

        Assert.Throws<AuthenticationTagMismatchException>(() => WorkingCopyCipher.Decrypt(
            RandomNumberGenerator.GetBytes(WorkingCopyCipher.KeyLengthBytes),
            encrypted));
    }

    [Fact]
    public void A_truncated_block_is_refused()
    {
        var key = RandomNumberGenerator.GetBytes(WorkingCopyCipher.KeyLengthBytes);

        Assert.Throws<InvalidDataException>(
            () => WorkingCopyCipher.Decrypt(key, new byte[WorkingCopyCipher.OverheadBytes - 1]));
    }

    [Fact]
    public async Task Written_files_carry_no_readable_dicom_signature()
    {
        // Пункт плана проверки ADR 0006: сканирование рабочего каталога
        // не находит читаемых DICOM-сигнатур.
        using var session = await this.SessionAsync();

        await session.WriteAsync("series/a.dcm", DicomLikeContent(), CancellationToken.None);

        foreach (var path in Directory.EnumerateFiles(
            session.Directory,
            "*",
            SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);

            Assert.DoesNotContain(
                "DICM",
                Encoding.ASCII.GetString(bytes),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task What_was_written_can_be_read_back()
    {
        using var session = await this.SessionAsync();

        var content = DicomLikeContent();

        await session.WriteAsync("series/a.dcm", content, CancellationToken.None);

        Assert.Equal(content, await session.ReadAsync("series/a.dcm", CancellationToken.None));
    }

    [Fact]
    public async Task Files_are_listed_by_their_logical_paths()
    {
        using var session = await this.SessionAsync();

        await session.WriteAsync("series/b.dcm", DicomLikeContent(), CancellationToken.None);
        await session.WriteAsync("series/a.dcm", DicomLikeContent(), CancellationToken.None);

        Assert.Equal(
            [Path.Combine("series", "a.dcm"), Path.Combine("series", "b.dcm")],
            session.Enumerate("series"));
    }

    [Fact]
    public async Task Destroying_the_session_removes_the_key_and_the_data()
    {
        var session = await this.SessionAsync();

        await session.WriteAsync("series/a.dcm", DicomLikeContent(), CancellationToken.None);

        var directory = session.Directory;

        session.Destroy();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task A_destroyed_session_refuses_further_work()
    {
        var session = await this.SessionAsync();

        session.Destroy();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.WriteAsync("series/a.dcm", DicomLikeContent(), CancellationToken.None));
    }

    [Fact]
    public async Task A_path_leaving_the_session_is_refused()
    {
        // Файл, записанный вне сеанса, не удалит никто.
        using var session = await this.SessionAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.WriteAsync("../escape.dcm", DicomLikeContent(), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_session_is_swept()
    {
        using (await this.SessionAsync("old"))
        {
            // Сеанс закрывается сразу; каталог остаётся до уборки.
        }

        await this.SessionAsync("old-kept");

        var sweep = WorkingCopyRetention.Sweep(
            this.root.FullName,
            Moment.AddHours(25),
            new WorkingCopyRetentionPolicy { TimeToLive = TimeSpan.FromHours(24) });

        Assert.Equal(1, sweep.Removed);

        // Истёкший срок и уборка за прерванным сеансом — разные требования
        // ADR 0006, и в журнале это разные строки: по одному числу на двоих
        // нельзя ответить ни на один из двух вопросов.
        Assert.Equal(1, sweep.Expired);
        Assert.Equal(0, sweep.Orphaned);
    }

    [Fact]
    public async Task A_session_within_its_lifetime_is_kept()
    {
        await this.SessionAsync("fresh");

        var sweep = WorkingCopyRetention.Sweep(this.root.FullName, Moment.AddHours(1));

        Assert.Equal(0, sweep.Removed);
        Assert.Equal(1, sweep.Kept);
    }

    [Fact]
    public async Task An_active_session_is_never_swept()
    {
        // Уборка при старте не должна уносить работу, которая идёт.
        await this.SessionAsync("active");

        var sweep = WorkingCopyRetention.Sweep(
            this.root.FullName,
            Moment.AddHours(100),
            activeSessionIds: new HashSet<string>(StringComparer.Ordinal) { "active" });

        Assert.Equal(0, sweep.Removed);
    }

    [Fact]
    public void An_orphaned_directory_without_a_key_is_swept()
    {
        // Осталось от прерванного удаления или от сеанса, не дошедшего
        // до создания ключа: читать нечего, а место занимает.
        var orphan = Directory.CreateDirectory(Path.Combine(this.root.FullName, "orphan"));

        File.WriteAllBytes(Path.Combine(orphan.FullName, "a.dcm.enc"), [1, 2, 3]);

        var sweep = WorkingCopyRetention.Sweep(this.root.FullName, Moment);

        Assert.Equal(1, sweep.Removed);
        Assert.Equal(1, sweep.Orphaned);

        // Осиротевший каталог не истёк по сроку: его возраст неизвестен,
        // и назвать его просроченным значило бы отчитаться о работе политики
        // хранения там, где сработала уборка за сбоем.
        Assert.Equal(0, sweep.Expired);
        Assert.False(Directory.Exists(orphan.FullName));
    }

    [Fact]
    public async Task A_session_without_a_readable_stamp_is_swept()
    {
        // Возраст неизвестен. Оставить такой каталог значило бы хранить данные
        // бессрочно — ровно то, против чего политика и вводится.
        var session = await this.SessionAsync("undated");

        await File.WriteAllTextAsync(
            Path.Combine(session.Directory, WorkingCopySession.StampFileName),
            "не дата",
            CancellationToken.None);

        var sweep = WorkingCopyRetention.Sweep(this.root.FullName, Moment);

        Assert.Equal(1, sweep.Removed);
        Assert.Equal(1, sweep.Orphaned);
    }

    [Fact]
    public async Task A_directory_that_could_not_be_removed_is_not_counted_as_removed()
    {
        // Ключ к этому моменту уже удалён, поэтому содержимое нечитаемо,
        // но каталог остался на диске. Назвать его удалённым значило бы
        // записать в журнал неправду ровно о том, ради чего журнал ведётся.
        var session = await this.SessionAsync("locked");

        await using var held = new FileStream(
            Path.Combine(session.Directory, "held.bin"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var sweep = WorkingCopyRetention.Sweep(this.root.FullName, Moment.AddHours(25));

        Assert.Equal(0, sweep.Removed);
        Assert.Equal(1, sweep.Failed);
    }

    [Fact]
    public void Sweeping_a_missing_root_is_not_an_error()
    {
        var sweep = WorkingCopyRetention.Sweep(
            Path.Combine(this.root.FullName, "nothing-here"),
            Moment);

        Assert.Equal(0, sweep.Removed);
        Assert.Equal(0, sweep.Kept);
    }

    private static byte[] DicomLikeContent()
    {
        var content = new byte[512];

        Encoding.ASCII.GetBytes("DICM").CopyTo(content, 128);

        return content;
    }

    private Task<WorkingCopySession> SessionAsync(string sessionId = "session-1") =>
        WorkingCopySession.CreateAsync(
            this.root.FullName,
            sessionId,
            Moment,

            // ACL выключен: временный каталог теста живёт в общем расположении,
            // и ограничивать его учётной записью незачем.
            restrictAccess: false,
            CancellationToken.None);
}
