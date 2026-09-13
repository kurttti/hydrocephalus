using System.ComponentModel;
using System.Runtime.CompilerServices;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Inference.Segmentation;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Viewing;

/// <summary>
/// Состояние одного вида: какая ось перелистывается, какой срез показан
/// и что на нём нарисовано.
///
/// Логика вида отделена от разметки намеренно: перепутанные стороны и съехавший
/// оверлей — ошибки, которые на экране выглядят правдоподобно, и проверять их
/// нужно тестом, а не глазом.
/// </summary>
public sealed class PlaneView : INotifyPropertyChanged
{
    private readonly IVoxelVolume volume;
    private readonly VoxelMask? mask;
    private readonly VoxelMask? review;
    private readonly PlaneTransform transform;
    private readonly int rawWidth;
    private readonly int rawHeight;

    private int index;
    private WindowLevel window;
    private bool showOverlay = true;
    private bool showReview;

    /// <summary>
    /// Создаёт вид.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="mask">Маска сегментации либо <see langword="null"/>.</param>
    /// <param name="axis">Ось перелистывания.</param>
    /// <param name="window">Начальное окно и уровень.</param>
    /// <param name="review">
    /// Разбор решения сегментации либо <see langword="null"/>: что метод выбрал
    /// и что отбросил. Показывается вместо маски по запросу врача.
    /// </param>
    public PlaneView(
        IVoxelVolume volume,
        VoxelMask? mask,
        VolumeAxis axis,
        WindowLevel window,
        VoxelMask? review = null)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (mask is not null && !mask.Fits(volume))
        {
            // Маска с другой сетки ложится со смещением и при этом выглядит
            // как неточная сегментация. Показывать её нельзя.
            throw new ArgumentException("The mask does not fit the volume grid.", nameof(mask));
        }

        if (review is not null && !review.Fits(volume))
        {
            throw new ArgumentException("The review does not fit the volume grid.", nameof(review));
        }

        this.volume = volume;
        this.mask = mask;
        this.review = review;
        this.Axis = axis;
        this.window = window;

        this.Count = VolumeSlicer.CountAlong(volume, axis);

        // Ориентация вывода не зависит от номера среза: оси у всех срезов
        // вида одни. Поэтому она вычисляется один раз, по первому срезу.
        var sample = VolumeSlicer.Extract(volume, axis, 0, window);

        this.rawWidth = sample.Width;
        this.rawHeight = sample.Height;
        this.transform = DisplayOrientation.For(sample.Labels, sample.Plane);

        // Начинается с середины: срединные структуры интереснее краёв,
        // и открывать вид на пустом первом срезе незачем.
        this.index = this.Count / 2;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Ось перелистывания.</summary>
    public VolumeAxis Axis { get; }

    /// <summary>Число срезов вдоль оси.</summary>
    public int Count { get; }

    /// <summary>Признак наличия маски.</summary>
    public bool HasMask => this.mask is not null;

    /// <summary>Признак наличия разбора решения сегментации.</summary>
    public bool HasReview => this.review is not null;

    /// <summary>
    /// Показывать ли вместо маски разбор решения: выбранное методом и
    /// отброшенный ликвор. Выключен по умолчанию: на экране по умолчанию
    /// то, что пошло в измерение, а не черновик метода.
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
            this.Raise(nameof(this.ShowReview));
            this.Raise(nameof(this.Overlay));
        }
    }

    /// <summary>Номер показанного среза.</summary>
    public int Index
    {
        get => this.index;
        set
        {
            var clamped = Math.Clamp(value, 0, this.Count - 1);

            if (this.index == clamped)
            {
                return;
            }

            this.index = clamped;
            this.Raise(nameof(this.Index));
            this.Raise(nameof(this.Image));
            this.Raise(nameof(this.Overlay));
        }
    }

    /// <summary>Окно и уровень.</summary>
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
            this.Raise(nameof(this.Window));
            this.Raise(nameof(this.Image));
        }
    }

    /// <summary>
    /// Показывать ли оверлей маски. Возможность выключить обязательна:
    /// врач должен видеть исходное изображение без наложений (ADR 0007).
    /// </summary>
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
            this.Raise(nameof(this.ShowOverlay));
            this.Raise(nameof(this.Overlay));
        }
    }

    /// <summary>Текущий срез изображения.</summary>
    public PlaneImage Image => DisplayOrientation.Apply(
        VolumeSlicer.Extract(this.volume, this.Axis, this.index, this.window),
        this.transform);

    /// <summary>
    /// Текущий срез маски — или разбора решения, если он включён, — либо
    /// <see langword="null"/>, если показывать нечего или оверлей выключен.
    /// </summary>
    public byte[]? Overlay
    {
        get
        {
            var plane = !this.showOverlay ? null
                : this.showReview && this.review is not null ? this.review.ExtractPlane(this.Axis, this.index)
                : this.mask?.ExtractPlane(this.Axis, this.index);

            // Маска поворачивается тем же преобразованием, что и изображение:
            // иначе оверлей лёг бы зеркально к анатомии.
            return plane is null
                ? null
                : DisplayOrientation.Apply(plane, this.rawWidth, this.rawHeight, this.transform);
        }
    }

    /// <summary>Анатомическая плоскость этого вида.</summary>
    public ImagingPlane Plane => this.Image.Plane;

    /// <summary>Подписи сторон изображения.</summary>
    public EdgeLabels Labels => this.Image.Labels;

    /// <summary>Перелистывает срез на заданное число позиций.</summary>
    /// <param name="delta">Смещение; отрицательное листает назад.</param>
    public void Step(int delta) => this.Index = this.index + delta;

    private void Raise([CallerMemberName] string? property = null) =>
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
