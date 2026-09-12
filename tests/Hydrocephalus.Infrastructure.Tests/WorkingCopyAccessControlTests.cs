using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Тест, который выполняется только в Windows.
///
/// Пропуск, а не молчаливый успех: тест, ничего не проверивший на другой
/// системе, отчитывается зелёным и выглядит как выполненная проверка.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    /// <summary>Создаёт атрибут, пропуская тест вне Windows.</summary>
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            this.Skip = "Права каталога проверяются средствами Windows.";
        }
    }
}

/// <summary>
/// Права каталогов рабочей копии (ADR 0006, план проверки).
///
/// Проверяется не то, что код выставил права, — это он делает по построению,
/// и прочитать их обратно значило бы проверить самого себя. Проверяется
/// утверждение, ради которого права и выставляются: **посторонняя учётная
/// запись доступа не получает**, в том числе через наследование от родительского
/// каталога, который выбирает администратор установки.
///
/// Поэтому тестов два, и второй обязателен. Первый показывает, что права
/// родителя до рабочей копии не дошли; второй — что они вообще были и дошли бы
/// до обычного каталога. Без второго первый доказывал бы лишь то, что права
/// родителя не применились ни к чему.
///
/// Ограничение прав не заменяет шифрования: оно защищает от других учётных
/// записей на включённой машине и не защищает от чтения диска в обход системы.
/// </summary>
public sealed class WorkingCopyAccessControlTests : IDisposable
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo parent = SyntheticDicom.CreateTempDirectory();

    /// <summary>Удаляет временный каталог.</summary>
    public void Dispose()
    {
        if (this.parent.Exists)
        {
            this.parent.Delete(recursive: true);
        }
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public async Task The_rights_of_the_parent_directory_do_not_reach_the_working_copy()
    {
        // Родительский каталог выбирает тот, кто разворачивает приложение.
        // Если бы наследование осталось включённым, права этого каталога
        // молча расширили бы доступ к расшифрованным данным пациента.
        var others = this.GrantOthersAccessToParent();

        var session = await WorkingCopySession.CreateAsync(
            Path.Combine(this.parent.FullName, "working-copies"),
            "session-1",
            Moment,
            restrictAccess: true,
            CancellationToken.None);

        Assert.DoesNotContain(others, IdentitiesWithAccessTo(session.Directory));
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void An_ordinary_directory_under_the_same_parent_does_inherit_those_rights()
    {
        // Обратная проверка. Без неё предыдущий тест проходил бы и в случае,
        // когда выданное родителю право не применилось вообще ни к чему,
        // и доказывал бы не ограничение доступа, а собственную бессмысленность.
        var others = this.GrantOthersAccessToParent();

        var ordinary = Directory.CreateDirectory(
            Path.Combine(this.parent.FullName, "ordinary"));

        Assert.Contains(others, IdentitiesWithAccessTo(ordinary.FullName));
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public async Task The_working_copy_belongs_to_the_account_the_application_runs_under()
    {
        var session = await WorkingCopySession.CreateAsync(
            Path.Combine(this.parent.FullName, "working-copies"),
            "session-1",
            Moment,
            restrictAccess: true,
            CancellationToken.None);

        var security = new DirectoryInfo(session.Directory).GetAccessControl();

        Assert.Equal(
            WindowsIdentity.GetCurrent().User,
            security.GetOwner(typeof(SecurityIdentifier)));

        // Наследование отключено явно: иначе права родителя вернулись бы
        // при следующем их изменении, уже после создания каталога.
        Assert.True(security.AreAccessRulesProtected);
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public async Task The_root_of_the_working_copies_is_protected_too()
    {
        // Корень создаёт то же приложение, и он существует дольше отдельного
        // сеанса. Защитить сеанс внутри открытого всем корня значило бы
        // оставить видимыми имена каталогов и время работы.
        var others = this.GrantOthersAccessToParent();

        var root = Path.Combine(this.parent.FullName, "working-copies");

        await WorkingCopySession.CreateAsync(
            root,
            "session-1",
            Moment,
            restrictAccess: true,
            CancellationToken.None);

        Assert.DoesNotContain(others, IdentitiesWithAccessTo(root));
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SecurityIdentifier> IdentitiesWithAccessTo(string directory) =>
    [
        .. new DirectoryInfo(directory)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference),
    ];

    /// <summary>
    /// Выдаёт наследуемое право чтения учётной записи, отличной от текущей.
    /// </summary>
    /// <returns>Идентификатор, которому выдано право.</returns>
    /// <remarks>
    /// Право только разрешающее: запрет самому себе сделал бы невозможной
    /// уборку временного каталога после теста.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private SecurityIdentifier GrantOthersAccessToParent()
    {
        var others = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null);

        var security = this.parent.GetAccessControl();

        security.AddAccessRule(new FileSystemAccessRule(
            others,
            FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        this.parent.SetAccessControl(security);

        return others;
    }
}
