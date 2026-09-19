using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Декодер JPEG Lossless.
///
/// Реальная серия с этим сжатием сверялась с libjpeg вне репозитория: все
/// 864 одноканальных кадра совпали побитно. Там встречается только первый
/// предиктор без перезапусков, поэтому остальные ветки процесса 14 проверяются
/// здесь, на синтетических кадрах. Кадры тестового кодера для всех предикторов,
/// перезапусков, категории 16 и 8 бит libjpeg тоже распаковывает в те же отсчёты.
/// </summary>
public sealed class JpegLosslessDecoderTests
{
    private const int Width = 13;
    private const int Height = 9;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Every_predictor_restores_the_frame(int predictor)
    {
        var samples = Pattern(precision: 16);

        var decoded = Decode(JpegLosslessEncoder.Encode(samples, Width, Height, precision: 16, predictor), bitsAllocated: 16);

        Assert.Equal(samples, Words(decoded));
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(12, 16)]
    [InlineData(16, 16)]
    public void Precision_up_to_the_allocated_bits_is_restored(int precision, int bitsAllocated)
    {
        var samples = Pattern(precision);

        var decoded = Decode(JpegLosslessEncoder.Encode(samples, Width, Height, precision, predictor: 4), bitsAllocated);

        Assert.Equal(samples, bitsAllocated == 8 ? decoded.Select(value => (int)value).ToArray() : Words(decoded));
    }

    [Theory]
    [InlineData(Width)]
    [InlineData(Width * 2)]
    [InlineData(Width * 4)]
    public void Restart_intervals_reset_the_prediction(int interval)
    {
        var samples = Pattern(precision: 12);

        var decoded = Decode(
            JpegLosslessEncoder.Encode(samples, Width, Height, precision: 12, predictor: 6, restartInterval: interval),
            bitsAllocated: 16);

        Assert.Equal(samples, Words(decoded));
    }

    [Fact]
    public void A_point_transform_is_refused_rather_than_guessed()
    {
        // Эталонный libjpeg и прочтение стандарта расходятся в начальном
        // предсказании при сдвиге точки; такой кадр отклоняется.
        var samples = Pattern(precision: 12).Select(value => value & ~1).ToArray();
        var encoded = JpegLosslessEncoder.Encode(samples, Width, Height, precision: 12, predictor: 1, pointTransform: 1);

        Assert.Throws<NotSupportedException>(() => Decode(encoded, bitsAllocated: 16));
    }

    [Fact]
    public void A_jump_of_half_the_range_uses_the_sixteenth_category()
    {
        // Категория 16 — единственная без дополнительных битов: разность 32768
        // по модулю 2^16. Её пропуск сдвинул бы весь остаток кадра.
        var samples = new int[Width * Height];

        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = index % 2 == 0 ? 0 : 32768;
        }

        var decoded = Decode(JpegLosslessEncoder.Encode(samples, Width, Height, precision: 16), bitsAllocated: 16);

        Assert.Equal(samples, Words(decoded));
    }

    [Fact]
    public void A_frame_of_another_size_is_refused()
    {
        var encoded = JpegLosslessEncoder.Encode(Pattern(16), Width, Height, precision: 16);

        Assert.Throws<InvalidDataException>(() => JpegLosslessDecoder.Decode(encoded, Height + 1, Width, 16));
    }

    [Fact]
    public void A_truncated_frame_is_refused_rather_than_padded()
    {
        var encoded = JpegLosslessEncoder.Encode(Pattern(16), Width, Height, precision: 16);
        var truncated = encoded.AsSpan(0, encoded.Length / 2).ToArray();

        Assert.Throws<InvalidDataException>(() => JpegLosslessDecoder.Decode(truncated, Height, Width, 16));
    }

    [Fact]
    public void Restart_markers_out_of_order_are_refused()
    {
        var encoded = JpegLosslessEncoder.Encode(Pattern(12), Width, Height, precision: 12, restartInterval: Width);
        var first = IndexOfMarker(encoded, 0xD0);

        encoded[first + 1] = 0xD3;

        Assert.Throws<InvalidDataException>(() => JpegLosslessDecoder.Decode(encoded, Height, Width, 16));
    }

    [Fact]
    public void Several_components_are_not_supported()
    {
        var encoded = JpegLosslessEncoder.Encode(Pattern(8), Width, Height, precision: 8, components: 3);

        Assert.Throws<NotSupportedException>(() => JpegLosslessDecoder.Decode(encoded, Height, Width, 8));
    }

    [Fact]
    public void Lossy_processes_are_not_supported()
    {
        // Заголовок baseline-кадра (SOF0): тот же поток, но процесс с потерями.
        var encoded = JpegLosslessEncoder.Encode(Pattern(8), Width, Height, precision: 8, frameMarker: 0xC0);

        Assert.Throws<NotSupportedException>(() => JpegLosslessDecoder.Decode(encoded, Height, Width, 8));
    }

    private static byte[] Decode(byte[] encoded, int bitsAllocated) =>
        JpegLosslessDecoder.Decode(encoded, Height, Width, bitsAllocated);

    /// <summary>
    /// Плавный рельеф с шумом во весь диапазон: разности попадают во все
    /// категории, а 0xFF в сжатом потоке заставляет вставлять нулевые байты.
    /// </summary>
    private static int[] Pattern(int precision)
    {
        var random = new Random(20260919);
        var top = (1 << precision) - 1;
        var samples = new int[Width * Height];

        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < Width; column++)
            {
                var smooth = (row * 997) + (column * 131);
                var noise = random.Next(0, top + 1);

                samples[(row * Width) + column] = (row + column) % 3 == 0 ? noise : smooth % (top + 1);
            }
        }

        samples[0] = top;
        samples[1] = 0;

        return samples;
    }

    private static int[] Words(byte[] decoded)
    {
        var values = new int[decoded.Length / 2];

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = decoded[2 * index] | (decoded[(2 * index) + 1] << 8);
        }

        return values;
    }

    private static int IndexOfMarker(byte[] data, byte marker)
    {
        for (var index = 0; index + 1 < data.Length; index++)
        {
            if (data[index] == 0xFF && data[index + 1] == marker)
            {
                return index;
            }
        }

        throw new InvalidOperationException("The marker is not in the stream.");
    }
}
