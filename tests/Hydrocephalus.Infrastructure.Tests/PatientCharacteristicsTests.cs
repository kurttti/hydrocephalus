using FellowOakDicom;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Чтение возраста и пола из источника.
///
/// Эти два поля нужны как ковариаты сравнения групп, но в файлы они не
/// попадают: профиль обезличивания удаляет их по-прежнему, и последний тест
/// здесь именно это и держит.
/// </summary>
public sealed class PatientCharacteristicsTests
{
    [Theory]
    [InlineData("065Y", 65)]
    [InlineData("65Y", 65)]
    [InlineData("065", 65)]
    [InlineData("008M", 0)]
    [InlineData("052W", 1)]
    [InlineData("730D", 2)]
    public void Age_is_read_in_whole_years_whatever_unit_the_scanner_wrote(
        string value,
        int expected)
    {
        Assert.Equal(expected, Read(dataset => dataset.AddOrUpdate(DicomTag.PatientAge, value)).AgeYears);
    }

    [Fact]
    public void Age_above_the_cap_is_not_written_precisely()
    {
        // Точный год в пожилом конце распределения сам работает идентификатором,
        // поэтому значение упирается в предел, а не растёт дальше.
        Assert.Equal(
            PatientCharacteristics.AgeCap,
            Read(dataset => dataset.AddOrUpdate(DicomTag.PatientAge, "094Y")).AgeYears);
    }

    [Fact]
    public void Age_is_derived_from_the_dates_when_the_tag_is_missing()
    {
        // День рождения ещё не наступил: полных лет на день исследования 69, не 70.
        var characteristics = Read(dataset =>
        {
            dataset.AddOrUpdate(DicomTag.PatientBirthDate, "19551103");
            dataset.AddOrUpdate(DicomTag.StudyDate, "20250701");
        });

        Assert.Equal(69, characteristics.AgeYears);
    }

    [Fact]
    public void Age_is_null_when_the_source_names_neither_the_tag_nor_the_dates()
    {
        Assert.Null(Read(_ => { }).AgeYears);
    }

    [Fact]
    public void Implausible_age_is_refused_rather_than_clamped()
    {
        // 999Y значит «тег заполнен мусором», и мусор не должен превратиться
        // в правдоподобные 90 лет.
        Assert.Null(Read(dataset => dataset.AddOrUpdate(DicomTag.PatientAge, "999Y")).AgeYears);
    }

    [Theory]
    [InlineData("M", "M")]
    [InlineData("f", "F")]
    [InlineData("O", "O")]
    public void Sex_is_normalised(string value, string expected)
    {
        Assert.Equal(expected, Read(dataset => dataset.AddOrUpdate(DicomTag.PatientSex, value)).Sex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("U")]
    public void Unrecognised_sex_stays_empty_instead_of_being_guessed(string value)
    {
        Assert.Null(Read(dataset => dataset.AddOrUpdate(DicomTag.PatientSex, value)).Sex);
    }

    [Fact]
    public void Both_tags_are_still_removed_by_the_profile()
    {
        // Характеристики живут в манифесте набора, а не в файлах: читаются они
        // из источника до обезличивания, и сам профиль не ослаблен.
        Assert.True(DeidentificationProfile.IsRemoved(DicomTag.PatientAge));
        Assert.True(DeidentificationProfile.IsRemoved(DicomTag.PatientSex));
    }

    private static PatientCharacteristics Read(Action<DicomDataset> customize)
    {
        var dataset = SyntheticDicom.BuildSlice(studyUid: "1.2.3.1", seriesUid: "1.2.3.11");

        // Правка идёт в обход проверки значений: разбор обязан выдерживать то,
        // что пишут аппараты, — возраст без единицы и пол строчной буквой, —
        // а библиотека такие значения записать не даёт. При чтении файла она их
        // не проверяет, то есть до разбора они доходят как есть.
        customize(dataset.NotValidated());

        return PatientCharacteristics.Read(dataset);
    }
}
