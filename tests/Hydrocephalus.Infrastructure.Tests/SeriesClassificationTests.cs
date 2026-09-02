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
}
