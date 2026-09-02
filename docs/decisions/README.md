# Журнал решений

Архитектурные решения (ADR) добавляются отдельными Markdown-файлами по шаблону `NNNN-краткое-название.md`.

## Шаблон

```markdown
# ADR NNNN: Название

- Статус: proposed | accepted | superseded
- Дата: YYYY-MM-DD
- Владельцы: роли/команда

## Контекст
## Решение
## Рассмотренные варианты
## Последствия и риски
## План проверки
```

## Журнал

| ADR | Тема | Статус |
|---|---|---|
| [0001](0001-dotnet-wpf.md) | WPF и поддерживаемая версия .NET LTS | proposed |
| [0002](0002-inference-hosting.md) | In-process ONNX или изолированный локальный worker | proposed |
| [0003](0003-dicom-library-deid-profile.md) | DICOM-библиотека и профиль деидентификации | proposed |
| [0004](0004-model-package-signing.md) | Формат model package и подпись | proposed |
| [0005](0005-report-format.md) | Формат структурированного отчёта | proposed |
| [0006](0006-local-encryption-retention.md) | Локальное шифрование и политика удаления | proposed |
| [0007](0007-viewer-rendering.md) | Визуализатор серий и оверлеев | proposed |
| [0008](0008-model-update-rollback.md) | Стратегия обновления model package и rollback | proposed |

Все восемь находятся в статусе `proposed` и требуют утверждения до старта M1: ADR 0003 и 0006 дополнительно проходят privacy review, ADR 0005, 0007 и 0008 — согласование с клинической командой.

ADR 0001–0006 закрывают список первых решений из этого файла, ADR 0007 и 0008 — оставшиеся пункты раздела «Открытые решения» в [архитектуре](../architecture/README.md).
