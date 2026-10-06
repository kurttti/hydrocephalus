using System.Security.Cryptography;
using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Infrastructure.Models;

/// <summary>
/// Чтение пакета модели с проверкой подписи.
///
/// Доверенный ключ, версия приложения и перечень понятных версий задаются при
/// сборке объекта и наружу не выходят: сценарий установки не может подставить
/// свой ключ, а значит и собственный пакет.
/// </summary>
public sealed class SignedModelPackageReader : IModelPackageReader, IDisposable
{
    private readonly ECDsa trustedPublicKey;
    private readonly Version applicationVersion;
    private readonly IReadOnlyCollection<string> knownPreprocessing;
    private readonly IReadOnlyCollection<string> knownLabelMaps;

    /// <summary>
    /// Создаёт читателя.
    /// </summary>
    /// <param name="trustedPublicKeyPem">Доверенный открытый ключ в формате PEM.</param>
    /// <param name="applicationVersion">Версия приложения.</param>
    /// <param name="knownPreprocessing">Версии предобработки, которые умеет приложение.</param>
    /// <param name="knownLabelMaps">Версии карт меток, которые умеет приложение.</param>
    public SignedModelPackageReader(
        string trustedPublicKeyPem,
        Version applicationVersion,
        IReadOnlyCollection<string> knownPreprocessing,
        IReadOnlyCollection<string> knownLabelMaps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPublicKeyPem);
        ArgumentNullException.ThrowIfNull(applicationVersion);
        ArgumentNullException.ThrowIfNull(knownPreprocessing);
        ArgumentNullException.ThrowIfNull(knownLabelMaps);

        this.trustedPublicKey = ECDsa.Create();
        this.trustedPublicKey.ImportFromPem(trustedPublicKeyPem);
        this.applicationVersion = applicationVersion;
        this.knownPreprocessing = knownPreprocessing;
        this.knownLabelMaps = knownLabelMaps;
    }

    /// <inheritdoc />
    public ModelPackageCheck Verify(string packagePath) => ModelPackage.Verify(
        packagePath,
        this.trustedPublicKey,
        this.applicationVersion,
        this.knownPreprocessing,
        this.knownLabelMaps);

    /// <summary>Освобождает ключ.</summary>
    public void Dispose() => this.trustedPublicKey.Dispose();
}
