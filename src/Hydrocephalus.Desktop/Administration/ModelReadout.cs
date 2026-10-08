using Hydrocephalus.Application;
using Hydrocephalus.Desktop.Composition;
using Hydrocephalus.Desktop.Results;
using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Desktop.Administration;

/// <summary>
/// Состояние пакетов модели и осмотр устанавливаемого пакета, приведённые
/// к тексту для экрана.
///
/// Отдельно от <see cref="AdministrationReadout"/>, потому что читается в двух
/// местах: разделом экрана администрирования и целиком — экраном модели.
/// Второй экран нужен ADR 0008: установка, подтверждение и переключение
/// версии — явные действия администратора, и у них своё окно.
///
/// Коды переводятся здесь, а не в журнале и не в проверке пакета: в файле
/// остаются коды, человекочитаемый текст собирается на уровне представления
/// (ADR 0005).
/// </summary>
public static class ModelReadout
{
    /// <summary>
    /// Описывает состояние пакетов модели одним разделом.
    /// </summary>
    /// <param name="models">Состояние установки.</param>
    /// <returns>Разделы для экрана модели.</returns>
    public static IReadOnlyList<ResultSection> Describe(ModelInstallationState models) =>
        [new ResultSection { Title = "Модель", Rows = Rows(models) }];

    /// <summary>
    /// Описывает состояние пакетов модели.
    ///
    /// Два вопроса разделены и отвечены по отдельности: чем приложение измеряет
    /// **в этом запуске** и какая версия помечена действующей. Способ разметки
    /// собирается один раз при запуске, поэтому новая метка начинает действовать
    /// только со следующего. Одна строка на оба вопроса заставила бы
    /// администратора принять неизменившееся значение за неудачу переключения —
    /// и переключить снова.
    /// </summary>
    /// <param name="models">Состояние установки.</param>
    /// <returns>Строки раздела.</returns>
    public static IReadOnlyList<ResultRow> Rows(ModelInstallationState models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var rows = new List<ResultRow>
        {
            new()
            {
                // То, что действительно считает: эта же строка стоит в отчёте
                // (ADR 0008 — отчёт обязан называть версию, которой получен).
                Text = "Измеряет в этом запуске: " + models.MeasuringNow,
                Severity = ResultSeverity.Neutral,
            },
        };

        rows.Add(models.ActiveVersion is { } active
            ? new ResultRow
            {
                Text = "Помечена действующей: " + active,
                Note = "Метка начинает действовать со следующего запуска приложения. "
                    + "Чем измеряют в этом — сказано строкой выше.",
                Severity = ResultSeverity.Neutral,
            }
            : new ResultRow
            {
                // Рядовое состояние, а не поломка: приложение поставляется
                // без модели, установщик её не приносит (ADR 0008).
                Text = "Действующей версии модели нет: желудочки размечаются пороговым методом.",
                Severity = ResultSeverity.Neutral,
            });

        rows.Add(models.InstalledVersions.Count == 0
            ? new ResultRow
            {
                Text = "Установленных пакетов нет.",
                Severity = ResultSeverity.Neutral,
            }
            : new ResultRow
            {
                Text = "Установлено: " + string.Join(", ", models.InstalledVersions),
                Note = "Установка новой версии не удаляет предыдущую: откат — "
                    + "переключение на одну из этих версий, без файла-источника (ADR 0008).",
                Severity = ResultSeverity.Neutral,
            });

        if (models.ActiveVersion is not null)
        {
            rows.Add(DescribeActivePackage(models));
        }

        if (!models.TrustKeyConfigured)
        {
            rows.Add(new ResultRow
            {
                // Не поломка, а незавершённая настройка установки: подпись
                // проверяется ключом, который приносит не пакет (ADR 0004).
                Text = "Доверенного ключа нет: проверить пакет нечем, установка недоступна.",
                Note = "Ключ ожидается файлом рядом с остальными секретами установки.",
                Severity = ResultSeverity.Warning,
            });
        }

        rows.Add(new ResultRow
        {
            // Названо отдельно от разметки: разметка моделью уже есть, а
            // классификатора в пакете нет, и «модель не подключена» было бы
            // неправдой про первое и правдой про второе.
            Text = "Классификатора в пакете нет: вероятность диагноза не выдаётся, "
                + "выдаются только признаки.",
            Severity = ResultSeverity.Warning,
        });

        if (models.StudyOpen)
        {
            rows.Add(new ResultRow
            {
                Text = "Пока открыто исследование, установка и переключение версии недоступны.",
                Note = "ADR 0008 запрещает менять версию во время разбора случая: "
                    + "отчёт должен называть ту версию, которой он получен.",
                Severity = ResultSeverity.Warning,
            });
        }

        rows.Add(PathReadout.Place("Пакеты модели", models.ModelRoot));

        return rows;
    }

    /// <summary>
    /// Описывает осмотренный пакет для подтверждения установки.
    ///
    /// ADR 0008 требует показать до подтверждения версию, идентификатор ключа,
    /// статус подписи, карточку модели и отличия от действующей версии. Всё
    /// перечисленное собрано здесь, и ничего, кроме перечисленного: экран
    /// подтверждения — последнее место, где администратор может отказаться.
    /// </summary>
    /// <param name="inspection">Итог осмотра пакета.</param>
    /// <param name="active">Объявление действующего пакета, если он есть.</param>
    /// <returns>Разделы для окна подтверждения.</returns>
    public static IReadOnlyList<ResultSection> DescribeInspection(
        ModelPackageInspection inspection,
        ModelPackageManifest? active)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        var sections = new List<ResultSection>
        {
            new() { Title = "Пакет", Rows = DescribePackage(inspection) },
        };

        // Отличия показываются только у принятого пакета: у отвергнутого
        // сравнивать имело бы смысл то, что можно установить, а установить
        // его нельзя — частичная загрузка запрещена (ADR 0004). Таблица
        // отличий рядом с причиной отказа читалась бы как «можно настоять».
        if (inspection.Accepted && inspection.Manifest is { } manifest)
        {
            sections.Add(new ResultSection
            {
                Title = "Отличия от действующей версии",
                Rows = DescribeDifferences(manifest, active),
            });
        }

        sections.Add(new ResultSection
        {
            Title = "Карточка модели",
            Rows = inspection.ModelCard.Length > 0
                ?
                [
                    new ResultRow { Text = inspection.ModelCard, Severity = ResultSeverity.Neutral },
                ]
                :
                [
                    new ResultRow
                    {
                        // Карточки нет только там, где разбор до неё не дошёл:
                        // в составе пакета она обязательна (ADR 0004).
                        Text = "Карточка не прочитана.",
                        Severity = ResultSeverity.Warning,
                    },
                ],
        });

        return sections;
    }

    /// <summary>
    /// Называет состояние подписи.
    /// </summary>
    /// <param name="signature">Состояние.</param>
    /// <returns>Текст для показа.</returns>
    public static string NameOf(ModelPackageSignature signature) => signature switch
    {
        ModelPackageSignature.Valid => "сошлась с доверенным ключом",
        ModelPackageSignature.Invalid => "не сошлась",
        ModelPackageSignature.NotChecked => "не проверялась",

        // Состояние, записанное более новой версией, не выдаётся за известное:
        // любое из трёх вместо него было бы заявлением о подписи.
        _ => "состояние неизвестно",
    };

    /// <summary>
    /// Называет причину отказа пакета.
    /// </summary>
    /// <param name="rejection">Причина.</param>
    /// <returns>Текст для показа.</returns>
    public static string NameOf(ModelPackageRejection rejection) => rejection switch
    {
        ModelPackageRejection.NotAPackage => "файл не читается как пакет",
        ModelPackageRejection.ManifestUnreadable => "объявление пакета не разбирается",
        ModelPackageRejection.ChecksumsUnreadable => "список хешей не разбирается",
        ModelPackageRejection.SignatureInvalid => "подпись отсутствует или не сошлась",
        ModelPackageRejection.ContentAltered => "хеш файла не совпал с объявленным",
        ModelPackageRejection.CompositionMismatch => "состав пакета не совпадает с объявленным",
        ModelPackageRejection.ApplicationTooOld => "пакет требует более новой версии приложения",
        ModelPackageRejection.IncompatibleContract =>
            "версия предобработки или карты меток приложению неизвестна",

        // Причина, добавленная в перечисление и забытая здесь, не должна
        // притвориться одной из известных.
        _ => "причина " + rejection.ToString(),
    };

    /// <summary>
    /// Дополняет причину уточнением, если оно есть.
    /// </summary>
    /// <param name="detail">Уточнение: имя файла или версия.</param>
    /// <returns>Текст в скобках либо пустая строка.</returns>
    public static string DetailOf(string detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : " (" + detail + ")";

    /// <summary>
    /// Называет исход установки.
    /// </summary>
    /// <param name="installation">Исход.</param>
    /// <returns>Строка для показа.</returns>
    public static ResultRow Describe(ModelPackageInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        return installation.Outcome switch
        {
            ModelInstallOutcome.Installed => new ResultRow
            {
                Text = "Пакет " + installation.ModelVersion + " установлен.",
                Severity = ResultSeverity.Neutral,
            },
            ModelInstallOutcome.AlreadyInstalled => new ResultRow
            {
                Text = "Версия " + installation.ModelVersion + " уже установлена; ничего не изменилось.",
                Note = "Поверх установленной версии не записывается ничего: две разные сборки "
                    + "под одним номером — это молчаливая смена измерительного инструмента. "
                    + "Новая сборка приходит с новым номером.",
                Severity = ResultSeverity.Warning,
            },
            ModelInstallOutcome.Refused => new ResultRow
            {
                Text = "Пакет отвергнут: "
                    + (installation.Rejection is { } rejection ? NameOf(rejection) : "причина не названа")
                    + DetailOf(installation.Detail) + ".",
                Note = "Отказ записан в журнал аудита.",
                Severity = ResultSeverity.Blocking,
            },
            _ => new ResultRow
            {
                Text = "Исход установки неизвестен.",
                Severity = ResultSeverity.Blocking,
            },
        };
    }

    /// <summary>
    /// Называет исход смены действующей версии.
    /// </summary>
    /// <param name="activation">Исход.</param>
    /// <returns>Строка для показа.</returns>
    public static ResultRow Describe(ModelVersionActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);

        return activation.Outcome switch
        {
            ModelActivationOutcome.Activated => new ResultRow
            {
                Text = "Действующей версией стала " + activation.ToVersion
                    + (activation.FromVersion.Length > 0
                        ? " (была " + activation.FromVersion + ")"
                        : string.Empty) + ".",

                // Сказано сразу: иначе администратор увидит в строке «измеряет
                // в этом запуске» прежнее значение и решит, что не получилось.
                Note = "Начнёт действовать со следующего запуска приложения: способ разметки "
                    + "собирается один раз при запуске, и отчёт не может сослаться на версию, "
                    + "которой он не получен (ADR 0008).",
                Severity = ResultSeverity.Neutral,
            },
            ModelActivationOutcome.AlreadyActive => new ResultRow
            {
                Text = "Версия " + activation.ToVersion + " уже помечена действующей.",
                Severity = ResultSeverity.Neutral,
            },
            ModelActivationOutcome.NotInstalled => new ResultRow
            {
                Text = "Версия " + activation.ToVersion + " не установлена.",
                Severity = ResultSeverity.Blocking,
            },
            ModelActivationOutcome.PackageRefused => new ResultRow
            {
                Text = "Пакет версии " + activation.ToVersion + " проверку не прошёл: "
                    + (activation.Rejection is { } rejection ? NameOf(rejection) : "причина не названа")
                    + DetailOf(activation.Detail) + ".",
                Note = "Метка осталась на прежней версии, а отказ записан в журнал: "
                    + "подставлять другой пакет молча ADR 0008 запрещает.",
                Severity = ResultSeverity.Blocking,
            },
            _ => new ResultRow
            {
                Text = "Исход переключения неизвестен.",
                Severity = ResultSeverity.Blocking,
            },
        };
    }

    private static ResultRow DescribeActivePackage(ModelInstallationState models)
    {
        if (models.ActiveRejection is { } rejection)
        {
            return new ResultRow
            {
                Text = "Действующий пакет проверку не прошёл: " + NameOf(rejection)
                    + DetailOf(models.ActiveDetail) + ".",

                // Подстановки другого пакета здесь нет намеренно: ADR 0008
                // требует остановиться до решения администратора, а не
                // посчитать молча другим способом.
                Note = "Анализ заблокирован до решения администратора. Выберите другую "
                    + "установленную версию либо установите пакет заново.",
                Severity = ResultSeverity.Blocking,
            };
        }

        var key = models.ActiveManifest?.SigningKeyId;

        return new ResultRow
        {
            Text = "Подпись действующего пакета: " + NameOf(models.ActiveSignature)
                + (string.IsNullOrWhiteSpace(key) ? string.Empty : ", ключ " + key),
            Note = "Отзыв подписи по сети не проверяется (ADR 0004): скомпрометированный "
                + "ключ нейтрализуется обновлением приложения.",
            Severity = models.ActiveSignature == ModelPackageSignature.Valid
                ? ResultSeverity.Neutral
                : ResultSeverity.Warning,
        };
    }

    private static List<ResultRow> DescribePackage(ModelPackageInspection inspection)
    {
        if (inspection.Rejection is { } rejection)
        {
            return
            [
                new ResultRow
                {
                    Text = "Пакет проверку не прошёл: " + NameOf(rejection)
                        + DetailOf(inspection.Detail) + ".",
                    Note = "Отказ записан в журнал аудита. Установить такой пакет нельзя: "
                        + "частичная загрузка запрещена, любая непройденная проверка "
                        + "означает отказ целиком (ADR 0004).",
                    Severity = ResultSeverity.Blocking,
                },
                new ResultRow
                {
                    Text = "Подпись: " + NameOf(inspection.Signature) + ".",
                    Severity = ResultSeverity.Neutral,
                },
            ];
        }

        var manifest = inspection.Manifest;

        return
        [
            new ResultRow
            {
                Text = "Версия модели: " + (manifest?.ModelVersion ?? "не прочитана"),
                Severity = ResultSeverity.Neutral,
            },
            new ResultRow
            {
                Text = "Подпись: " + NameOf(inspection.Signature)
                    + (string.IsNullOrWhiteSpace(manifest?.SigningKeyId)
                        ? string.Empty
                        : ", ключ " + manifest.SigningKeyId),
                Note = "Открытый ключ взят из установки, а не из пакета: ключ из проверяемого "
                    + "файла проверял бы только то, что файл подписан сам собой (ADR 0004).",
                Severity = ResultSeverity.Neutral,
            },
            new ResultRow
            {
                Text = "Предобработка: " + (manifest?.PreprocessingVersion ?? "не прочитана")
                    + " · карта меток: " + (manifest?.LabelMapVersion ?? "не прочитана"),
                Severity = ResultSeverity.Neutral,
            },
        ];
    }

    private static List<ResultRow> DescribeDifferences(
        ModelPackageManifest incoming,
        ModelPackageManifest? active)
    {
        if (active is null)
        {
            return
            [
                new ResultRow
                {
                    Text = "Действующей версии нет: сравнивать не с чем.",
                    Severity = ResultSeverity.Neutral,
                },
            ];
        }

        var rows = new List<ResultRow>();

        Compare(rows, "Версия модели", active.ModelVersion, incoming.ModelVersion);
        Compare(rows, "Предобработка", active.PreprocessingVersion, incoming.PreprocessingVersion);
        Compare(rows, "Карта меток", active.LabelMapVersion, incoming.LabelMapVersion);
        Compare(
            rows,
            "Наименьшая версия приложения",
            active.MinimumApplicationVersion,
            incoming.MinimumApplicationVersion);
        Compare(rows, "Ключ подписи", active.SigningKeyId, incoming.SigningKeyId);
        Compare(rows, "Версия формата пакета", active.FormatVersion, incoming.FormatVersion);

        if (rows.Count == 0)
        {
            // Совпадение всех полей объявления не означает совпадения весов:
            // одинаковые номера при разных байтах — ровно то, что запрещает
            // переустановку поверх установленной версии.
            rows.Add(new ResultRow
            {
                Text = "Объявление совпадает с действующим во всех полях.",
                Severity = ResultSeverity.Neutral,
            });
        }

        return rows;
    }

    private static void Compare(List<ResultRow> rows, string what, string active, string incoming)
    {
        if (string.Equals(active, incoming, StringComparison.Ordinal))
        {
            return;
        }

        rows.Add(new ResultRow
        {
            Text = what + ": " + active + " → " + incoming,

            // Смена ключа подписи выделена: это смена того, кто отвечает за
            // пакет, а не очередная версия модели.
            Severity = string.Equals(what, "Ключ подписи", StringComparison.Ordinal)
                ? ResultSeverity.Warning
                : ResultSeverity.Neutral,
        });
    }
}
