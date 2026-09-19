namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Кодер JPEG Lossless (процесс 14) для синтетических фикстур. Реальных снимков
/// в тестах нет, поэтому сжатые кадры строятся здесь: одна компонента, одна
/// таблица Хаффмана, в которой все семнадцать категорий разности закодированы
/// пятью битами, — так в фикстуру попадает каждая категория, включая 16.
/// </summary>
internal static class JpegLosslessEncoder
{
    /// <summary>Кодирует кадр.</summary>
    /// <param name="samples">Отсчёты, столбец быстрее строки; младшие <paramref name="pointTransform"/> битов нулевые.</param>
    /// <param name="width">Число столбцов.</param>
    /// <param name="height">Число строк.</param>
    /// <param name="precision">Разрядность отсчёта, 2–16.</param>
    /// <param name="predictor">Предиктор, 1–7.</param>
    /// <param name="pointTransform">Сдвиг точки.</param>
    /// <param name="restartInterval">Интервал перезапуска в отсчётах; 0 — без перезапусков.</param>
    /// <param name="components">Число компонент в заголовке кадра — для проверки отказа.</param>
    /// <param name="frameMarker">Маркер заголовка кадра — для проверки отказа на других процессах.</param>
    /// <returns>Сжатый кадр от SOI до EOI.</returns>
    internal static byte[] Encode(
        int[] samples,
        int width,
        int height,
        int precision,
        int predictor = 1,
        int pointTransform = 0,
        int restartInterval = 0,
        byte components = 1,
        byte frameMarker = 0xC3)
    {
        var output = new List<byte> { 0xFF, 0xD8 };

        var frame = new List<byte>
        {
            (byte)precision, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, components,
        };

        for (var component = 1; component <= components; component++)
        {
            frame.AddRange([(byte)component, 0x11, 0x00]);
        }

        Segment(output, frameMarker, frame);

        var table = new List<byte> { 0x00 };
        table.AddRange(Enumerable.Range(1, 16).Select(length => (byte)(length == 5 ? 17 : 0)));
        table.AddRange(Enumerable.Range(0, 17).Select(symbol => (byte)symbol));
        Segment(output, 0xC4, table);

        if (restartInterval > 0)
        {
            Segment(output, 0xDD, [(byte)(restartInterval >> 8), (byte)restartInterval]);
        }

        Segment(output, 0xDA, [1, 1, 0x00, (byte)predictor, 0x00, (byte)pointTransform]);

        var bits = new BitWriter(output);
        var shifted = samples.Select(sample => sample >> pointTransform).ToArray();
        var initial = 1 << (precision - pointTransform - 1);
        var intervalStart = 0;
        var marker = 0;

        for (var index = 0; index < shifted.Length; index++)
        {
            if (restartInterval > 0 && index > 0 && (index - intervalStart) == restartInterval)
            {
                bits.Flush();
                output.AddRange([0xFF, (byte)(0xD0 + marker)]);
                marker = (marker + 1) & 7;
                intervalStart = index;
            }

            var column = index % width;
            int prediction;

            if (index == intervalStart)
            {
                prediction = initial;
            }
            else if (index - intervalStart < width)
            {
                prediction = shifted[index - 1];
            }
            else if (column == 0)
            {
                prediction = shifted[index - width];
            }
            else
            {
                var left = shifted[index - 1];
                var above = shifted[index - width];
                var diagonal = shifted[index - width - 1];

                prediction = predictor switch
                {
                    1 => left,
                    2 => above,
                    3 => diagonal,
                    4 => left + above - diagonal,
                    5 => left + ((above - diagonal) >> 1),
                    6 => above + ((left - diagonal) >> 1),
                    _ => (left + above) >> 1,
                };
            }

            var difference = (shifted[index] - prediction) & 0xFFFF;

            if (difference >= 32768)
            {
                difference -= 65536;
            }

            if (difference == -32768)
            {
                bits.Write(16, 5);
                continue;
            }

            var magnitude = Math.Abs(difference);
            var category = magnitude == 0 ? 0 : 32 - int.LeadingZeroCount(magnitude);

            bits.Write(category, 5);

            if (category > 0)
            {
                bits.Write(difference < 0 ? difference + (1 << category) - 1 : difference, category);
            }
        }

        bits.Flush();
        output.AddRange([0xFF, 0xD9]);

        return [.. output];
    }

    private static void Segment(List<byte> output, byte marker, List<byte> body)
    {
        output.AddRange([0xFF, marker, (byte)((body.Count + 2) >> 8), (byte)(body.Count + 2)]);
        output.AddRange(body);
    }

    private sealed class BitWriter(List<byte> output)
    {
        private int buffer;
        private int count;

        public void Write(int value, int length)
        {
            for (var bit = length - 1; bit >= 0; bit--)
            {
                buffer = (buffer << 1) | ((value >> bit) & 1);
                count++;

                if (count == 8)
                {
                    Emit();
                }
            }
        }

        /// <summary>Добивает байт единицами (F.1.2.3) перед маркером.</summary>
        public void Flush()
        {
            while (count != 0)
            {
                buffer = (buffer << 1) | 1;
                count++;

                if (count == 8)
                {
                    Emit();
                }
            }
        }

        private void Emit()
        {
            output.Add((byte)buffer);

            if (buffer == 0xFF)
            {
                output.Add(0x00);
            }

            buffer = 0;
            count = 0;
        }
    }
}
