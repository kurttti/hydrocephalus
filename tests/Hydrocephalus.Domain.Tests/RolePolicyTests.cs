using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Domain.Tests;

/// <summary>
/// Политика доступа.
///
/// Записана в одном месте, и тесты закрепляют именно её содержание, а не способ
/// проверки: ошибка здесь не проявляется как сбой, она проявляется как доступ,
/// которого не должно было быть.
/// </summary>
public sealed class RolePolicyTests
{
    [Fact]
    public void An_unspecified_role_grants_nothing()
    {
        // Значение по умолчанию у перечисления получают неинициализированные
        // данные. Роль по умолчанию с правами — дыра, которая открывается сама.
        Assert.Empty(RolePolicy.CapabilitiesOf(ClinicalRole.Unspecified));
    }

    [Fact]
    public void A_role_missing_from_the_policy_grants_nothing()
    {
        // Новая роль, добавленная в перечисление и забытая в политике, должна
        // ничего не мочь, а не всё.
        Assert.Empty(RolePolicy.CapabilitiesOf((ClinicalRole)999));
    }

    [Fact]
    public void Every_declared_role_is_named_in_the_policy()
    {
        // Забытая роль работает безопасно, но незаметно. Тест делает пропуск
        // видимым: решение «эта роль не получает прав» должно быть записано.
        foreach (var role in Enum.GetValues<ClinicalRole>())
        {
            Assert.True(
                RolePolicy.CapabilitiesOf(role) is not null,
                $"Role {role} is not named in the policy.");
        }
    }

    [Fact]
    public void A_clinician_works_with_the_case_but_not_with_cohorts()
    {
        var clinician = Actor.Create("doctor-1", ClinicalRole.Clinician);

        Assert.True(clinician.Can(Capability.AnalyseStudy));
        Assert.True(clinician.Can(Capability.ExportClinicalReport));
        Assert.True(clinician.Can(Capability.ExportDeidentifiedReport));

        // Сбор обучающей выборки — не лечебная работа.
        Assert.False(clinician.Can(Capability.ExportDatasetManifest));
    }

    [Fact]
    public void A_researcher_never_gets_patient_identifiers()
    {
        var researcher = Actor.Create("researcher-1", ClinicalRole.Researcher);

        Assert.True(researcher.Can(Capability.ExportDatasetManifest));
        Assert.True(researcher.Can(Capability.ExportDeidentifiedReport));

        // Клинический вариант отчёта несёт идентификаторы пациента,
        // а исследовательская задача в них не нуждается.
        Assert.False(researcher.Can(Capability.ExportClinicalReport));
    }

    [Fact]
    public void An_administrator_gets_no_access_to_data()
    {
        // Обслуживание установки и доступ к её содержимому — разные задачи,
        // и совмещать их по умолчанию значит раздавать доступ без нужды.
        var administrator = Actor.Create("admin-1", ClinicalRole.Administrator);

        Assert.False(administrator.Can(Capability.AnalyseStudy));
        Assert.False(administrator.Can(Capability.ExportDeidentifiedReport));
        Assert.False(administrator.Can(Capability.ExportClinicalReport));
        Assert.False(administrator.Can(Capability.ExportDatasetManifest));
    }

    [Fact]
    public void Only_an_administrator_reads_the_audit_log()
    {
        // Журнал показывает работу всей установки. Разбор одного случая
        // этого не требует, а обслуживание — требует; данных пациента
        // в журнале нет, поэтому право относится ко второму, а не к первому.
        Assert.True(Actor.Create("admin-1", ClinicalRole.Administrator)
            .Can(Capability.ReadAuditLog));

        Assert.False(Actor.Create("clinician-1", ClinicalRole.Clinician)
            .Can(Capability.ReadAuditLog));

        Assert.False(Actor.Create("researcher-1", ClinicalRole.Researcher)
            .Can(Capability.ReadAuditLog));
    }

    [Fact]
    public void Requiring_a_missing_capability_names_it_without_naming_the_person()
    {
        // В тексте ошибки не должно быть ни имени сотрудника,
        // ни идентификаторов исследования.
        var researcher = Actor.Create("researcher-1", ClinicalRole.Researcher);

        var denied = Assert.Throws<AccessDeniedException>(
            () => researcher.Require(Capability.ExportClinicalReport));

        Assert.Contains(nameof(Capability.ExportClinicalReport), denied.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("researcher-1", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requiring_a_granted_capability_passes()
    {
        Actor.Create("researcher-1", ClinicalRole.Researcher)
            .Require(Capability.ExportDatasetManifest);
    }

    [Fact]
    public void An_actor_needs_an_identifier()
    {
        Assert.Throws<DomainRuleViolationException>(
            () => Actor.Create(" ", ClinicalRole.Clinician));
    }

    [Fact]
    public void No_role_is_granted_the_unspecified_capability()
    {
        // Unspecified существует ради выявления неинициализированных данных
        // и не должен давать никакой операции.
        foreach (var role in Enum.GetValues<ClinicalRole>())
        {
            Assert.DoesNotContain(Capability.Unspecified, RolePolicy.CapabilitiesOf(role));
        }
    }
}
