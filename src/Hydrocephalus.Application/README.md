# Hydrocephalus.Application

Оркестрация пользовательских сценариев без зависимости от WPF и конкретных инфраструктурных библиотек.

## Планируемые use cases

- `ImportStudy`;
- `SelectSeries`;
- `RunQualityControl`;
- `AnalyzeStudy`;
- `CancelAnalysis`;
- `ReviewMeasurements`;
- `ExportReport`;
- `InstallModelPackage`.

Слой отвечает за последовательность операций, транзакционные границы, проверку прав, отмену и audit events. Медицинские вычисления делегируются `Inference`, а ввод-вывод — портам `Infrastructure`.
