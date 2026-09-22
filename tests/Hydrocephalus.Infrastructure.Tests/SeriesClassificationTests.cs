using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Распознавание характера серии по описанию — эвристика над свободным текстом
/// производителя. Тесты фиксируют и то, что она ловит, и то, что она заведомо
/// пропускает: у правила есть ложноотрицательные срабатывания, и они не должны
/// выглядеть неожиданностью при разборе результатов.
/// </summary>
public sealed class SeriesClassificationTests
{
    [Theory]
    [InlineData("T1 MPRAGE +C")]
    [InlineData("T1 post contrast")]
    [InlineData("CE T1 axial")]
    [InlineData("T1 GD axial")]
    [InlineData("Т1 с контрастом")]
    public void Contrast_markers_are_detected(string description) =>
        Assert.True(SeriesClassification.LooksContrastEnhanced(description));

    [Theory]
    [InlineData("T1 MPRAGE")]
    [InlineData("T2 FLAIR")]
    [InlineData("T1 non-contrast")]
    [InlineData("T1 pre-contrast")]
    [InlineData("")]
    [InlineData(null)]
    public void Plain_series_are_not_treated_as_contrast_enhanced(string? description) =>
        Assert.False(SeriesClassification.LooksContrastEnhanced(description));

    [Fact]
    public void Known_false_negative_is_documented_rather_than_hidden()
    {
        // Описание без общепринятого маркера контраста распознать нельзя.
        // Это ограничение эвристики, а не дефект: окончательное отделение
        // постконтрастных серий требует ручной проверки на приёмке.
        Assert.False(SeriesClassification.LooksContrastEnhanced("T1 axial repeat"));
    }

    [Theory]
    [InlineData("T1 MPRAGE", SeriesWeighting.T1)]
    [InlineData("AX T2", SeriesWeighting.T2)]
    [InlineData("T2 FLAIR", SeriesWeighting.Flair)]
    [InlineData("localizer", SeriesWeighting.Unknown)]
    public void Weighting_is_detected_from_the_description(string description, SeriesWeighting expected) =>
        Assert.Equal(expected, SeriesClassification.DetectWeighting(description));

    [Theory]
    [InlineData("Т1 сагиттальный", SeriesWeighting.T1)]
    [InlineData("Т2 аксиальный", SeriesWeighting.T2)]
    [InlineData("Т2 ФЛАИР", SeriesWeighting.Flair)]
    [InlineData("Т1 с контрастом", SeriesWeighting.T1)]
    public void Cyrillic_letters_that_look_like_latin_ones_are_read_the_same(
        string description, SeriesWeighting expected) =>
        // «Т» в этих описаниях кириллическая (U+0422). На вид она неотличима от
        // латинской, и операторы набирают её не задумываясь; до приведения
        // раскладок такие серии уходили в Unknown, хотя контраст в них
        // распознавался — правила расходились между собой.
        Assert.Equal(expected, SeriesClassification.DetectWeighting(description));

    [Theory]
    [InlineData("MPRAGE sag", SeriesWeighting.T1)]
    [InlineData("3D BRAVO", SeriesWeighting.T1)]
    [InlineData("FSPGR AX", SeriesWeighting.T1)]
    [InlineData("HASTE cor", SeriesWeighting.T2)]
    public void Sequence_names_are_used_when_the_description_omits_the_weighting(
        string description, SeriesWeighting expected) =>
        Assert.Equal(expected, SeriesClassification.DetectWeighting(description));

    [Theory]
    [InlineData("3D SPACE")]
    [InlineData("CUBE sag")]
    [InlineData("VISTA")]
    [InlineData("TSE ax")]
    public void Sequence_names_that_cover_several_weightings_stay_unknown(string description) =>
        // Под этими именами выпускаются и T1, и T2, и FLAIR. Честное «не знаю»
        // здесь лучше догадки: неверная взвешенность уводит серию не в тот
        // конвейер, а отсутствующая лишь оставляет её без автоматического отбора.
        Assert.Equal(SeriesWeighting.Unknown, SeriesClassification.DetectWeighting(description));

    [Fact]
    public void Magnetisation_prepared_volumes_are_read_as_T1()
    {
        // Самая ценная группа выборки: 28 серий с описанием, в котором нет ни
        // «T1», ни «MPRAGE», но SequenceVariant = MP при трёхмерном наборе и
        // коротком эхе не оставляет вариантов.
        var mprage = new SeriesClassification.AcquisitionParameters("GR", "MP", "3D", 2300, 2.98, 900);

        Assert.Equal(SeriesWeighting.T1, SeriesClassification.DetectWeighting("sag isotropic", mprage));
    }

    [Fact]
    public void Diffusion_is_not_mistaken_for_T2_even_though_its_echo_is_long()
    {
        // По физике эхо-планарная диффузия T2-взвешена, и правило по временам
        // записало бы в T2 сразу 172 серии выборки. Анатомическим снимком они не
        // являются: признак различения — эхо-планарная последовательность.
        var diffusion = new SeriesClassification.AcquisitionParameters("EP SE", "SK SP", "2D", 5000, 90, 0);

        Assert.Equal(SeriesWeighting.Unknown, SeriesClassification.DetectWeighting("b1000", diffusion));
    }

    [Theory]
    [InlineData("GR", "TOF MTC SP", "3D", 25.0, 3.5, 0.0, SeriesWeighting.Unknown)]
    [InlineData("SE", "NONE", "2D", 4000.0, 100.0, 0.0, SeriesWeighting.T2)]
    [InlineData("SE", "SK", "2D", 9000.0, 120.0, 2500.0, SeriesWeighting.Flair)]
    [InlineData("SE", "NONE", "2D", 500.0, 12.0, 0.0, SeriesWeighting.T1)]
    [InlineData("", "", "", 0.0, 0.0, 0.0, SeriesWeighting.Unknown)]
    public void Sequence_parameters_are_used_when_the_description_says_nothing(
        string sequence, string variant, string acquisition,
        double repetition, double echo, double inversion, SeriesWeighting expected)
    {
        var parameters = new SeriesClassification.AcquisitionParameters(
            sequence, variant, acquisition, repetition, echo, inversion);

        Assert.Equal(expected, SeriesClassification.DetectWeighting("исследование", parameters));
    }

    [Fact]
    public void The_description_wins_over_the_parameters()
    {
        // Описание набирал человек, знавший, что снимает. Параметры подключаются
        // только там, где текста не хватило, иначе они переспорили бы оператора.
        var looksLikeT1 = new SeriesClassification.AcquisitionParameters("SE", "NONE", "2D", 500, 12, 0);

        Assert.Equal(SeriesWeighting.T2, SeriesClassification.DetectWeighting("AX T2", looksLikeT1));
    }
}
