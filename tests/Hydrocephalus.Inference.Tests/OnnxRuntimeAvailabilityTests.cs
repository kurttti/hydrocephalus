using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Загружается ли движок вывода в этой среде.
///
/// Проверка отдельная и намеренно простая: нативные библиотеки ONNX Runtime
/// попадают в поставку (ADR 0002), а Smart App Control на этой машине уже
/// блокировал сборки — отказ движка надо отличать от ошибки в коде конвейера.
///
/// Веса в репозиторий не входят и CI их не качает, поэтому проверка с настоящей
/// моделью включается переменной среды и без неё пропускается.
/// </summary>
public sealed class OnnxRuntimeAvailabilityTests
{
    private const string ModelPathVariable = "HYDROCEPHALUS_VENTRICLE_ONNX";

    [Fact]
    public void The_native_runtime_loads()
    {
        // Создание настроек трогает нативную часть: если библиотека не грузится,
        // падает здесь, а не в середине сегментации.
        using var options = new SessionOptions();

        Assert.NotNull(OrtEnv.Instance());
    }

    [Fact]
    public void The_published_model_runs_on_one_slice_stack()
    {
        var path = Environment.GetEnvironmentVariable(ModelPathVariable);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // Весов нет — проверять нечего. Это не провал: репозиторий их не
            // содержит по лицензионным причинам (ADR 0009).
            return;
        }

        using var session = new InferenceSession(path);

        // Объявленные оси модель оставляет переменными (-1), хотя граф запечён
        // под пачку в единицу: экспорт ушёл в запасной путь через TorchScript
        // (docs/data/README.md). Проверяется поэтому работа, а не объявление.
        var input = session.InputMetadata.Single();
        var tensor = new DenseTensor<float>([1, 7, 256, 256]);

        using var results = session.Run(
            [NamedOnnxValue.CreateFromTensor(input.Key, tensor)]);

        var logits = results.Single().AsTensor<float>();

        // Классов столько же, сколько у исходной модели.
        Assert.Equal(79, logits.Dimensions[1]);
    }
}
