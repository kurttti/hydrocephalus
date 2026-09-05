using System.Buffers.Binary;
using FellowOakDicom;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Профиль деидентификации: какие теги удаляются, какие сохраняются и какие
/// замещаются. Основа — DICOM PS3.15 Basic Application Level Confidentiality Profile
/// с опциями, зафиксированными в ADR 0003.
///
/// Теги перечислены числовыми номерами группы и элемента, а не именами библиотеки:
/// профиль — это таблица стандарта, и она должна читаться рядом с PS3.15, а также
/// не зависеть от переименований в fo-dicom.
/// </summary>
internal static class DeidentificationProfile
{
    /// <summary>
    /// Наибольший сдвиг дат в днях. Сдвиг всегда в прошлое и всегда кратен суткам,
    /// поэтому интервалы между исследованиями одного пациента сохраняются точно
    /// (Retain Longitudinal Temporal Information, ADR 0003).
    /// </summary>
    private const int MaxDateShiftDays = 3650;

    /// <summary>
    /// Теги, удаляемые безусловно: прямые идентификаторы пациента, сведения об
    /// учреждении и персонале, номера направлений и свободный текст.
    ///
    /// Отдельно отмечено: SeriesDescription (0008,103E) и ProtocolName (0018,1030)
    /// удаляются, хотя по ним определяется взвешенность и постконтрастность серии.
    /// Распознавание выполняется на исходном наборе тегов до деидентификации, а его
    /// результат живёт в доменной модели, поэтому свободный текст производителя
    /// не обязан попадать в рабочую копию.
    /// </summary>
    private static readonly DicomTag[] RemovedTags =
    [
        new(0x0008, 0x0050), // AccessionNumber
        new(0x0008, 0x0080), // InstitutionName
        new(0x0008, 0x0081), // InstitutionAddress
        new(0x0008, 0x0082), // InstitutionCodeSequence
        new(0x0008, 0x0090), // ReferringPhysicianName
        new(0x0008, 0x0092), // ReferringPhysicianAddress
        new(0x0008, 0x0094), // ReferringPhysicianTelephoneNumbers
        new(0x0008, 0x0096), // ReferringPhysicianIdentificationSequence
        new(0x0008, 0x1010), // StationName
        new(0x0008, 0x1030), // StudyDescription
        new(0x0008, 0x103E), // SeriesDescription
        new(0x0008, 0x1040), // InstitutionalDepartmentName
        new(0x0008, 0x1048), // PhysiciansOfRecord
        new(0x0008, 0x1049), // PhysiciansOfRecordIdentificationSequence
        new(0x0008, 0x1050), // PerformingPhysicianName
        new(0x0008, 0x1052), // PerformingPhysicianIdentificationSequence
        new(0x0008, 0x1060), // NameOfPhysiciansReadingStudy
        new(0x0008, 0x1062), // PhysiciansReadingStudyIdentificationSequence
        new(0x0008, 0x1070), // OperatorsName
        new(0x0008, 0x1072), // OperatorIdentificationSequence
        new(0x0008, 0x1080), // AdmittingDiagnosesDescription
        new(0x0008, 0x1084), // AdmittingDiagnosesCodeSequence
        new(0x0008, 0x2111), // DerivationDescription
        new(0x0008, 0x4000), // IdentifyingComments
        new(0x0010, 0x0010), // PatientName
        new(0x0010, 0x0020), // PatientID
        new(0x0010, 0x0021), // IssuerOfPatientID
        new(0x0010, 0x0030), // PatientBirthDate
        new(0x0010, 0x0032), // PatientBirthTime
        new(0x0010, 0x0040), // PatientSex
        new(0x0010, 0x1000), // OtherPatientIDs
        new(0x0010, 0x1001), // OtherPatientNames
        new(0x0010, 0x1002), // OtherPatientIDsSequence
        new(0x0010, 0x1005), // PatientBirthName
        new(0x0010, 0x1010), // PatientAge
        new(0x0010, 0x1020), // PatientSize
        new(0x0010, 0x1030), // PatientWeight
        new(0x0010, 0x1040), // PatientAddress
        new(0x0010, 0x1060), // PatientMotherBirthName
        new(0x0010, 0x1080), // MilitaryRank
        new(0x0010, 0x1081), // BranchOfService
        new(0x0010, 0x1090), // MedicalRecordLocator
        new(0x0010, 0x2000), // MedicalAlerts
        new(0x0010, 0x2110), // Allergies
        new(0x0010, 0x2150), // CountryOfResidence
        new(0x0010, 0x2152), // RegionOfResidence
        new(0x0010, 0x2154), // PatientTelephoneNumbers
        new(0x0010, 0x2160), // EthnicGroup
        new(0x0010, 0x2180), // Occupation
        new(0x0010, 0x21A0), // SmokingStatus
        new(0x0010, 0x21B0), // AdditionalPatientHistory
        new(0x0010, 0x21C0), // PregnancyStatus
        new(0x0010, 0x21D0), // LastMenstrualDate
        new(0x0010, 0x21F0), // PatientReligiousPreference
        new(0x0010, 0x4000), // PatientComments
        new(0x0018, 0x1000), // DeviceSerialNumber
        new(0x0018, 0x1030), // ProtocolName
        new(0x0018, 0x1400), // AcquisitionDeviceProcessingDescription
        new(0x0020, 0x0010), // StudyID
        new(0x0020, 0x4000), // ImageComments
        new(0x0032, 0x1032), // RequestingPhysician
        new(0x0032, 0x1033), // RequestingService
        new(0x0032, 0x1060), // RequestedProcedureDescription
        new(0x0032, 0x4000), // StudyComments
        new(0x0038, 0x0010), // AdmissionID
        new(0x0038, 0x0011), // IssuerOfAdmissionID
        new(0x0038, 0x0020), // AdmittingDate
        new(0x0038, 0x0021), // AdmittingTime
        new(0x0038, 0x0300), // CurrentPatientLocation
        new(0x0038, 0x0400), // PatientInstitutionResidence
        new(0x0038, 0x0500), // PatientState
        new(0x0038, 0x4000), // VisitComments
        new(0x0040, 0x0241), // PerformedStationAETitle
        new(0x0040, 0x0242), // PerformedStationName
        new(0x0040, 0x0243), // PerformedLocation
        new(0x0040, 0x0253), // PerformedProcedureStepID
        new(0x0040, 0x0254), // PerformedProcedureStepDescription
        new(0x0040, 0x0275), // RequestAttributesSequence
        new(0x0040, 0x1001), // RequestedProcedureID
        new(0x0040, 0x1400), // RequestedProcedureComments
        new(0x0040, 0x2016), // PlacerOrderNumberImagingServiceRequest
        new(0x0040, 0x2017), // FillerOrderNumberImagingServiceRequest
        new(0x0040, 0x3001), // ConfidentialityConstraintOnPatientDataDescription
        new(0x0040, 0xA123), // PersonName
        new(0x0040, 0xA730), // ContentSequence
        new(0x0070, 0x0084), // ContentCreatorName
        new(0x0088, 0x0904), // TopicTitle
        new(0x0088, 0x0906), // TopicSubject

        // OriginalAttributesSequence хранит значения тегов до предыдущей модификации,
        // то есть может нести исходные идентификаторы дословно.
        new(0x0400, 0x0561),
    ];

    /// <summary>
    /// Теги, которые профиль обязан сохранить. ADR 0003 сохраняет идентичность
    /// оборудования ради подгрупповой отчётности (docs/validation/README.md)
    /// и перечисляет при этом ровно три атрибута: производитель, модель сканера
    /// и напряжённость поля. Реализован этот перечень, а не полная опция Retain
    /// Device Identity: серийный номер аппарата и имя станции связывают серию
    /// с конкретным рабочим местом и потому удаляются.
    /// </summary>
    private static readonly DicomTag[] RetainedTags =
    [
        new(0x0008, 0x0070), // Manufacturer
        new(0x0008, 0x1090), // ManufacturerModelName
        new(0x0018, 0x0087), // MagneticFieldStrength
        new(0x0018, 0x1020), // SoftwareVersions
    ];

    /// <summary>
    /// UID, которые нельзя замещать: они называют не экземпляр данных, а класс
    /// сервиса, синтаксис передачи или реализацию библиотеки. Их замена сделала бы
    /// файл нечитаемым и при этом ничего не скрыла.
    /// </summary>
    private static readonly DicomTag[] PreservedUidTags =
    [
        new(0x0002, 0x0002), // MediaStorageSOPClassUID
        new(0x0002, 0x0010), // TransferSyntaxUID
        new(0x0002, 0x0012), // ImplementationClassUID
        new(0x0008, 0x0016), // SOPClassUID
        new(0x0008, 0x1150), // ReferencedSOPClassUID
    ];

    private static readonly HashSet<DicomTag> Removed = [.. RemovedTags];

    private static readonly HashSet<DicomTag> PreservedUids = [.. PreservedUidTags];

    /// <summary>Теги, удаляемые профилем.</summary>
    internal static IReadOnlyCollection<DicomTag> RemovedByProfile => Removed;

    /// <summary>Теги, которые профиль обязан сохранить.</summary>
    internal static IReadOnlyList<DicomTag> RetainedByProfile => RetainedTags;

    /// <summary>Проверяет, удаляется ли тег профилем.</summary>
    /// <param name="tag">Проверяемый тег.</param>
    /// <returns><see langword="true"/>, если тег подлежит удалению.</returns>
    internal static bool IsRemoved(DicomTag tag) => Removed.Contains(tag);

    /// <summary>
    /// Проверяет, относится ли тег к группам, удаляемым целиком: приватным,
    /// оверлейным и кривым.
    ///
    /// Приватные теги удаляются по умолчанию (ADR 0003). Оверлеи удаляются,
    /// потому что оверлейный слой — такой же носитель вписанного текста, как
    /// и пиксели, а тег BurnedInAnnotation его не описывает.
    /// </summary>
    /// <param name="tag">Проверяемый тег.</param>
    /// <returns><see langword="true"/>, если группа тега удаляется целиком.</returns>
    internal static bool IsInRemovedGroup(DicomTag tag) =>
        (tag.Group & 1) == 1
        || tag.Group is >= 0x5000 and <= 0x50FF
        || tag.Group is >= 0x6000 and <= 0x60FF;

    /// <summary>Проверяет, подлежит ли UID в этом теге замещению.</summary>
    /// <param name="tag">Проверяемый тег.</param>
    /// <returns><see langword="true"/>, если UID нужно заместить.</returns>
    internal static bool IsRemappedUid(DicomTag tag) => !PreservedUids.Contains(tag);

    /// <summary>
    /// Вычисляет сдвиг дат для пациента.
    ///
    /// Сдвиг привязан к псевдониму пациента, а не к исследованию: одинаковый сдвиг
    /// на всех исследованиях одного пациента сохраняет интервалы между ними, без
    /// которых невозможно пред/послеоперационное сравнение.
    /// </summary>
    /// <param name="salt">Соль псевдонимизации.</param>
    /// <param name="pseudonymousSubjectId">Псевдонимный идентификатор пациента.</param>
    /// <returns>Отрицательное число дней.</returns>
    internal static int DeriveDayShift(string salt, string pseudonymousSubjectId)
    {
        var bytes = Convert.FromHexString(
            Pseudonyms.Derive(salt, "date-shift", pseudonymousSubjectId));

        var offset = BinaryPrimitives.ReadUInt32BigEndian(bytes) % MaxDateShiftDays;

        return -(int)(offset + 1);
    }
}
