using System.Globalization;
using System.IO;
using Hydrocephalus.Desktop.Composition;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Срок жизни рабочей копии, заданный при установке.
///
/// Проверяется не разбор JSON, а то, что настройка не может незаметно
/// превратиться во что-то другое: непригодное значение откатывается к сроку
/// по умолчанию, а не принимается, и запуск приложения от испорченного файла
/// не зависит.
/// </summary>
public sealed class RetentionSettingsTests : IDisposable
{
    private readonly DirectoryInfo directory =
        Directory.CreateTempSubdirectory("hydrocephalus-retention-");

    [Fact]
    public void An_installation_that_says_nothing_keeps_the_default_of_a_day()
    {
        // Отсутствие файла — обычное состояние: срок задаёт тот, кто
        // разворачивает приложение, и он вправе оставить значение ADR.
        Assert.Equal(TimeSpan.FromHours(24), RetentionSettings.Read(this.directory.FullName).TimeToLive);
    }

    [Fact]
    public void The_administrator_can_set_a_shorter_period()
    {
        this.Write("{\"timeToLiveHours\": 4}");

        Assert.Equal(TimeSpan.FromHours(4), RetentionSettings.Read(this.directory.FullName).TimeToLive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_period_that_would_delete_the_copy_at_once_is_not_a_policy(double hours)
    {
        // Нулевой и отрицательный срок означают удаление рабочей копии сразу
        // после создания, то есть неработающее приложение. Это ошибка
        // настройки, и принимать её нельзя.
        this.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"timeToLiveHours\": {hours}}}"));

        Assert.Equal(TimeSpan.FromHours(24), RetentionSettings.Read(this.directory.FullName).TimeToLive);
    }

    [Fact]
    public void A_period_long_enough_to_be_no_policy_at_all_is_refused()
    {
        // Опечатка в порядке величины не должна превращать политику хранения
        // в её отсутствие: расшифрованные данные не живут на рабочей станции
        // неделями.
        this.Write("{\"timeToLiveHours\": 10000}");

        Assert.Equal(TimeSpan.FromHours(24), RetentionSettings.Read(this.directory.FullName).TimeToLive);
    }

    [Fact]
    public void A_broken_settings_file_does_not_stop_the_application()
    {
        // Отказ запускаться из-за испорченного файла настроек хуже, чем срок
        // по умолчанию: врач остаётся без инструмента там, где достаточно было
        // взять известное значение.
        this.Write("{ это не json");

        Assert.Equal(TimeSpan.FromHours(24), RetentionSettings.Read(this.directory.FullName).TimeToLive);
    }

    [Fact]
    public void The_period_in_force_is_stated_in_words_a_clinician_reads()
    {
        // ADR 0006 называет риском молчаливое удаление незавершённого разбора
        // случая. Срок, о котором узнают в момент пропажи данных, требованию
        // «заранее видимо» не отвечает.
        this.Write("{\"timeToLiveHours\": 4}");

        var text = RetentionSettings.Describe(RetentionSettings.Read(this.directory.FullName));

        Assert.Contains("4", text, StringComparison.Ordinal);
        Assert.Contains("удаляется", text, StringComparison.Ordinal);
    }

    /// <summary>Удаляет временный каталог.</summary>
    public void Dispose()
    {
        this.directory.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string content) =>
        File.WriteAllText(
            Path.Combine(this.directory.FullName, RetentionSettings.FileName),
            content);
}
