using Hydrocephalus.Infrastructure.Models;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Хранилище установленных пакетов модели.
///
/// ADR 0008 требует, чтобы установка нового пакета не удаляла предыдущий, а
/// откат был переключением метки — без переустановки файла и без сети. Причина
/// клиническая: смена версии модели меняет результат для одного и того же
/// пациента, и возврат назад не должен зависеть от того, сохранил ли
/// администратор старый файл.
/// </summary>
public sealed class InstalledModelStoreTests : IDisposable
{
    private readonly DirectoryInfo root =
        Directory.CreateTempSubdirectory("hydrocephalus-models-");

    [Fact]
    public void Nothing_is_installed_in_an_empty_store()
    {
        // Рядовое состояние, а не ошибка: приложение поставляется без модели,
        // установщик её не приносит (ADR 0008).
        var store = new InstalledModelStore(Path.Combine(this.root.FullName, "models"));

        Assert.Empty(store.InstalledVersions());
        Assert.Null(store.ActiveVersion());
        Assert.Null(store.ActivePackagePath());
    }

    [Fact]
    public void An_installed_package_is_not_active_until_it_is_activated()
    {
        // Установка и активация — разные действия: ADR 0008 запрещает
        // переключение версии во время выполняющегося анализа.
        var store = this.Store();

        store.Install(this.Package("one"), "vinn-axial-2.0.0");

        Assert.Equal(["vinn-axial-2.0.0"], store.InstalledVersions());
        Assert.Null(store.ActiveVersion());
    }

    [Fact]
    public void Installing_a_second_version_keeps_the_first()
    {
        var store = this.Store();

        store.Install(this.Package("one"), "vinn-axial-2.0.0");
        store.Install(this.Package("two"), "vinn-axial-2.1.0");

        Assert.Equal(["vinn-axial-2.0.0", "vinn-axial-2.1.0"], store.InstalledVersions());
    }

    [Fact]
    public void Rolling_back_is_switching_the_marker()
    {
        var store = this.Store();

        store.Install(this.Package("one"), "vinn-axial-2.0.0");
        store.Install(this.Package("two"), "vinn-axial-2.1.0");
        store.Activate("vinn-axial-2.1.0");

        Assert.Equal("vinn-axial-2.1.0", store.ActiveVersion());

        store.Activate("vinn-axial-2.0.0");

        Assert.Equal("vinn-axial-2.0.0", store.ActiveVersion());

        // Оба файла на месте: откат не переустанавливал ничего.
        Assert.Equal(["vinn-axial-2.0.0", "vinn-axial-2.1.0"], store.InstalledVersions());
    }

    [Fact]
    public void A_version_that_is_not_installed_cannot_be_activated()
    {
        var store = this.Store();

        Assert.Throws<FileNotFoundException>(() => store.Activate("vinn-axial-9.9.9"));
    }

    [Fact]
    public void A_marker_pointing_at_a_missing_file_means_nothing_is_active()
    {
        // Иначе приложение называло бы версию, которой не может загрузить.
        var store = this.Store();

        store.Install(this.Package("one"), "vinn-axial-2.0.0");
        store.Activate("vinn-axial-2.0.0");

        File.Delete(Path.Combine(this.root.FullName, "vinn-axial-2.0.0.hcmp"));

        Assert.Null(store.ActiveVersion());
        Assert.Null(store.ActivePackagePath());
    }

    [Fact]
    public void A_marker_written_with_a_byte_order_mark_still_counts()
    {
        // Метку ставят редакторы и оболочки Windows, а разбор JSON считает её
        // мусором и отвергает файл целиком. Отказ выглядел бы как «модели нет»,
        // и приложение молча считало бы пороговым путём. На этом я и
        // попался, готовя прогон.
        var store = this.Store();

        store.Install(this.Package("one"), "vinn-axial-2.0.0");
        File.WriteAllBytes(
            Path.Combine(this.root.FullName, InstalledModelStore.ActiveMarkerName),
            [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "{\"modelVersion\":\"vinn-axial-2.0.0\"}"u8]);

        Assert.Equal("vinn-axial-2.0.0", store.ActiveVersion());
    }

    [Fact]
    public void A_broken_marker_means_nothing_is_active()
    {
        var store = this.Store();

        store.Install(this.Package("one"), "vinn-axial-2.0.0");
        File.WriteAllText(
            Path.Combine(this.root.FullName, InstalledModelStore.ActiveMarkerName),
            "это не json");

        Assert.Null(store.ActiveVersion());
    }

    [Fact]
    public void Nothing_is_written_over_an_installed_version()
    {
        // Две разные сборки под одним номером — молчаливая смена
        // измерительного инструмента: отчёт, уже сославшийся на эту версию,
        // стал бы ссылаться не на то, чем получен. Новая сборка приходит
        // с новым номером; переустановки поверх нет намеренно.
        var store = this.Store();

        store.Install(this.Package("one"), "vinn-axial-2.0.0");

        Assert.Throws<IOException>(
            () => store.Install(this.Package("two"), "vinn-axial-2.0.0"));

        Assert.Equal("one", File.ReadAllText(
            Path.Combine(this.root.FullName, "vinn-axial-2.0.0.hcmp")));
    }

    [Fact]
    public void A_version_cannot_write_outside_the_store()
    {
        // Версия приходит из объявления пакета, то есть из файла. Вида `../..`
        // она вывела бы запись за пределы хранилища.
        var store = this.Store();

        store.Install(this.Package("one"), Path.Combine("..", "escaped"));

        Assert.Equal(["escaped"], store.InstalledVersions());
        Assert.False(File.Exists(
            Path.Combine(this.root.Parent!.FullName, "escaped.hcmp")));
    }

    public void Dispose() => this.root.Delete(recursive: true);

    private InstalledModelStore Store() => new(this.root.FullName);

    private string Package(string name)
    {
        // Содержимое неважно: хранилище пакеты не проверяет и весов не читает,
        // это дело SignedModelPackageReader.
        var path = Path.Combine(this.root.FullName, $"source-{name}.bin");

        File.WriteAllText(path, name);

        return path;
    }
}
