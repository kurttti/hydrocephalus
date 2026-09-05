using System.Globalization;
using System.Numerics;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Построение замещающих DICOM UID.
///
/// Псевдонимы из <see cref="Pseudonyms"/> — шестнадцатеричные строки, и записать их
/// в поле с VR UI нельзя: UID состоит только из цифр и точек и ограничен 64 символами.
/// Поэтому те же 128 бит псевдонима отображаются в дугу 2.25, зарезервированную
/// под UUID: значение остаётся детерминированным при одной соли, поэтому ссылочная
/// целостность внутри пациента сохраняется (ADR 0003), но исходный UID из него
/// не восстанавливается.
/// </summary>
internal static class PseudonymousUid
{
    /// <summary>Корневая дуга UUID в пространстве OID.</summary>
    private const string UuidArc = "2.25.";

    /// <summary>
    /// Вычисляет замещающий UID.
    /// </summary>
    /// <param name="salt">Соль псевдонимизации.</param>
    /// <param name="scope">Область имён, например "study" или "instance".</param>
    /// <param name="value">Исходный UID.</param>
    /// <returns>UID вида 2.25.&lt;целое&gt;, не длиннее 64 символов.</returns>
    internal static string Derive(string salt, string scope, string value)
    {
        var bytes = Convert.FromHexString(Pseudonyms.Derive(salt, scope, value));

        // Дуга 2.25 предназначена для UUID, поэтому 128 бит приводятся к виду
        // UUID версии 4: иначе значение синтаксически корректно, но семантически
        // не является UUID, на который эта дуга ссылается.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        var number = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);

        return UuidArc + number.ToString(CultureInfo.InvariantCulture);
    }
}
