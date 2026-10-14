using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Hydrocephalus.Desktop;

/// <summary>
/// Переводит заголовок окна в тёмное оформление.
///
/// Заголовок и рамку рисует не WPF, а система, и цвет она берёт из темы
/// Windows: при светлой теме над тёмным снимком оставалась белая полоса во
/// всю ширину. Другого пути к тёмному заголовку у обычного окна нет: отказ
/// от системной рамки (<c>WindowStyle="None"</c>) забрал бы вместе с ней
/// кнопки свёртывания и закрытия, перетаскивание за заголовок и прилипание
/// к краям экрана — цена несоразмерная цвету полосы.
/// </summary>
internal static class DarkTitleBar
{
    // Атрибут объявлен с Windows 10 20H1; в сборках до неё тот же смысл имел
    // номер 19. Пробуются оба по очереди: на неподдерживаемом номере вызов
    // возвращает ошибку и ничего не меняет, поэтому проверять версию
    // системы отдельно не нужно.
    private const int UseImmersiveDarkMode = 20;

    private const int UseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>
    /// Применяет тёмный заголовок к окну.
    ///
    /// Вызывать после создания дескриптора окна (<c>OnSourceInitialized</c>):
    /// до этого дескриптора нет, а менять нечего.
    /// </summary>
    /// <param name="window">Окно, заголовок которого затемняется.</param>
    internal static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;

        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(
                handle,
                UseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int size);
}
