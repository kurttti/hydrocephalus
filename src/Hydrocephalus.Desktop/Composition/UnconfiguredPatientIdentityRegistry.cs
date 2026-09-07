using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Заглушка внешнего реестра идентификаторов пациента.
///
/// Реестр живёт вне приложения и в этой установке не подключён. Заглушка
/// отвечает «не знаю» на любой псевдоним, и клинический вариант экспорта
/// поэтому не состоится: <c>ExportReportUseCase</c> откажется выдавать файл,
/// у которого клиническое название и обезличенное содержимое.
///
/// Тип назван по своему состоянию, а не по поведению («пустой», «нулевой»):
/// отсутствие реестра — это факт развёртывания, о котором должен знать экран,
/// а не деталь реализации. Сборка сообщает о нём через
/// <see cref="CompositionRoot.PatientRegistryConfigured"/>, чтобы кнопка
/// клинического экспорта была выключена с причиной, а не падала при нажатии.
/// </summary>
internal sealed class UnconfiguredPatientIdentityRegistry : IPatientIdentityRegistry
{
    /// <summary>
    /// Отвечает, что соответствие неизвестно.
    /// </summary>
    /// <param name="pseudonymousStudyId">Псевдонимный идентификатор исследования.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Всегда <see langword="null"/>.</returns>
    public Task<PatientIdentity?> ResolveAsync(
        string pseudonymousStudyId,
        CancellationToken cancellationToken) =>
        Task.FromResult<PatientIdentity?>(null);
}
