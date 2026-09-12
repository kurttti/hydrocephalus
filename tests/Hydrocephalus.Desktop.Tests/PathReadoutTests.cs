// В WPF-проекте короткое Path разрешается в System.Windows.Shapes.Path.
using Hydrocephalus.Desktop.Composition;
using IoPath = System.IO.Path;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Путь для показа на экране.
///
/// Каталоги приложения лежат в профиле пользователя, а путь профиля содержит
/// имя учётной записи — в клинике обычно производное от фамилии сотрудника.
/// Проверяется, что имя уходит, а путь остаётся рабочим.
/// </summary>
public sealed class PathReadoutTests
{
    [Fact]
    public void Local_application_data_is_named_by_its_variable()
    {
        var path = IoPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hydrocephalus",
            "audit");

        Assert.Equal(
            "%LOCALAPPDATA%" + IoPath.DirectorySeparatorChar + "Hydrocephalus" + IoPath.DirectorySeparatorChar + "audit",
            PathReadout.Describe(path));
    }

    [Fact]
    public void Elsewhere_in_the_profile_is_named_by_the_profile_variable()
    {
        var path = IoPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents");

        Assert.StartsWith("%USERPROFILE%", PathReadout.Describe(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_outside_the_profile_is_left_as_it_is()
    {
        // Каталог, выбранный администратором вне профиля, ничьего имени
        // не содержит, и переписывать его значило бы показать не тот путь.
        const string path = @"D:\hydrocephalus\exports";

        Assert.Equal(path, PathReadout.Describe(path));
    }
}
