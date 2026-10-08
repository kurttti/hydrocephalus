using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Segmentation;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Сегментация желудочков обученной моделью (ADR 0009).
///
/// Модель — FastSurfer VINN, единственный кандидат, у которого и код, и веса под
/// разрешительной лицензией. Из её разметки берутся только метки желудочковой
/// системы: боковые с нижними рогами, III и IV.
///
/// Работает **один аксиальный вид из трёх**. Полная модель складывает три, и для
/// этого нужен изотропный объём около миллиметра — таких в выборке 4 серии из 26.
/// Ещё 22 — аксиальные T1 толщиной больше 4 мм, где корональный и сагиттальный
/// виды собирались бы из срезов, между которыми 5–7 мм пустоты. Это заведомо
/// ослабленный режим, и его результат помечается отдельно.
///
/// Вход сети — семь соседних срезов приведённого объёма: сеть двумерная, но
/// смотрит на окрестность поперёк среза. У края стопка дополняется повтором
/// крайнего среза.
/// </summary>
public sealed class OnnxVentricleSegmentation : IConformedVolumeLabelling, IDisposable
{
    /// <summary>Число соседних срезов, которые сеть принимает за раз.</summary>
    public const int SliceStack = 7;

    /// <summary>Версия карты меток этой модели.</summary>
    public const string LabelMapVersion = "fastsurfer-vinn-axial-2.0.0";

    /// <inheritdoc />
    string IConformedVolumeLabelling.LabelMapVersion => LabelMapVersion;

    /// <summary>Метка желудочковой системы в выдаваемой маске.</summary>
    public static readonly AnatomicalLabel VentricularSystem = new("ventricular-system");

    /// <summary>
    /// Метки FreeSurfer по номеру класса сети.
    ///
    /// Получено из `FastSurferCNN/config/FastSurfer_ColorLUT.tsv` версии 2.5.4, а
    /// не набрано руками: ошибка здесь дала бы молча не ту анатомию.
    /// </summary>
    private static readonly int[] FreeSurferLabels =
    [
        0, 2, 4, 5, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 24, 26, 28, 31, 41,
        43, 44, 46, 47, 49, 50, 51, 52, 53, 54, 58, 60, 63, 77, 1002, 1003, 1005,
        1006, 1007, 1008, 1009, 1010, 1011, 1012, 1013, 1014, 1015, 1016, 1017,
        1018, 1019, 1020, 1021, 1022, 1023, 1024, 1025, 1026, 1027, 1028, 1029,
        1030, 1031, 1034, 1035, 2002, 2005, 2010, 2012, 2013, 2014, 2016, 2017,
        2021, 2022, 2023, 2024, 2025, 2028,
    ];

    /// <summary>
    /// Метки желудочковой системы в разметке FreeSurfer: боковые желудочки, их
    /// нижние рога, III и IV.
    /// </summary>
    private static readonly int[] VentricleLabels = [4, 5, 14, 15, 43, 44];

    private readonly InferenceSession session;
    private readonly bool[] isVentricle;

    /// <summary>
    /// Открывает модель.
    /// </summary>
    /// <param name="modelPath">Путь к файлу модели в формате ONNX.</param>
    public OnnxVentricleSegmentation(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        this.session = new InferenceSession(modelPath);
        this.isVentricle = new bool[FreeSurferLabels.Length];

        for (var index = 0; index < FreeSurferLabels.Length; index++)
        {
            this.isVentricle[index] = Array.IndexOf(VentricleLabels, FreeSurferLabels[index]) >= 0;
        }
    }

    /// <summary>
    /// Размечает приведённый объём и возвращает маску желудочков в его сетке.
    /// </summary>
    /// <param name="conformed">Приведённый куб, как его отдаёт <see cref="VolumeConforming"/>.</param>
    /// <param name="progress">Доля размеченных срезов, если нужна.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Метки куба: 1 — желудочки, 0 — остальное.</returns>
    public byte[] Segment(
        byte[] conformed,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conformed);

        var size = VolumeConforming.Size;
        var expected = size * size * size;

        if (conformed.Length != expected)
        {
            throw new DomainRuleViolationException(
                $"The conformed volume must hold {expected} samples, not {conformed.Length}.");
        }

        var inputName = this.session.InputMetadata.Keys.Single();
        var mask = new byte[expected];
        var tensor = new DenseTensor<float>([1, SliceStack, size, size]);
        var area = size * size;

        // Лучший класс и его значение по каждому отсчёту среза. Заводятся один
        // раз: на 256 срезов это 256 выделений вместо двух.
        var bestValue = new float[area];
        var bestLabel = new byte[area];

        // Осевые срезы приведённого куба перпендикулярны второй оси: укладка
        // LIA ставит по ней направление «вниз». Внутри среза строка тензора
        // отвечает оси «вперёд», а столбец — оси «влево»: срез подаётся сети
        // транспонированным. Определено сверкой с перехваченным входом, а не
        // выведено: при обратной раскладке модель дала 9,4 мл вместо 110,6.
        for (var plane = 0; plane < size; plane++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Fill(tensor, conformed, plane, size);

            using var results = this.session.Run(
                [NamedOnnxValue.CreateFromTensor(inputName, tensor)]);

            // Выход читается сплошным куском памяти, а не по многомерному
            // указателю: там на каждый отсчёт пересчитывается индекс с проверкой
            // границ, и перебор 79 классов по 65 тысячам отсчётов каждого из 256
            // срезов — это 1,3 млрд таких обращений.
            var logits = results.Single().Value as DenseTensor<float>
                ?? throw new DomainRuleViolationException(
                    "The model returned an output that is not a dense tensor.");
            var values = logits.Buffer.Span;

            bestValue.AsSpan().Fill(float.NegativeInfinity);

            for (var label = 0; label < FreeSurferLabels.Length; label++)
            {
                var offset = label * area;

                for (var index = 0; index < area; index++)
                {
                    var value = values[offset + index];

                    if (value > bestValue[index])
                    {
                        bestValue[index] = value;
                        bestLabel[index] = (byte)label;
                    }
                }
            }

            for (var row = 0; row < size; row++)
            {
                var rowBase = (row * size) + plane;

                for (var column = 0; column < size; column++)
                {
                    if (this.isVentricle[bestLabel[(row * size) + column]])
                    {
                        mask[(rowBase * size) + column] = 1;
                    }
                }
            }

            progress?.Report((plane + 1) / (double)size);
        }

        return mask;
    }

    /// <summary>Освобождает модель.</summary>
    public void Dispose() => this.session.Dispose();

    /// <summary>
    /// Набирает стопку из семи соседних срезов вокруг заданного.
    ///
    /// У края объёма стопка дополняется повтором крайнего среза: иначе сеть
    /// получила бы пустоту там, где у неё в обучении была анатомия.
    /// </summary>
    private static void Fill(DenseTensor<float> tensor, byte[] conformed, int plane, int size)
    {
        var destination = tensor.Buffer.Span;
        var area = size * size;

        for (var channel = 0; channel < SliceStack; channel++)
        {
            var source = Math.Clamp(plane + channel - (SliceStack / 2), 0, size - 1);
            var channelBase = channel * area;

            for (var row = 0; row < size; row++)
            {
                var from = ((row * size) + source) * size;
                var to = channelBase + (row * size);

                for (var column = 0; column < size; column++)
                {
                    // Сеть обучена на значениях от нуля до единицы, а не 0–255.
                    destination[to + column] = conformed[from + column] / 255f;
                }
            }
        }
    }
}
