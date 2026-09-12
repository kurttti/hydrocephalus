using System.IO;
using System.Security.Cryptography;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Ошибка, приведённая к тексту, пригодному для показа на экране.
///
/// Текст исключения на экран не попадает — ни целиком, ни по частям. Исходные
/// данные приходят с PHI в именах файлов и каталогов (ADR 0006), а исключения
/// ввода-вывода и разбора DICOM несут в тексте полный путь: «Could not find file
/// 'D:\Иванов И.И. 1968\IM0001'» — это фамилия пациента в строке состояния,
/// которую видно в кабинете. Вырезать из текста пути нельзя: перечислить, что
/// сторонние библиотеки кладут в сообщение, невозможно, поэтому недоверенным
/// считается сообщение целиком.
///
/// Показывается категория, выведенная из <b>типа</b> исключения, и имя типа.
/// Тип — это код, а не данные: в нём не бывает ни путей, ни имён, и по нему
/// сопровождение найдёт место отказа. Врачу категории достаточно, чтобы понять,
/// что делать дальше: «файл недоступен» и «данные повреждены» ведут к разным
/// действиям, а путь к файлу, который он сам выбрал, ему не нужен.
/// </summary>
public static class ErrorReadout
{
    /// <summary>
    /// Описывает ошибку без её текста.
    /// </summary>
    /// <param name="exception">Исключение.</param>
    /// <returns>Категория ошибки и имя типа исключения.</returns>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Обёртка ничего не говорит о причине: у задачи с одной ошибкой
        // причина — эта ошибка.
        var cause = exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception;

        return CategoryOf(cause) + " (" + cause.GetType().Name + ")";
    }

    // Порядок проверок — от частного к общему: FileNotFoundException является
    // IOException, а DomainRuleViolationException — InvalidOperationException,
    // и общая ветвь первой проглотила бы более точную.
    private static string CategoryOf(Exception exception) => exception switch
    {
        // Окна перехватывают отказ по правам раньше и называют операцию сами;
        // сюда он доходит только необработанным, через обработчик приложения.
        AccessDeniedException => "роль не даёт права на эту операцию",
        OperationCanceledException => "операция отменена",
        DomainRuleViolationException => "данные не отвечают требованиям приложения",
        FileNotFoundException or DirectoryNotFoundException => "файл или каталог не найден",
        PathTooLongException => "слишком длинный путь к файлу",
        UnauthorizedAccessException => "нет доступа к файлу или каталогу",
        InvalidDataException => "данные повреждены или имеют неподдерживаемый формат",
        IOException => "ошибка чтения или записи файла",
        CryptographicException => "не удалось защитить или расшифровать данные",
        OutOfMemoryException or InsufficientMemoryException => "недостаточно памяти",

        // Библиотека DICOM сюда не подключается ради одной проверки типа:
        // её исключения узнаются по пространству имён.
        _ when IsDicomLibrary(exception) => "файл не разобран как DICOM",

        InvalidOperationException => "операция невозможна в текущем состоянии",
        _ => "непредвиденная ошибка",
    };

    private static bool IsDicomLibrary(Exception exception) =>
        exception.GetType().Namespace?.StartsWith("FellowOakDicom", StringComparison.Ordinal) == true;
}
