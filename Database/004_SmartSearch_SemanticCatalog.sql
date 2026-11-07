/* =====================================================================
   EHR Smart Search - SEMANTIC CATALOG for dynamic queries
   Run after 001-003.

   The catalog is the ONLY thing the dynamic query path can see:
     - Bedrock gets entity / field names and descriptions from here (never table names, never data)
     - The validator accepts only entities, fields, relations and operators listed here
     - The SQL compiler reads the real view / column names from here

   Each entity points to a CURATED VIEW (schema ss) that the team builds over the legacy
   tables. That keeps 20 years of table history, renamed columns and soft-delete rules out of
   the AI's sight, and lets you index / tune each view independently.

   Every curated view MUST expose a TenantId column. The compiler always adds
   "TenantId = @tenant" and the client-access filter itself; the model cannot remove them.
   ===================================================================== */

CREATE TABLE ss.SemEntity
(
    EntityCode            varchar(40)    NOT NULL CONSTRAINT PK_SemEntity PRIMARY KEY,   -- e.g. Visit
    DisplayName           nvarchar(80)   NOT NULL,
    Description           nvarchar(500)  NOT NULL,      -- shown to Bedrock; say what a row IS
    SqlSource             varchar(128)   NOT NULL,      -- curated view, e.g. ss.v_Visit
    KeyColumn             varchar(64)    NOT NULL,      -- row id used for links, e.g. VisitId
    ClientIdColumn        varchar(64)    NULL,          -- set when rows belong to a client (enables access filter, name search, exists-joins)
    DefaultDateField      varchar(64)    NULL,          -- FieldCode used for time ranges
    RequiresTimeRange     bit            NOT NULL CONSTRAINT DF_SemEntity_RequiresTime DEFAULT (0),
    DefaultLookbackMonths smallint       NOT NULL CONSTRAINT DF_SemEntity_Lookback DEFAULT (12),
    MaxLookbackMonths     smallint       NOT NULL CONSTRAINT DF_SemEntity_MaxLookback DEFAULT (240),
    DefaultLimit          smallint       NOT NULL CONSTRAINT DF_SemEntity_DefaultLimit DEFAULT (50),
    MaxLimit              smallint       NOT NULL CONSTRAINT DF_SemEntity_MaxLimit DEFAULT (500),
    LinkEntityType        varchar(32)    NULL,          -- ILinkProvider.EntityType for links
    RequiredPermission    varchar(100)   NULL,
    IsConsentRestricted   bit            NOT NULL CONSTRAINT DF_SemEntity_Consent DEFAULT (0),  -- e.g. 42 CFR Part 2 data: extra consent check
    TitleTemplate         nvarchar(200)  NOT NULL,      -- placeholders use {Entity_Field}
    SubtitleTemplate      nvarchar(200)  NULL,
    StatementTemplate     nvarchar(400)  NULL,          -- Statements layout sentence
    PrimaryLink           varchar(64)    NULL,
    SecondaryLinksCsv     varchar(200)   NULL,
    IsEnabled             bit            NOT NULL CONSTRAINT DF_SemEntity_IsEnabled DEFAULT (1)
);
GO

CREATE TABLE ss.SemField
(
    EntityCode     varchar(40)    NOT NULL CONSTRAINT FK_SemField_Entity REFERENCES ss.SemEntity (EntityCode),
    FieldCode      varchar(64)    NOT NULL,             -- e.g. VisitDate
    DisplayName    nvarchar(80)   NOT NULL,
    Description    nvarchar(300)  NULL,
    ColumnName     varchar(128)   NOT NULL,             -- column in SqlSource view
    DataType       varchar(12)    NOT NULL CONSTRAINT CK_SemField_Type
                   CHECK (DataType IN ('text','code','int','decimal','date','datetime','bool','personName')),
    IsFilterable   bit            NOT NULL CONSTRAINT DF_SemField_Filter DEFAULT (1),
    IsSelectable   bit            NOT NULL CONSTRAINT DF_SemField_Select DEFAULT (1),
    IsSortable     bit            NOT NULL CONSTRAINT DF_SemField_Sort DEFAULT (1),
    IsDefault      bit            NOT NULL CONSTRAINT DF_SemField_Default DEFAULT (0),  -- shown when the plan selects nothing
    Sensitivity    tinyint        NOT NULL CONSTRAINT DF_SemField_Sensitivity DEFAULT (0)
                   CONSTRAINT CK_SemField_Sensitivity CHECK (Sensitivity IN (0,1,2)),
                   -- 0 normal, 1 PHI (allowed, audited), 2 restricted (never exposed: SSN, note text...)
    AllowedValues  nvarchar(1000) NULL,                 -- pipe list shown to Bedrock: Signed|Draft|No show
    SortOrder      smallint       NOT NULL CONSTRAINT DF_SemField_SortOrder DEFAULT (100),
    CONSTRAINT PK_SemField PRIMARY KEY (EntityCode, FieldCode)
);
GO

/* Joins the compiler may use.
   ManyToOne  (Visit -> Client): fields of the target can be selected / filtered directly.
   OneToMany  (Client -> Visit): only via exists / notExists / minCount, so rows never multiply. */
CREATE TABLE ss.SemRelation
(
    FromEntity   varchar(40)  NOT NULL CONSTRAINT FK_SemRelation_From REFERENCES ss.SemEntity (EntityCode),
    ToEntity     varchar(40)  NOT NULL CONSTRAINT FK_SemRelation_To   REFERENCES ss.SemEntity (EntityCode),
    Cardinality  varchar(10)  NOT NULL CONSTRAINT CK_SemRelation_Card CHECK (Cardinality IN ('ManyToOne','OneToMany')),
    FromColumn   varchar(128) NOT NULL,
    ToColumn     varchar(128) NOT NULL,
    CONSTRAINT PK_SemRelation PRIMARY KEY (FromEntity, ToEntity)
);
GO

/* Words users say -> catalog names. Used to pick which entities go into the prompt. */
CREATE TABLE ss.SemSynonym
(
    Term        nvarchar(60)  NOT NULL,
    TargetType  varchar(8)    NOT NULL CONSTRAINT CK_SemSynonym_Type CHECK (TargetType IN ('Entity','Field','Value')),
    Target      varchar(140)  NOT NULL,   -- 'Visit' | 'Visit.Status' | 'Visit.Status=No show'
    CONSTRAINT PK_SemSynonym PRIMARY KEY (Term, TargetType, Target)
);
GO

/* Verified question -> plan pairs. Used as few-shot examples in the prompt and as a regression test set.
   Promote good dynamic queries from ss.QueryLog into here. */
CREATE TABLE ss.SemExample
(
    ExampleId    int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_SemExample PRIMARY KEY,
    Question     nvarchar(500)  NOT NULL,
    PlanJson     nvarchar(max)  NOT NULL CONSTRAINT CK_SemExample_Json CHECK (ISJSON(PlanJson) = 1),
    EntitiesCsv  varchar(200)   NOT NULL,     -- entities the example touches, for retrieval
    IsVerified   bit            NOT NULL CONSTRAINT DF_SemExample_Verified DEFAULT (0),
    Embedding    varbinary(max) NULL,         -- optional: Titan embedding for semantic retrieval
    CreatedAt    datetime2(3)   NOT NULL CONSTRAINT DF_SemExample_CreatedAt DEFAULT (sysutcdatetime())
);
GO

/* Dynamic plans are logged in ss.QueryLog with IntentCode = 'Dynamic' and the plan in IntentJson. */
IF NOT EXISTS (SELECT 1 FROM ss.IntentDefinition WHERE IntentCode = 'Dynamic')
    INSERT ss.IntentDefinition (IntentCode, DisplayName, Description, OwningModule, PrimaryEntityType, RequiredPermission)
    VALUES ('Dynamic', N'Dynamic query', N'Any question not covered by a predefined intent. Answered with a validated query plan over the semantic catalog.',
            'SmartSearch', 'Any', 'SmartSearch.Dynamic');
GO

ALTER TABLE ss.QueryLog ADD SqlHash binary(32) NULL, RowsReturned int NULL, DbLatencyMs int NULL, PlanVersion smallint NULL;
GO

/* =====================================================================
   SEED - starter catalog. Adjust view / column names to your curated views.
   ===================================================================== */
INSERT ss.SemEntity (EntityCode, DisplayName, Description, SqlSource, KeyColumn, ClientIdColumn, DefaultDateField, RequiresTimeRange,
                     DefaultLookbackMonths, LinkEntityType, RequiredPermission, TitleTemplate, SubtitleTemplate, StatementTemplate,
                     PrimaryLink, SecondaryLinksCsv)
VALUES
('Client', N'Client', N'A person receiving services (patient). One row per client.',
 'ss.v_Client', 'ClientId', 'ClientId', NULL, 0, 12, 'Client', 'Client.View',
 N'{Client_FirstName} {Client_LastName}', N'DOB {Client_DateOfBirth:date} · MRN {Client_Mrn}',
 N'[{Client_FirstName} {Client_LastName}](ClientChart) ~~(DOB {Client_DateOfBirth:date})~~ is **{Client_Status}** in {Client_PrimaryProgram}',
 'ClientChart', 'ClientVisits'),

('Visit', N'Visit', N'A delivered or scheduled clinical service (visit, session, encounter, appointment that happened). One row per service.',
 'ss.v_Visit', 'VisitId', 'ClientId', 'VisitDate', 1, 12, 'Visit', 'Visit.View',
 N'{Client_FirstName} {Client_LastName}', N'{Visit_VisitDate:datetime} · {Visit_VisitType}',
 N'[{Client_FirstName} {Client_LastName}](ClientChart) was seen for [{Visit_VisitType}](VisitDetail) by **{Visit_ProviderName}** on {Visit_VisitDate:datetime}',
 'VisitDetail', 'ClientChart'),

('Diagnosis', N'Diagnosis', N'A diagnosis recorded for a client (ICD-10 code), with start / end dates. One row per diagnosis record.',
 'ss.v_Diagnosis', 'DiagnosisId', 'ClientId', 'StartDate', 0, 12, 'Client', 'Diagnosis.View',
 N'{Client_FirstName} {Client_LastName}', N'{Diagnosis_Code} {Diagnosis_Description}',
 N'[{Client_FirstName} {Client_LastName}](ClientChart) has **{Diagnosis_Code}** {Diagnosis_Description} since {Diagnosis_StartDate:date}',
 'ClientChart', NULL),

('Appointment', N'Appointment', N'A future or past scheduled appointment slot (not the delivered service). One row per appointment.',
 'ss.v_Appointment', 'AppointmentId', 'ClientId', 'StartTime', 1, 3, 'Appointment', 'Schedule.View',
 N'{Client_FirstName} {Client_LastName}', N'{Appointment_StartTime:datetime} · {Appointment_Status}',
 N'[{Client_FirstName} {Client_LastName}](ClientChart) has an appointment with **{Appointment_StaffName}** on [{Appointment_StartTime:datetime}](AppointmentDetail)',
 'AppointmentDetail', 'ClientChart'),

('Staff', N'Staff', N'An employee or clinician (provider, therapist, case manager).',
 'ss.v_Staff', 'StaffId', NULL, NULL, 0, 12, NULL, 'Staff.View',
 N'{Staff_FullName}', N'{Staff_Role}', NULL, NULL, NULL),

('Medication', N'Medication', N'A medication prescribed / ordered for a client (drug, dose, frequency, prescriber, start and stop dates). One row per medication order.',
 'ss.v_Medication', 'MedicationOrderId', 'ClientId', 'StartDate', 0, 12, 'Medication', 'Medication.View',
 N'{Client_FirstName} {Client_LastName}', N'{Medication_DrugName} {Medication_Strength}',
 N'[{Client_FirstName} {Client_LastName}](ClientChart) was prescribed [{Medication_DrugName} {Medication_Strength}](MedicationDetail) {Medication_Frequency} by **{Medication_Prescriber}** on {Medication_StartDate:date} ~~({Medication_Status})~~',
 'MedicationDetail', 'ClientChart'),

('Charge', N'Billing charge', N'A billed service charge / claim line for a client: billed amount, payer, claim status, balance. One row per charge. Use for billing, claims, invoices, balances.',
 'ss.v_Charge', 'ChargeId', 'ClientId', 'ServiceDate', 1, 24, 'Charge', 'Billing.View',
 N'{Client_FirstName} {Client_LastName}', N'{Charge_ServiceDate:date} · {Charge_ProcedureCode}',
 N'[{Client_FirstName} {Client_LastName}](ClientChart) was billed **{Charge_BilledAmount}** to {Charge_Payer} for [{Charge_ProcedureCode}](ChargeDetail) on {Charge_ServiceDate:date}; balance {Charge_Balance} ~~({Charge_ClaimStatus})~~',
 'ChargeDetail', 'ClientChart');
GO

INSERT ss.SemField (EntityCode, FieldCode, DisplayName, Description, ColumnName, DataType, IsDefault, Sensitivity, AllowedValues, SortOrder) VALUES
('Client','ClientId','Client ID',NULL,'ClientId','int',0,0,NULL,1),
('Client','FullName','Client name','Search by any part of the name; misspellings are handled','FullName','personName',0,1,NULL,5),
('Client','FirstName','First name',NULL,'FirstName','text',1,1,NULL,10),
('Client','LastName','Last name',NULL,'LastName','text',1,1,NULL,11),
('Client','DateOfBirth','Date of birth',NULL,'DateOfBirth','date',1,1,NULL,12),
('Client','Mrn','MRN','Medical record number','Mrn','code',1,1,NULL,13),
('Client','Gender','Gender',NULL,'Gender','text',0,1,NULL,20),
('Client','Status','Client status',NULL,'ClientStatus','text',1,0,N'Active|Inactive|Discharged',21),
('Client','PrimaryProgram','Program','Main program the client is enrolled in','PrimaryProgram','text',1,0,NULL,22),
('Client','PrimaryClinician','Primary clinician',NULL,'PrimaryClinicianName','text',0,0,NULL,23),
('Client','AdmissionDate','Admission date',NULL,'AdmissionDate','date',0,0,NULL,24),
('Client','DischargeDate','Discharge date',NULL,'DischargeDate','date',0,0,NULL,25),
('Client','Ssn','SSN',NULL,'Ssn','text',0,2,NULL,99),            -- restricted: never exposed

('Visit','VisitDate','Date of service',NULL,'VisitDate','datetime',1,0,NULL,10),
('Visit','VisitType','Service type','Kind of service','VisitType','text',1,0,N'Individual therapy|Group session|Psychiatric eval|Medication management|Case management|Intake',11),
('Visit','ProviderName','Provider','Staff member who delivered the service','ProviderName','text',1,0,NULL,12),
('Visit','Status','Visit status',NULL,'VisitStatus','text',1,0,N'Signed|Draft|Completed|No show|Cancelled',13),
('Visit','Program','Program',NULL,'Program','text',0,0,NULL,14),
('Visit','Location','Location',NULL,'LocationName','text',0,0,NULL,15),
('Visit','DurationMinutes','Duration (minutes)',NULL,'DurationMinutes','int',0,0,NULL,16),
('Visit','IsBillable','Billable',NULL,'IsBillable','bool',0,0,NULL,17),
('Visit','NoteText','Progress note text',NULL,'NoteText','text',0,2,NULL,99), -- restricted

('Diagnosis','Code','ICD-10 code','e.g. F32.1. Use startsWith for a family like F32','IcdCode','code',1,1,NULL,10),
('Diagnosis','Description','Diagnosis',NULL,'IcdDescription','text',1,1,NULL,11),
('Diagnosis','StartDate','Start date',NULL,'StartDate','date',1,0,NULL,12),
('Diagnosis','EndDate','End date','Null when still active','EndDate','date',0,0,NULL,13),
('Diagnosis','IsPrimary','Primary diagnosis',NULL,'IsPrimary','bool',0,0,NULL,14),

('Appointment','StartTime','Appointment time',NULL,'StartTime','datetime',1,0,NULL,10),
('Appointment','Status','Appointment status',NULL,'AppointmentStatus','text',1,0,N'Scheduled|Confirmed|Arrived|Cancelled|No show|Rescheduled',11),
('Appointment','StaffName','With',NULL,'StaffName','text',1,0,NULL,12),
('Appointment','AppointmentType','Appointment type',NULL,'AppointmentType','text',0,0,NULL,13),
('Appointment','Location','Location',NULL,'LocationName','text',0,0,NULL,14),

('Medication','DrugName','Medication',N'Drug name, generic or brand','DrugName','text',1,1,NULL,10),
('Medication','Strength','Strength',N'e.g. 50 mg','Strength','text',1,1,NULL,11),
('Medication','Frequency','Frequency',N'e.g. once daily','Frequency','text',1,0,NULL,12),
('Medication','Route','Route',NULL,'RouteName','text',0,0,N'Oral|Injection|Topical|Sublingual',13),
('Medication','Prescriber','Prescriber',NULL,'PrescriberName','text',1,0,NULL,14),
('Medication','StartDate','Start date',NULL,'StartDate','date',1,0,NULL,15),
('Medication','EndDate','Stop date','Null while still active','EndDate','date',0,0,NULL,16),
('Medication','Status','Medication status',NULL,'OrderStatus','text',1,0,N'Active|Discontinued|Completed|On hold',17),

('Charge','ServiceDate','Service date',NULL,'ServiceDate','date',1,0,NULL,10),
('Charge','ProcedureCode','Procedure code','CPT / HCPCS code','ProcedureCode','code',1,0,NULL,11),
('Charge','BilledAmount','Billed amount',NULL,'BilledAmount','decimal',1,0,NULL,12),
('Charge','PaidAmount','Paid amount',NULL,'PaidAmount','decimal',0,0,NULL,13),
('Charge','Balance','Balance due',NULL,'BalanceDue','decimal',1,0,NULL,14),
('Charge','Payer','Payer','Insurance / funding source','PayerName','text',1,0,NULL,15),
('Charge','ClaimStatus','Claim status',NULL,'ClaimStatus','text',1,0,N'Not billed|Submitted|Paid|Partially paid|Denied|Written off',16),
('Charge','BilledDate','Billed date',NULL,'BilledDate','date',0,0,NULL,17),

('Staff','FullName','Staff name',NULL,'FullName','text',1,0,NULL,10),
('Staff','Role','Role',NULL,'RoleName','text',1,0,N'Psychiatrist|Therapist|Case manager|Nurse|Prescriber',11),
('Staff','IsActive','Active',NULL,'IsActive','bool',0,0,NULL,12);
GO

INSERT ss.SemRelation (FromEntity, ToEntity, Cardinality, FromColumn, ToColumn) VALUES
('Visit','Client','ManyToOne','ClientId','ClientId'),
('Diagnosis','Client','ManyToOne','ClientId','ClientId'),
('Appointment','Client','ManyToOne','ClientId','ClientId'),
('Visit','Staff','ManyToOne','ProviderStaffId','StaffId'),
('Client','Visit','OneToMany','ClientId','ClientId'),
('Client','Diagnosis','OneToMany','ClientId','ClientId'),
('Client','Appointment','OneToMany','ClientId','ClientId'),
('Staff','Visit','OneToMany','StaffId','ProviderStaffId'),
('Medication','Client','ManyToOne','ClientId','ClientId'),
('Charge','Client','ManyToOne','ClientId','ClientId'),
('Client','Medication','OneToMany','ClientId','ClientId'),
('Client','Charge','OneToMany','ClientId','ClientId');
GO

INSERT ss.SemSynonym (Term, TargetType, Target) VALUES
(N'patient','Entity','Client'), (N'client','Entity','Client'), (N'consumer','Entity','Client'), (N'member','Entity','Client'),
(N'visit','Entity','Visit'), (N'session','Entity','Visit'), (N'service','Entity','Visit'), (N'encounter','Entity','Visit'), (N'seen','Entity','Visit'),
(N'diagnosis','Entity','Diagnosis'), (N'dx','Entity','Diagnosis'), (N'icd','Entity','Diagnosis'), (N'depression','Entity','Diagnosis'), (N'anxiety','Entity','Diagnosis'),
(N'appointment','Entity','Appointment'), (N'scheduled','Entity','Appointment'), (N'upcoming','Entity','Appointment'), (N'tomorrow','Entity','Appointment'),
(N'staff','Entity','Staff'), (N'provider','Entity','Staff'), (N'clinician','Entity','Staff'), (N'therapist','Entity','Staff'),
(N'medication','Entity','Medication'), (N'medications','Entity','Medication'), (N'meds','Entity','Medication'), (N'prescription','Entity','Medication'), (N'rx','Entity','Medication'), (N'prescribed','Entity','Medication'),
(N'billing','Entity','Charge'), (N'bill','Entity','Charge'), (N'billed','Entity','Charge'), (N'claim','Entity','Charge'), (N'claims','Entity','Charge'), (N'charge','Entity','Charge'), (N'invoice','Entity','Charge'), (N'balance','Entity','Charge'), (N'payment','Entity','Charge'),
(N'no show','Value','Visit.Status=No show'), (N'no-show','Value','Visit.Status=No show'), (N'unsigned','Value','Visit.Status=Draft');
GO

/* Few-shot examples (verified). Plans use the SearchPlan shape: {"parts":[ ... ]}, one part per thing asked. */
INSERT ss.SemExample (Question, PlanJson, EntitiesCsv, IsVerified) VALUES
(N'clients with a depression diagnosis who have not been seen in the last 90 days',
 N'{"parts":[{"title":"Clients not seen in 90 days","kind":"rows","entity":"Client","select":["Client.FirstName","Client.LastName","Client.DateOfBirth","Client.PrimaryProgram"],
   "where":{"and":[
     {"exists":{"entity":"Diagnosis","where":{"and":[{"field":"Diagnosis.Code","op":"startsWith","value":"F32"},{"field":"Diagnosis.EndDate","op":"isNull"}]}}},
     {"notExists":{"entity":"Visit","where":{"field":"Visit.VisitDate","op":"gte","value":{"relative":"-90d"}}}}]},
   "orderBy":[{"field":"Client.LastName","dir":"asc"}],"limit":100}]}',
 'Client,Diagnosis,Visit', 1),
(N'no shows last month for Dr. Shah',
 N'{"parts":[{"title":"No-shows last month","kind":"rows","entity":"Visit","select":["Visit.VisitDate","Visit.VisitType","Client.FirstName","Client.LastName"],
   "where":{"and":[{"field":"Visit.Status","op":"eq","value":"No show"},{"field":"Visit.ProviderName","op":"contains","value":"Shah"}]},
   "timeRange":{"from":{"startOf":"month","offset":-1},"to":{"startOf":"month","offset":0}},
   "orderBy":[{"field":"Visit.VisitDate","dir":"desc"}]}]}',
 'Visit,Client', 1),
(N'how many visits per program this year',
 N'{"parts":[{"title":"Visits per program","kind":"aggregate","entity":"Visit","groupBy":["Visit.Program"],"aggregates":[{"fn":"count","alias":"visits"}],
   "timeRange":{"from":{"startOf":"year","offset":0}},"orderBy":[{"field":"visits","dir":"desc"}],"limit":50}]}',
 'Visit', 1),
(N'clients with more than 3 no shows in the last 6 months',
 N'{"parts":[{"title":"Clients with 4+ no-shows","kind":"rows","entity":"Client","select":["Client.FirstName","Client.LastName","Client.PrimaryClinician"],
   "where":{"exists":{"entity":"Visit","minCount":4,"where":{"and":[{"field":"Visit.Status","op":"eq","value":"No show"},{"field":"Visit.VisitDate","op":"gte","value":{"relative":"-6m"}}]}}}}]}',
 'Client,Visit', 1),
(N'latest visit for vatsal',
 N'{"parts":[{"title":"Latest visit","kind":"rows","entity":"Visit","select":["Visit.VisitDate","Visit.VisitType","Visit.ProviderName","Visit.Status"],
   "where":{"field":"Client.FullName","op":"nameMatches","value":"vatsal"},
   "orderBy":[{"field":"Visit.VisitDate","dir":"desc"}],"latestPerClient":true,"limit":25}]}',
 'Visit,Client', 1),
(N'provide me details for last medication for vatsal and his last billing details',
 N'{"parts":[
   {"title":"Last medication","kind":"rows","entity":"Medication",
    "select":["Medication.DrugName","Medication.Strength","Medication.Frequency","Medication.Prescriber","Medication.StartDate","Medication.Status"],
    "where":{"field":"Client.FullName","op":"nameMatches","value":"vatsal"},
    "orderBy":[{"field":"Medication.StartDate","dir":"desc"}],"latestPerClient":true,"limit":25},
   {"title":"Last billing","kind":"rows","entity":"Charge",
    "select":["Charge.ServiceDate","Charge.ProcedureCode","Charge.BilledAmount","Charge.Payer","Charge.ClaimStatus","Charge.Balance"],
    "where":{"field":"Client.FullName","op":"nameMatches","value":"vatsal"},
    "orderBy":[{"field":"Charge.ServiceDate","dir":"desc"}],"latestPerClient":true,"limit":25}]}',
 'Medication,Charge,Client', 1);
GO

/* =====================================================================
   CURATED VIEW TEMPLATE (example - write one per entity over your legacy tables)
   Rules:
     - expose TenantId, the key, ClientId (if client-owned) and only catalogued columns
     - apply soft-delete / test-client / merged-client rules HERE, once
     - make sure (TenantId, ClientId, <date>) is indexed on the base table

CREATE OR ALTER VIEW ss.v_Visit WITH SCHEMABINDING AS
SELECT  s.TenantId,
        s.ServiceId            AS VisitId,
        s.ClientId,
        s.ServiceDateTime      AS VisitDate,
        st.Name                AS VisitType,
        s.StaffId              AS ProviderStaffId,
        stf.FullName           AS ProviderName,
        CASE s.StatusCode WHEN 'S' THEN N'Signed' WHEN 'D' THEN N'Draft' WHEN 'N' THEN N'No show'
                          WHEN 'C' THEN N'Cancelled' ELSE N'Completed' END AS VisitStatus,
        p.Name                 AS Program,
        l.Name                 AS LocationName,
        s.DurationMin          AS DurationMinutes,
        s.IsBillable,
        CAST(NULL AS nvarchar(max)) AS NoteText
FROM    dbo.ClientService s
JOIN    dbo.ServiceType st ON st.ServiceTypeId = s.ServiceTypeId
LEFT JOIN dbo.Staff stf    ON stf.StaffId = s.StaffId
LEFT JOIN dbo.Program p    ON p.ProgramId = s.ProgramId
LEFT JOIN dbo.Location l   ON l.LocationId = s.LocationId
WHERE   s.IsDeleted = 0;
   ===================================================================== */
