using SmartSearch.Application.Dynamic;

/// <summary>In-memory copy of the seed in Database/004_SmartSearch_SemanticCatalog.sql.</summary>
internal static class TestCatalog
{
    public static SemanticCatalog Build()
    {
        static FieldDefinition F(string e, string code, string name, string col, string type, bool def = false, int sens = 0, string? values = null, string? desc = null, int order = 100) => new()
        {
            EntityCode = e, Code = code, DisplayName = name, ColumnName = col, DataType = type, IsDefault = def, Sensitivity = sens,
            AllowedValues = values?.Split('|') ?? [], Description = desc, SortOrder = order
        };
        static IReadOnlyDictionary<string, FieldDefinition> Fields(params FieldDefinition[] fs) =>
            fs.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);

        var entities = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["Client"] = new()
            {
                Code = "Client", DisplayName = "Client", Description = "A person receiving services (patient). One row per client.",
                SqlSource = "ss.v_Client", KeyColumn = "ClientId", ClientIdColumn = "ClientId", LinkEntityType = "Client", RequiredPermission = "Client.View",
                TitleTemplate = "{Client_FirstName} {Client_LastName}", SubtitleTemplate = "DOB {Client_DateOfBirth:date} · MRN {Client_Mrn}",
                StatementTemplate = "[{Client_FirstName} {Client_LastName}](ClientChart) ~~(DOB {Client_DateOfBirth:date})~~ is **{Client_Status}** in {Client_PrimaryProgram}",
                PrimaryLink = "ClientChart", SecondaryLinks = ["ClientVisits"],
                Fields = Fields(
                    F("Client", "ClientId", "Client ID", "ClientId", "int", order: 1),
                    F("Client", "FullName", "Client name", "FullName", "personName", sens: 1, desc: "Search by any part of the name", order: 5),
                    F("Client", "FirstName", "First name", "FirstName", "text", true, 1, order: 10),
                    F("Client", "LastName", "Last name", "LastName", "text", true, 1, order: 11),
                    F("Client", "DateOfBirth", "Date of birth", "DateOfBirth", "date", true, 1, order: 12),
                    F("Client", "Mrn", "MRN", "Mrn", "code", true, 1, order: 13),
                    F("Client", "Status", "Client status", "ClientStatus", "text", true, values: "Active|Inactive|Discharged", order: 21),
                    F("Client", "PrimaryProgram", "Program", "PrimaryProgram", "text", true, order: 22),
                    F("Client", "PrimaryClinician", "Primary clinician", "PrimaryClinicianName", "text", order: 23),
                    F("Client", "Ssn", "SSN", "Ssn", "text", sens: 2, order: 99))
            },
            ["Visit"] = new()
            {
                Code = "Visit", DisplayName = "Visit", Description = "A delivered clinical service (visit, session, encounter). One row per service.",
                SqlSource = "ss.v_Visit", KeyColumn = "VisitId", ClientIdColumn = "ClientId", DefaultDateField = "VisitDate", RequiresTimeRange = true,
                LinkEntityType = "Visit", RequiredPermission = "Visit.View",
                TitleTemplate = "{Client_FirstName} {Client_LastName}", SubtitleTemplate = "{Visit_VisitDate:datetime} · {Visit_VisitType}",
                StatementTemplate = "[{Client_FirstName} {Client_LastName}](ClientChart) was seen for [{Visit_VisitType}](VisitDetail) by **{Visit_ProviderName}** on {Visit_VisitDate:datetime}",
                PrimaryLink = "VisitDetail", SecondaryLinks = ["ClientChart"],
                Fields = Fields(
                    F("Visit", "VisitDate", "Date of service", "VisitDate", "datetime", true, order: 10),
                    F("Visit", "VisitType", "Service type", "VisitType", "text", true, values: "Individual therapy|Group session|Psychiatric eval|Medication management", order: 11),
                    F("Visit", "ProviderName", "Provider", "ProviderName", "text", true, order: 12),
                    F("Visit", "Status", "Visit status", "VisitStatus", "text", true, values: "Signed|Draft|Completed|No show|Cancelled", order: 13),
                    F("Visit", "Program", "Program", "Program", "text", order: 14),
                    F("Visit", "DurationMinutes", "Duration (minutes)", "DurationMinutes", "int", order: 16),
                    F("Visit", "NoteText", "Progress note text", "NoteText", "text", sens: 2, order: 99))
            },
            ["Diagnosis"] = new()
            {
                Code = "Diagnosis", DisplayName = "Diagnosis", Description = "A diagnosis recorded for a client (ICD-10).",
                SqlSource = "ss.v_Diagnosis", KeyColumn = "DiagnosisId", ClientIdColumn = "ClientId", DefaultDateField = "StartDate",
                LinkEntityType = "Client", RequiredPermission = "Diagnosis.View",
                TitleTemplate = "{Client_FirstName} {Client_LastName}", SubtitleTemplate = "{Diagnosis_Code} {Diagnosis_Description}",
                PrimaryLink = "ClientChart",
                Fields = Fields(
                    F("Diagnosis", "Code", "ICD-10 code", "IcdCode", "code", true, 1, desc: "Use startsWith for a family like F32", order: 10),
                    F("Diagnosis", "Description", "Diagnosis", "IcdDescription", "text", true, 1, order: 11),
                    F("Diagnosis", "StartDate", "Start date", "StartDate", "date", true, order: 12),
                    F("Diagnosis", "EndDate", "End date", "EndDate", "date", order: 13))
            },
            ["Medication"] = new()
            {
                Code = "Medication", DisplayName = "Medication", Description = "A medication prescribed for a client.",
                SqlSource = "ss.v_Medication", KeyColumn = "MedicationOrderId", ClientIdColumn = "ClientId", DefaultDateField = "StartDate",
                LinkEntityType = "Medication", RequiredPermission = "Medication.View",
                TitleTemplate = "{Client_FirstName} {Client_LastName}", SubtitleTemplate = "{Medication_DrugName} {Medication_Strength}",
                StatementTemplate = "[{Client_FirstName} {Client_LastName}](ClientChart) was prescribed [{Medication_DrugName} {Medication_Strength}](MedicationDetail) {Medication_Frequency} by **{Medication_Prescriber}** on {Medication_StartDate:date}",
                PrimaryLink = "MedicationDetail", SecondaryLinks = ["ClientChart"],
                Fields = Fields(
                    F("Medication", "DrugName", "Medication", "DrugName", "text", true, 1, order: 10),
                    F("Medication", "Strength", "Strength", "Strength", "text", true, 1, order: 11),
                    F("Medication", "Frequency", "Frequency", "Frequency", "text", true, order: 12),
                    F("Medication", "Prescriber", "Prescriber", "PrescriberName", "text", true, order: 14),
                    F("Medication", "StartDate", "Start date", "StartDate", "date", true, order: 15),
                    F("Medication", "Status", "Medication status", "OrderStatus", "text", true, values: "Active|Discontinued|Completed|On hold", order: 17))
            },
            ["Charge"] = new()
            {
                Code = "Charge", DisplayName = "Billing charge", Description = "A billed service charge / claim line for a client.",
                SqlSource = "ss.v_Charge", KeyColumn = "ChargeId", ClientIdColumn = "ClientId", DefaultDateField = "ServiceDate", RequiresTimeRange = true,
                DefaultLookbackMonths = 24, LinkEntityType = "Charge", RequiredPermission = "Billing.View",
                TitleTemplate = "{Client_FirstName} {Client_LastName}", SubtitleTemplate = "{Charge_ServiceDate:date} · {Charge_ProcedureCode}",
                PrimaryLink = "ChargeDetail", SecondaryLinks = ["ClientChart"],
                Fields = Fields(
                    F("Charge", "ServiceDate", "Service date", "ServiceDate", "date", true, order: 10),
                    F("Charge", "ProcedureCode", "Procedure code", "ProcedureCode", "code", true, order: 11),
                    F("Charge", "BilledAmount", "Billed amount", "BilledAmount", "decimal", true, order: 12),
                    F("Charge", "Balance", "Balance due", "BalanceDue", "decimal", true, order: 14),
                    F("Charge", "Payer", "Payer", "PayerName", "text", true, order: 15),
                    F("Charge", "ClaimStatus", "Claim status", "ClaimStatus", "text", true, values: "Submitted|Paid|Denied", order: 16))
            },
            ["Staff"] = new()
            {
                Code = "Staff", DisplayName = "Staff", Description = "An employee or clinician.",
                SqlSource = "ss.v_Staff", KeyColumn = "StaffId", RequiredPermission = "Staff.View",
                TitleTemplate = "{Staff_FullName}", SubtitleTemplate = "{Staff_Role}",
                Fields = Fields(
                    F("Staff", "FullName", "Staff name", "FullName", "text", true, order: 10),
                    F("Staff", "Role", "Role", "RoleName", "text", true, order: 11))
            }
        };

        return new SemanticCatalog
        {
            Entities = entities,
            Relations =
            [
                new("Visit", "Client", "ManyToOne", "ClientId", "ClientId"),
                new("Diagnosis", "Client", "ManyToOne", "ClientId", "ClientId"),
                new("Visit", "Staff", "ManyToOne", "ProviderStaffId", "StaffId"),
                new("Client", "Visit", "OneToMany", "ClientId", "ClientId"),
                new("Client", "Diagnosis", "OneToMany", "ClientId", "ClientId"),
                new("Staff", "Visit", "OneToMany", "StaffId", "ProviderStaffId"),
                new("Medication", "Client", "ManyToOne", "ClientId", "ClientId"),
                new("Charge", "Client", "ManyToOne", "ClientId", "ClientId"),
                new("Client", "Medication", "OneToMany", "ClientId", "ClientId"),
                new("Client", "Charge", "OneToMany", "ClientId", "ClientId"),
            ],
            Synonyms =
            [
                new("patient", "Entity", "Client"), new("visit", "Entity", "Visit"), new("session", "Entity", "Visit"),
                new("no shows", "Value", "Visit.Status=No show"), new("no show", "Value", "Visit.Status=No show"),
                new("diagnosis", "Entity", "Diagnosis"), new("provider", "Entity", "Staff"),
                new("medication", "Entity", "Medication"), new("meds", "Entity", "Medication"),
                new("billing", "Entity", "Charge"), new("bill", "Entity", "Charge"), new("claim", "Entity", "Charge"),
            ]
        };
    }
}
