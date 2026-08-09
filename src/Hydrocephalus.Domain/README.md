# Hydrocephalus.Domain

Чистая доменная модель без зависимостей от UI, DICOM, ONNX и файловой системы.

## Предлагаемые сущности

- `ImagingStudy`, `ImagingSeries`, `SeriesGeometry`;
- `QualityAssessment`, `QualityIssue`, `RefusalReason`;
- `SegmentationResult`, `AnatomicalLabel`;
- `Biomarker`, `MeasurementMethod`, `MeasurementQuality`;
- `Prediction`, `DiagnosticClass`, `Uncertainty`;
- `ModelIdentity`, `PipelineIdentity`, `AnalysisReport`.

## Инварианты

- прогноз невозможен без успешного QC;
- каждое измерение связано с методом, версией и единицей;
- каждый прогноз связан с моделью и перечнем поддерживаемых классов;
- отказ от ответа не преобразуется в отрицательный диагноз;
- ручное исправление врача хранится отдельно от вывода модели.
