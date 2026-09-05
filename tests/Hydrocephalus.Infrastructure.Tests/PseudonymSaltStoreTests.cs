using System.Text;
using Hydrocephalus.Infrastructure.Configuration;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Хранилище соли псевдонимизации.
///
/// Соль создаётся на установку: общая для всех установок сделала бы псевдонимы
/// предсказуемыми, то есть восстановимыми по словарю исходных идентификаторов.
/// На диске она лежит защищённой, иначе замена UID теряет смысл (ADR 0006).
/// </summary>
public sealed class PseudonymSaltStoreTests : IDisposable
{
    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_salt_is_stable_across_calls()
    {
        // Разная соль между запусками разорвала бы соответствие псевдонимов:
        // один пациент разошёлся бы на двух.
        var path = this.PathFor("salt.bin");

        var first = await PseudonymSaltStore.GetOrCreateAsync(path, CancellationToken.None);
        var again = await PseudonymSaltStore.GetOrCreateAsync(path, CancellationToken.None);

        Assert.Equal(first, again);
    }

    [Fact]
    public async Task Separate_installations_get_separate_salts()
    {
        var first = await PseudonymSaltStore.GetOrCreateAsync(this.PathFor("a.bin"), CancellationToken.None);
        var second = await PseudonymSaltStore.GetOrCreateAsync(this.PathFor("b.bin"), CancellationToken.None);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task The_salt_is_not_stored_in_the_clear()
    {
        var path = this.PathFor("salt.bin");

        var salt = await PseudonymSaltStore.GetOrCreateAsync(path, CancellationToken.None);

        var stored = await File.ReadAllBytesAsync(path, CancellationToken.None);

        Assert.DoesNotContain(
            Encoding.UTF8.GetString(stored),
            salt,
            StringComparison.Ordinal);

        Assert.False(
            Contains(stored, Convert.FromBase64String(salt)),
            "The raw salt bytes were found in the stored file.");
    }

    [Fact]
    public async Task The_salt_has_the_declared_length()
    {
        var salt = await PseudonymSaltStore.GetOrCreateAsync(this.PathFor("salt.bin"), CancellationToken.None);

        Assert.Equal(PseudonymSaltStore.SaltLengthBytes, Convert.FromBase64String(salt).Length);
    }

    [Fact]
    public async Task A_corrupted_file_is_reported_rather_than_replaced()
    {
        // Молча создать новую соль вместо повреждённой значило бы тихо разорвать
        // соответствие псевдонимов вместо того, чтобы сообщить о проблеме.
        var path = this.PathFor("salt.bin");

        await PseudonymSaltStore.GetOrCreateAsync(path, CancellationToken.None);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(
            () => PseudonymSaltStore.GetOrCreateAsync(path, CancellationToken.None));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var offset = 0; offset + needle.Length <= haystack.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private string PathFor(string name) => Path.Combine(this.root.FullName, "secrets", name);
}
