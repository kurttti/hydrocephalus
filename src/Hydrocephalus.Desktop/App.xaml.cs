namespace Hydrocephalus.Desktop;

/// <summary>
/// Composition root приложения: здесь собираются реализации портов через DI.
/// Базовый тип указан полным именем намеренно — короткое <c>Application</c> разрешается
/// в пространство имён <see cref="Hydrocephalus.Application"/>, а не в тип WPF.
/// </summary>
public partial class App : System.Windows.Application
{
}
