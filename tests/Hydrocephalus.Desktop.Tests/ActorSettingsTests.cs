using System.IO;
using System.Text.Json;
using Hydrocephalus.Desktop.Composition;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Роль, заданная при установке.
///
/// Каждый способ не прочитать роль обязан давать отсутствие прав. Обратное
/// умолчание здесь — не неудобство, а раздача доступа там, где никто ничего
/// не решал, и заметить это по работающему приложению нельзя.
/// </summary>
public sealed class ActorSettingsTests : IDisposable
{
    private readonly DirectoryInfo root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hydro-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("Clinician", ClinicalRole.Clinician)]
    [InlineData("Researcher", ClinicalRole.Researcher)]
    [InlineData("Administrator", ClinicalRole.Administrator)]
    public void A_declared_role_is_read(string declared, ClinicalRole expected)
    {
        Assert.Equal(expected, ActorSettings.ReadRole(this.Write(new { role = declared })));
    }

    [Fact]
    public void A_missing_settings_file_grants_nothing()
    {
        // «Нет файла, значит врач» раздавало бы доступ по недосмотру установки.
        Assert.Equal(
            ClinicalRole.Unspecified,
            ActorSettings.ReadRole(Path.Combine(this.root.FullName, ActorSettings.FileName)));
    }

    [Fact]
    public void A_corrupted_settings_file_grants_nothing()
    {
        var path = Path.Combine(this.root.FullName, ActorSettings.FileName);

        File.WriteAllText(path, "{ это не json");

        Assert.Equal(ClinicalRole.Unspecified, ActorSettings.ReadRole(path));
    }

    [Fact]
    public void A_file_without_a_role_grants_nothing()
    {
        Assert.Equal(ClinicalRole.Unspecified, ActorSettings.ReadRole(this.Write(new { other = "Clinician" })));
    }

    [Theory]
    [InlineData("clinician")]
    [InlineData("CLINICIAN")]
    [InlineData("Доктор")]
    [InlineData("")]
    public void An_unrecognised_role_grants_nothing(string declared)
    {
        // Разбор строгий по регистру: молчаливое приравнивание однажды примет
        // опечатку за роль.
        Assert.Equal(ClinicalRole.Unspecified, ActorSettings.ReadRole(this.Write(new { role = declared })));
    }

    [Fact]
    public void The_user_identifier_is_a_pseudonym_and_not_the_account_name()
    {
        // Имя учётной записи в клинике обычно образовано от фамилии, а журнал
        // не должен нести персональных данных ни о пациенте, ни о сотруднике.
        var identifier = ActorSettings.PseudonymousUserId();

        Assert.DoesNotContain(
            Environment.UserName,
            identifier,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(16, identifier.Length);
    }

    [Fact]
    public void The_user_identifier_is_stable_across_calls()
    {
        // Иначе один и тот же сотрудник выглядел бы в журнале как несколько.
        Assert.Equal(ActorSettings.PseudonymousUserId(), ActorSettings.PseudonymousUserId());
    }

    [Fact]
    public void An_unconfigured_installation_can_do_nothing()
    {
        // Сквозная проверка: собранный инициатор без настройки не имеет прав.
        var actor = ActorSettings.Read(this.root.FullName);

        Assert.Equal(ClinicalRole.Unspecified, actor.Role);
        Assert.Empty(actor.Capabilities);
    }

    [Fact]
    public void A_configured_researcher_may_export_a_dataset_manifest()
    {
        this.Write(new { role = "Researcher" });

        var actor = ActorSettings.Read(this.root.FullName);

        Assert.True(actor.Can(Capability.ExportDatasetManifest));
        Assert.False(actor.Can(Capability.ExportClinicalReport));
    }

    private string Write(object settings)
    {
        var path = Path.Combine(this.root.FullName, ActorSettings.FileName);

        File.WriteAllText(path, JsonSerializer.Serialize(settings));

        return path;
    }
}
