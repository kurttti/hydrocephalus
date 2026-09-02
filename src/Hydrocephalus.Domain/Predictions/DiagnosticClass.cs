namespace Hydrocephalus.Domain.Predictions;

/// <summary>
/// Класс, различаемый моделью. Перечень задаётся model package (labels.json), а не кодом:
/// состав классов меняется вместе с моделью и не должен требовать пересборки приложения.
/// </summary>
/// <param name="Code">Стабильный код класса, например "inph".</param>
public readonly record struct DiagnosticClass(string Code);

/// <summary>
/// Вероятность одного класса.
/// </summary>
/// <param name="Class">Класс.</param>
/// <param name="Probability">Вероятность в диапазоне от 0 до 1.</param>
public readonly record struct ClassProbability(DiagnosticClass Class, double Probability);
