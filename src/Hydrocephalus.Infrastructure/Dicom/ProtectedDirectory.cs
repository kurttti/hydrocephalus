using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Создание каталога с доступом, ограниченным учётной записью приложения
/// (docs/security/README.md, ADR 0006).
///
/// Ограничение прав — не замена шифрованию: оно защищает от других учётных
/// записей на включённой машине и не защищает от доступа к диску в обход системы.
/// Шифрование содержимого рабочей копии по ADR 0006 здесь не выполняется.
/// </summary>
internal static class ProtectedDirectory
{
    /// <summary>
    /// Создаёт каталог, при возможности ограничив доступ текущей учётной записью.
    /// </summary>
    /// <param name="path">Путь каталога.</param>
    /// <param name="restrictAccess">Ограничивать ли доступ.</param>
    internal static void Create(string path, bool restrictAccess)
    {
        if (!restrictAccess || !OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        CreateRestricted(path);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateRestricted(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var identity = WindowsIdentity.GetCurrent().User;

        if (identity is null)
        {
            // Учётная запись без SID встречается только в нетипичных контекстах;
            // молча создавать каталог с наследуемыми правами нельзя.
            throw new InvalidOperationException(
                "The current Windows identity has no security identifier, so the working copy directory cannot be restricted.");
        }

        var security = new DirectorySecurity();

        // Наследование отключается: иначе права родительского каталога, каким бы
        // он ни был выбран администратором, молча расширили бы доступ.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(identity);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // Права задаются при создании, а не после: каталог, созданный обычным
        // способом и исправленный следом, существует какое-то время с правами,
        // унаследованными от родителя.
        new DirectoryInfo(path).Create(security);
    }
}
