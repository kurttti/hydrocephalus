using System.ComponentModel;
using System.Runtime.CompilerServices;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Inference.Segmentation;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Viewing;

/// <summary>
/// Состояние экрана просмотра: три вида одного объёма и общее для них окно.
///
/// Окно общее намеренно: три вида показывают одни и те же данные, и разные
/// окна на них создали бы впечатление разной ткани там, где ткань одна.
/// </summary>
public sealed class StudyView : INotifyPropertyChanged
{
    private WindowLevel window;
    private bool showOverlay = true;
    private bool showReview;

    /// <summary>
    /// Создаёт экран просмотра.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="mask">Маска сегментации либо <see langword="null"/>.</param>
    /// <param name="review">Разбор решения сегментации либо <see langword="null"/>.</param>
    public StudyView(IVoxelVolume volume, VoxelMask? mask = null, VoxelMask? review = null)
    {
        ArgumentNullException.ThrowIfNull(volume);

        // Подсказка из тегов есть только у загруженного объёма;
        // у приведённого к другой сетке её неоткуда взять.
        this.window = WindowLevelSelector.For(volume, (volume as VoxelVolume)?.SuggestedWindow);

        this.Planes =
        [
            new PlaneView(volume, mask, VolumeAxis.AcrossSlices, this.window, review),
            new PlaneView(volume, mask, VolumeAxis.AcrossRows, this.window, review),
            new PlaneView(volume, mask, VolumeAxis.AcrossColumns, this.window, review),
        ];

        this.HasMask = mask is not null;
        this.HasReview = review is not null;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Три вида объёма.</summary>
    public IReadOnlyList<PlaneView> Planes { get; }

    /// <summary>Признак наличия маски.</summary>
    public bool HasMask { get; }

    /// <summary>Признак наличия разбора решения сегментации.</summary>
    public bool HasReview { get; }

    /// <summary>
    /// Показывать ли на всех видах разбор решения вместо маски. Общий
    /// для трёх видов по той же причине, что и окно: на одном виде маска,
    /// на другом разбор — и цвета значили бы разное на соседних картинках.
    /// </summary>
    public bool ShowReview
    {
        get => this.showReview;
        set
        {
            if (this.showReview == value)
            {
                return;
            }

            this.showReview = value;

            foreach (var plane in this.Planes)
            {
                plane.ShowReview = value;
            }

            this.Raise(nameof(this.ShowReview));
        }
    }

    /// <summary>Окно и уровень, общие для всех видов.</summary>
    public WindowLevel Window
    {
        get => this.window;
        set
        {
            if (this.window == value)
            {
                return;
            }

            this.window = value;

            foreach (var plane in this.Planes)
            {
                plane.Window = value;
            }

            this.Raise(nameof(this.Window));
        }
    }

    /// <summary>Показывать ли оверлей маски на всех видах.</summary>
    public bool ShowOverlay
    {
        get => this.showOverlay;
        set
        {
            if (this.showOverlay == value)
            {
                return;
            }

            this.showOverlay = value;

            foreach (var plane in this.Planes)
            {
                plane.ShowOverlay = value;
            }

            this.Raise(nameof(this.ShowOverlay));
        }
    }

    /// <summary>
    /// Возвращает вид указанной анатомической плоскости.
    /// </summary>
    /// <param name="plane">Плоскость.</param>
    /// <returns>Вид либо <see langword="null"/>, если такой плоскости нет.</returns>
    public PlaneView? OfPlane(ImagingPlane plane) =>
        this.Planes.FirstOrDefault(item => item.Plane == plane);

    private void Raise([CallerMemberName] string? property = null) =>
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
