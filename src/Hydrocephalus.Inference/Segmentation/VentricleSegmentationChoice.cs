using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Выбор способа разметки желудочков по тому, что установлено.
///
/// Живёт здесь, а не в составе приложения, потому что составов два: оконное
/// приложение и пакетный замер выборки. Один и тот же набор пакетов обязан
/// давать в них один и тот же способ — иначе прогон по выборке мерил бы не тем,
/// чем мерит программа у врача, и сверка результатов ничего бы не значила.
///
/// Чтение пакета принимается договором из домена: здесь не нужно знать ни про
/// ZIP, ни про ECDSA, ни про то, где лежит доверенный ключ.
/// </summary>
public static class VentricleSegmentationChoice
{
    /// <summary>
    /// Выбирает способ разметки.
    ///
    /// Три исхода, и они не равнозначны.
    ///
    /// **Модели нет** — нет действующего пакета либо нет чем его проверить. Это
    /// рядовое состояние: приложение поставляется без модели (ADR 0008 —
    /// установщик её не приносит), и до установки работает пороговый путь.
    ///
    /// **Пакет прошёл проверку** — модель получает серии T1, остальные
    /// взвешенности остаются пороговому пути
    /// (<see cref="WeightingRoutedVentricleSegmentation"/>). Веса берутся из
    /// того же прохода, которым сошлись хеши: распакованного файла на диске не
    /// возникает, и подменять нечего (ADR 0004).
    ///
    /// **Пакет проверку не прошёл** — анализ блокируется. Откат на пороговый
    /// путь здесь запрещён прямо: ADR 0008 требует не подставлять молча другой
    /// пакет, а остановиться до решения администратора. Врач не должен получить
    /// число, посчитанное не тем способом, который называет экран.
    /// </summary>
    /// <param name="activePackagePath">
    /// Путь к действующему пакету либо <see langword="null"/>, если его нет.
    /// </param>
    /// <param name="reader">
    /// Чем проверять пакет; <see langword="null"/>, если доверенного ключа нет.
    /// </param>
    /// <returns>Способ разметки. Обёртку с памятью накладывает вызывающий.</returns>
    public static IVentricleSegmentation For(
        string? activePackagePath,
        IModelPackageReader? reader) =>
        For(activePackagePath, reader, out _);

    /// <summary>
    /// Выбирает способ разметки и отдаёт итог проверки пакета.
    ///
    /// Итог нужен вызывающему, чтобы записать отказ в журнал: пакет, который
    /// испортился уже в хранилище, блокирует анализ, и бесследным это событие
    /// быть не должно (`docs/security/README.md`). Проверка при этом остаётся
    /// одна — второе чтение пакета ради той же справки стоило бы сотню
    /// мегабайт и оставило бы щель между проверкой и загрузкой.
    /// </summary>
    /// <param name="activePackagePath">
    /// Путь к действующему пакету либо <see langword="null"/>, если его нет.
    /// </param>
    /// <param name="reader">
    /// Чем проверять пакет; <see langword="null"/>, если доверенного ключа нет.
    /// </param>
    /// <param name="check">
    /// Итог проверки либо <see langword="null"/>, если проверять было нечего.
    /// </param>
    /// <returns>Способ разметки. Обёртку с памятью накладывает вызывающий.</returns>
    public static IVentricleSegmentation For(
        string? activePackagePath,
        IModelPackageReader? reader,
        out ModelPackageCheck? check)
    {
        check = null;

        if (activePackagePath is null || reader is null)
        {
            return new ThresholdVentricleSegmentation();
        }

        var opened = reader.Open(activePackagePath);

        check = opened;

        // Принятый пакет обязан отдать и объявление, и веса: разбор доходит до
        // них одним проходом. Если чего-то нет — это не «почти прошёл», а
        // противоречие внутри проверки, и блокировка уместнее попытки продолжить.
        if (opened.Rejection is not null
            || opened.Weights is not { } weights
            || opened.Manifest is not { } manifest)
        {
            return new BlockedVentricleSegmentation(
                opened.Rejection?.ToString() ?? "incompleteCheck",
                opened.Detail);
        }

        // Модель получает T1, остальное остаётся пороговому пути: модель
        // проверена только на T1, а отнимать у T2 и FLAIR то, что уже считалось,
        // установка модели не должна.
        return new WeightingRoutedVentricleSegmentation(
            new ModelVentricleSegmentation(
                new OnnxVentricleSegmentation(weights), manifest.ModelVersion),
            new ThresholdVentricleSegmentation());
    }
}
