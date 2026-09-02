using System.Windows;

namespace Hydrocephalus.Desktop;

/// <summary>
/// Главное окно приложения. Composition root подключает реализации через DI;
/// окно не выполняет медицинских вычислений и не читает DICOM напрямую.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Создаёт главное окно.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
