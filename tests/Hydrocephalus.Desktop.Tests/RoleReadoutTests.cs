using Hydrocephalus.Desktop.Composition;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Текст о роли в строке состояния.
///
/// Проверяется не формулировка, а то, что по ней можно отличить: настроена ли
/// установка вообще и есть ли у роли права. Приложение, молчащее о правах,
/// выглядит одинаково у врача и у ненастроенной установки, и разница
/// обнаруживается только отказом посреди работы.
/// </summary>
public sealed class RoleReadoutTests
{
    [Theory]
    [InlineData(ClinicalRole.Clinician)]
    [InlineData(ClinicalRole.Researcher)]
    public void A_role_with_rights_is_not_described_as_powerless(ClinicalRole role) =>
        Assert.DoesNotContain("нет", Describe(role), StringComparison.Ordinal);

    [Theory]
    [InlineData(ClinicalRole.Unspecified)]
    public void A_role_without_rights_says_so(ClinicalRole role) =>
        Assert.Contains("доступных операций нет", Describe(role), StringComparison.Ordinal);

    [Fact]
    public void A_role_that_cannot_open_a_study_says_so()
    {
        // У администратора есть ровно одно право — журнал аудита, — и без
        // этой оговорки приложение выглядело бы сломанным: кнопки на месте,
        // а открыть исследование нельзя.
        var text = Describe(ClinicalRole.Administrator);

        Assert.DoesNotContain("доступных операций нет", text, StringComparison.Ordinal);
        Assert.Contains("разбор исследований недоступен", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_administrator_reads_differently_from_an_unconfigured_installation()
    {
        // Права у обоих пусты, но это разные положения дел: у первого —
        // решение политики, у второго — недосмотр установки. Одинаковый текст
        // не дал бы различить их и скрыл бы недосмотр.
        Assert.NotEqual(
            Describe(ClinicalRole.Administrator),
            Describe(ClinicalRole.Unspecified));
    }

    [Fact]
    public void An_unconfigured_installation_says_the_role_was_never_set()
    {
        Assert.Contains("не задана", Describe(ClinicalRole.Unspecified), StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_unknown_to_the_readout_is_not_shown_as_a_known_one()
    {
        // Роль, добавленная в перечисление и забытая в тексте, не должна
        // притвориться врачом. Прав у неё нет — это следует из политики.
        var text = Describe((ClinicalRole)97);

        Assert.Contains("не распознана", text, StringComparison.Ordinal);
        Assert.Contains("доступных операций нет", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readout_never_carries_the_account_name()
    {
        // Строка состояния видна на экране в кабинете; имя учётной записи
        // в клинике обычно образовано от фамилии сотрудника.
        Assert.DoesNotContain(
            Environment.UserName,
            Describe(ClinicalRole.Clinician),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(ClinicalRole role) =>
        RoleReadout.Describe(Actor.Create("0123456789abcdef", role));
}
