using System.IO;
using System.Security.Cryptography;
using Hydrocephalus.Desktop.Composition;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Текст ошибки на экране.
///
/// Исходные данные приходят с PHI в именах файлов и каталогов (ADR 0006),
/// а исключения ввода-вывода и разбора DICOM несут полный путь в своём тексте.
/// Проверяется, что текст исключения не доходит до экрана ни для одной
/// категории: утечка строится явно, и тест требует, чтобы её не было.
/// </summary>
public sealed class ErrorReadoutTests
{
    private const string Surname = "Иванов";

    private const string LeakingMessage =
        @"Could not find file 'D:\Иванов И.И. 1968\МРТ головы\IM0001'.";

    public static TheoryData<Exception> Leaks() =>
    [
        new FileNotFoundException(LeakingMessage),
        new DirectoryNotFoundException(LeakingMessage),
        new UnauthorizedAccessException(LeakingMessage),
        new PathTooLongException(LeakingMessage),
        new IOException(LeakingMessage),
        new InvalidDataException(LeakingMessage),
        new CryptographicException(LeakingMessage),
        new DomainRuleViolationException(LeakingMessage),
        new AccessDeniedException(LeakingMessage),
        new OperationCanceledException(LeakingMessage),
        new InvalidOperationException(LeakingMessage),
        new FormatException(LeakingMessage),
        new AggregateException(new IOException(LeakingMessage)),
    ];

    [Theory]
    [MemberData(nameof(Leaks))]
    public void The_exception_text_never_reaches_the_screen(Exception exception)
    {
        var text = ErrorReadout.Describe(exception);

        Assert.DoesNotContain(Surname, text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IM0001", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_type_is_named_so_support_can_find_the_failure()
    {
        // Тип — это код, а не данные: путей и имён в нём не бывает.
        Assert.Contains(
            nameof(FileNotFoundException),
            ErrorReadout.Describe(new FileNotFoundException(LeakingMessage)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_told_apart_from_damaged_data()
    {
        // «Файл недоступен» и «данные повреждены» ведут к разным действиям.
        Assert.NotEqual(
            Category(new FileNotFoundException()),
            Category(new InvalidDataException()));
    }

    [Fact]
    public void A_specific_io_failure_is_not_swallowed_by_the_general_one()
    {
        Assert.NotEqual(
            Category(new FileNotFoundException()),
            Category(new IOException()));
    }

    [Fact]
    public void A_domain_rule_is_not_swallowed_by_invalid_operation()
    {
        Assert.NotEqual(
            Category(new DomainRuleViolationException("x")),
            Category(new InvalidOperationException("x")));
    }

    [Fact]
    public void A_single_wrapped_failure_is_described_by_its_cause()
    {
        Assert.Equal(
            ErrorReadout.Describe(new IOException()),
            ErrorReadout.Describe(new AggregateException(new IOException())));
    }

    private static string Category(Exception exception)
    {
        var text = ErrorReadout.Describe(exception);

        return text[..text.LastIndexOf(" (", StringComparison.Ordinal)];
    }
}
