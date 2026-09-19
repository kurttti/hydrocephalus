namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Декодер JPEG Lossless (ITU-T T.81, приложение H, процесс 14) для одноканальных
/// снимков DICOM: синтаксисы 1.2.840.10008.1.2.4.57 и 1.2.840.10008.1.2.4.70.
///
/// Свой управляемый декодер вместо нативного кодека: файлы из внешних источников
/// недоверенные, и разбирать их кодом на C значит впустить в процесс ошибки
/// работы с памятью; нативные кодеки к тому же увеличивают установщик и SBOM.
/// Процесс 14 невелик — предсказание по соседям и коды Хаффмана разностей, — и
/// каждая его ветка проверяется тестами.
///
/// Всё, что выходит за пределы нужного (несколько компонент, арифметическое
/// кодирование, иерархический режим), отклоняется явно, а не декодируется наугад.
/// </summary>
internal static class JpegLosslessDecoder
{
    /// <summary>JPEG Lossless, процесс 14, любой предиктор.</summary>
    public const string Process14 = "1.2.840.10008.1.2.4.57";

    /// <summary>JPEG Lossless, процесс 14, первый предиктор (SV1).</summary>
    public const string Process14FirstOrder = "1.2.840.10008.1.2.4.70";

    /// <summary>Поддерживается ли синтаксис передачи.</summary>
    public static bool Handles(string transferSyntaxUid) =>
        transferSyntaxUid is Process14 or Process14FirstOrder;

    /// <summary>
    /// Декодирует кадр в несжатые отсчёты: по байту на отсчёт при
    /// <paramref name="bitsAllocated"/> = 8, по два байта little-endian при 16.
    /// </summary>
    /// <param name="compressed">Сжатый кадр целиком, от SOI до EOI.</param>
    /// <param name="rows">Число строк, объявленное в DICOM.</param>
    /// <param name="columns">Число столбцов, объявленное в DICOM.</param>
    /// <param name="bitsAllocated">Разрядность хранения, 8 или 16.</param>
    /// <returns>Несжатый кадр.</returns>
    /// <exception cref="InvalidDataException">Если поток повреждён или не совпадает с заголовком DICOM.</exception>
    /// <exception cref="NotSupportedException">Если поток использует возможности вне процесса 14 для одной компоненты.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> compressed, int rows, int columns, int bitsAllocated)
    {
        if (bitsAllocated is not (8 or 16))
        {
            throw new NotSupportedException("JPEG Lossless frames are decoded to 8 or 16 allocated bits only.");
        }

        var reader = new MarkerReader(compressed);
        var tables = new HuffmanTable?[4];
        var restartInterval = 0;
        Frame? frame = null;

        if (reader.NextMarker() != Soi)
        {
            throw new InvalidDataException("The JPEG stream does not start with SOI.");
        }

        while (true)
        {
            var marker = reader.NextMarker();

            switch (marker)
            {
                case Sof3:
                    frame = ReadFrame(reader.Segment(), rows, columns, bitsAllocated);
                    break;

                case Dht:
                    ReadHuffmanTables(reader.Segment(), tables);
                    break;

                case Dri:
                    restartInterval = ReadRestartInterval(reader.Segment());
                    break;

                case Sos:
                    if (frame is null)
                    {
                        throw new InvalidDataException("The JPEG scan precedes its frame header.");
                    }

                    var scan = ReadScan(reader.Segment(), frame, tables);

                    return DecodeScan(compressed[reader.Position..], frame, scan, restartInterval, bitsAllocated);

                case Eoi:
                    throw new InvalidDataException("The JPEG stream ends before its scan.");

                case >= 0xC0 and <= 0xCF when marker is not (Dht or 0xC8 or Dac):
                    throw new NotSupportedException("Only the lossless Huffman process (SOF3) is supported.");

                case Dac:
                    throw new NotSupportedException("Arithmetic-coded JPEG is not supported.");

                case Dhp or Exp:
                    throw new NotSupportedException("Hierarchical JPEG is not supported.");

                default:
                    // APPn, COM и прочие сегменты с длиной описания не меняют.
                    reader.Segment();
                    break;
            }
        }
    }

    private static Frame ReadFrame(ReadOnlySpan<byte> segment, int rows, int columns, int bitsAllocated)
    {
        if (segment.Length < 6)
        {
            throw new InvalidDataException("The JPEG frame header is truncated.");
        }

        var precision = segment[0];
        var lines = (segment[1] << 8) | segment[2];
        var samplesPerLine = (segment[3] << 8) | segment[4];
        var components = segment[5];

        if (components != 1)
        {
            throw new NotSupportedException("Only single-component JPEG Lossless frames are supported.");
        }

        if (segment.Length < 6 + (3 * components))
        {
            throw new InvalidDataException("The JPEG frame header is truncated.");
        }

        if (precision is < 2 or > 16 || precision > bitsAllocated)
        {
            throw new InvalidDataException("The JPEG sample precision does not fit the allocated bits.");
        }

        // Размер кадра обязан совпасть с заголовком DICOM: иначе разметка
        // и геометрия серии относились бы к другому изображению.
        if (lines != rows || samplesPerLine != columns)
        {
            throw new InvalidDataException("The JPEG frame size differs from the DICOM image size.");
        }

        return new Frame(precision, lines, samplesPerLine, segment[6]);
    }

    private static void ReadHuffmanTables(ReadOnlySpan<byte> segment, HuffmanTable?[] tables)
    {
        var position = 0;

        while (position < segment.Length)
        {
            if (position + 17 > segment.Length)
            {
                throw new InvalidDataException("The JPEG Huffman table is truncated.");
            }

            var tableClass = segment[position] >> 4;
            var identifier = segment[position] & 0x0F;

            if (tableClass != 0 || identifier > 3)
            {
                throw new InvalidDataException("Lossless JPEG uses DC Huffman tables 0–3 only.");
            }

            var counts = segment.Slice(position + 1, 16);
            var total = 0;

            foreach (var count in counts)
            {
                total += count;
            }

            if (total > 17 || position + 17 + total > segment.Length)
            {
                // Разностей в процессе 14 семнадцать категорий (0–16).
                throw new InvalidDataException("The JPEG Huffman table is malformed.");
            }

            tables[identifier] = new HuffmanTable(counts, segment.Slice(position + 17, total));
            position += 17 + total;
        }
    }

    private static int ReadRestartInterval(ReadOnlySpan<byte> segment)
    {
        if (segment.Length != 2)
        {
            throw new InvalidDataException("The JPEG restart interval segment is malformed.");
        }

        return (segment[0] << 8) | segment[1];
    }

    private static Scan ReadScan(ReadOnlySpan<byte> segment, Frame frame, HuffmanTable?[] tables)
    {
        if (segment.Length < 6 || segment[0] != 1 || segment.Length != 1 + (2 * segment[0]) + 3)
        {
            throw new InvalidDataException("The JPEG scan header is malformed.");
        }

        if (segment[1] != frame.ComponentId)
        {
            throw new InvalidDataException("The JPEG scan refers to an unknown component.");
        }

        var table = tables[segment[2] >> 4]
            ?? throw new InvalidDataException("The JPEG scan refers to a missing Huffman table.");

        var predictor = segment[3];
        var pointTransform = segment[5] & 0x0F;

        if (predictor is < 1 or > 7)
        {
            throw new InvalidDataException("The JPEG lossless predictor is out of range.");
        }

        if (pointTransform != 0)
        {
            // Со сдвигом точки эталонный libjpeg и прочтение T.81 (H.1.2.1)
            // расходятся в начальном предсказании: 2^(P-1) против 2^(P-Pt-1),
            // и отсчёты отличаются на половину диапазона. Угадывать нельзя;
            // в DICOM сдвиг практически всегда нулевой.
            throw new NotSupportedException("JPEG Lossless with a point transform is not supported.");
        }

        return new Scan(table, predictor, pointTransform);
    }

    private static byte[] DecodeScan(
        ReadOnlySpan<byte> entropy,
        Frame frame,
        Scan scan,
        int restartInterval,
        int bitsAllocated)
    {
        var width = frame.SamplesPerLine;
        var height = frame.Lines;
        var samples = new int[width * height];
        var bits = new BitReader(entropy);
        var precision = frame.Precision - scan.PointTransform;
        var initial = 1 << (precision - 1);
        var mask = (1 << 16) - 1;

        var sinceRestart = 0;
        var intervalStart = 0;
        var nextRestartMarker = 0;

        for (var index = 0; index < samples.Length; index++)
        {
            if (restartInterval > 0 && sinceRestart == restartInterval)
            {
                bits.Restart(Rst0 + nextRestartMarker);
                nextRestartMarker = (nextRestartMarker + 1) & 7;
                sinceRestart = 0;
                intervalStart = index;
            }

            var row = index / width;
            var column = index % width;
            int prediction;

            if (index == intervalStart)
            {
                // Первый отсчёт скана и каждого интервала перезапуска.
                prediction = initial;
            }
            else if (index - intervalStart < width)
            {
                // Первая строка интервала: сверху ещё ничего нет.
                prediction = samples[index - 1];
            }
            else if (column == 0)
            {
                prediction = samples[index - width];
            }
            else
            {
                var left = samples[index - 1];
                var above = samples[index - width];
                var diagonal = samples[index - width - 1];

                prediction = scan.Predictor switch
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

            samples[index] = (prediction + ReadDifference(ref bits, scan.Table)) & mask;
            sinceRestart++;
        }

        if (bits.Exhausted)
        {
            // Верный поток не требует читать дальше своих данных: недостающие
            // биты значат, что кадр обрезан или испорчен, и отсчёты за этим
            // местом выдуманы.
            throw new InvalidDataException("The JPEG scan ends before all samples are decoded.");
        }

        return Pack(samples, scan.PointTransform, bitsAllocated);
    }

    private static int ReadDifference(ref BitReader bits, HuffmanTable table)
    {
        var category = table.Decode(ref bits);

        switch (category)
        {
            case 0:
                return 0;
            case 16:
                // Категория 16 не несёт дополнительных битов (H.1.2.2).
                return 32768;
            case > 16:
                throw new InvalidDataException("The JPEG difference category is out of range.");
        }

        var value = bits.Read(category);

        // Расширение знака (F.2.2.1): старший бит 0 означает отрицательную разность.
        return value < (1 << (category - 1)) ? value - (1 << category) + 1 : value;
    }

    private static byte[] Pack(int[] samples, int pointTransform, int bitsAllocated)
    {
        if (bitsAllocated == 8)
        {
            var bytes = new byte[samples.Length];

            for (var index = 0; index < samples.Length; index++)
            {
                bytes[index] = (byte)(samples[index] << pointTransform);
            }

            return bytes;
        }

        var words = new byte[samples.Length * 2];

        for (var index = 0; index < samples.Length; index++)
        {
            var value = (ushort)(samples[index] << pointTransform);

            words[2 * index] = (byte)value;
            words[(2 * index) + 1] = (byte)(value >> 8);
        }

        return words;
    }

    private const int Soi = 0xD8;
    private const int Eoi = 0xD9;
    private const int Sof3 = 0xC3;
    private const int Dht = 0xC4;
    private const int Dac = 0xCC;
    private const int Dri = 0xDD;
    private const int Sos = 0xDA;
    private const int Dhp = 0xDE;
    private const int Exp = 0xDF;
    private const int Rst0 = 0xD0;

    private sealed record Frame(int Precision, int Lines, int SamplesPerLine, int ComponentId);

    private sealed record Scan(HuffmanTable Table, int Predictor, int PointTransform);

    /// <summary>Разбор сегментов с маркерами до начала энтропийных данных.</summary>
    private ref struct MarkerReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;

        public int Position { get; private set; }

        public int NextMarker()
        {
            // Между сегментами допускаются заполняющие 0xFF.
            while (Position < this.data.Length && this.data[Position] != 0xFF)
            {
                Position++;
            }

            while (Position < this.data.Length && this.data[Position] == 0xFF)
            {
                Position++;
            }

            if (Position >= this.data.Length)
            {
                throw new InvalidDataException("The JPEG stream is truncated.");
            }

            return this.data[Position++];
        }

        public ReadOnlySpan<byte> Segment()
        {
            if (Position + 2 > this.data.Length)
            {
                throw new InvalidDataException("The JPEG segment length is truncated.");
            }

            var length = (this.data[Position] << 8) | this.data[Position + 1];

            if (length < 2 || Position + length > this.data.Length)
            {
                throw new InvalidDataException("The JPEG segment length is out of range.");
            }

            var segment = this.data.Slice(Position + 2, length - 2);

            Position += length;

            return segment;
        }
    }

    /// <summary>
    /// Чтение битов энтропийных данных со снятием вставленных нулей (0xFF 0x00).
    /// Встретив маркер или конец данных, читатель отдаёт нули и запоминает это:
    /// выхода за границы нет, а обрезанный кадр отклоняется после скана.
    /// </summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position;
        private int buffer;
        private int available;
        private bool atMarker;

        public bool Exhausted { get; private set; }

        public int Read(int count)
        {
            var value = 0;

            for (var bit = 0; bit < count; bit++)
            {
                value = (value << 1) | ReadBit();
            }

            return value;
        }

        public int ReadBit()
        {
            if (available == 0)
            {
                Fill();
            }

            available--;

            return (buffer >> available) & 1;
        }

        /// <summary>Переходит через маркер перезапуска, отбрасывая недочитанные биты байта.</summary>
        public void Restart(int expectedMarker)
        {
            available = 0;
            atMarker = false;

            if (position + 1 >= this.data.Length || this.data[position] != 0xFF || this.data[position + 1] != expectedMarker)
            {
                throw new InvalidDataException("The JPEG restart marker is missing or out of order.");
            }

            position += 2;
        }

        private void Fill()
        {
            if (atMarker || position >= this.data.Length)
            {
                Exhausted = true;
                buffer = 0;
                available = 8;

                return;
            }

            var next = this.data[position];

            if (next == 0xFF)
            {
                if (position + 1 < this.data.Length && this.data[position + 1] == 0x00)
                {
                    position += 2;
                }
                else
                {
                    // Маркер: байт не потребляется, чтобы его увидел Restart.
                    atMarker = true;
                    Exhausted = true;
                    buffer = 0;
                    available = 8;

                    return;
                }
            }
            else
            {
                position++;
            }

            buffer = next;
            available = 8;
        }
    }

    /// <summary>Канонический код Хаффмана (F.2.2.3): наибольший код каждой длины и начало её символов.</summary>
    private sealed class HuffmanTable
    {
        private readonly int[] maxCode = new int[17];
        private readonly int[] valueOffset = new int[17];
        private readonly byte[] values;

        public HuffmanTable(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
        {
            values = symbols.ToArray();

            var code = 0;
            var index = 0;

            for (var length = 1; length <= 16; length++)
            {
                var count = counts[length - 1];

                valueOffset[length] = index - code;
                code += count;
                index += count;
                maxCode[length] = count == 0 ? -1 : code - 1;

                if (code > (1 << length))
                {
                    throw new InvalidDataException("The JPEG Huffman table has more codes than its lengths allow.");
                }

                code <<= 1;
            }
        }

        public int Decode(ref BitReader bits)
        {
            var code = 0;

            for (var length = 1; length <= 16; length++)
            {
                code = (code << 1) | bits.ReadBit();

                if (maxCode[length] >= 0 && code <= maxCode[length])
                {
                    return values[code + valueOffset[length]];
                }
            }

            throw new InvalidDataException("The JPEG stream contains an invalid Huffman code.");
        }
    }
}
