/* =====================================================================
   EHR Smart Search - seed data (system defaults, TenantId = 0)
   Safe to re-run: uses MERGE / NOT EXISTS.
   ===================================================================== */

/* ---------- Intents ---------- */
MERGE ss.IntentDefinition AS t
USING (VALUES
    ('FindClient',     N'Find client',       N'Find clients by name. Use when the user only wants to locate a person.',                           'Clients', 'Client', 'Client.View'),
    ('GetLatestVisit', N'Latest visit',      N'Get the most recent visit for one or more clients matched by name.',                              'Visits',  'Visit',  'Visit.View'),
    ('GetVisits',      N'Visits',            N'List visits for clients matched by name, optionally filtered by date range, provider or status.', 'Visits',  'Visit',  'Visit.View')
) AS s (IntentCode, DisplayName, Description, OwningModule, PrimaryEntityType, RequiredPermission)
ON t.IntentCode = s.IntentCode
WHEN MATCHED THEN UPDATE SET DisplayName = s.DisplayName, Description = s.Description, OwningModule = s.OwningModule,
                             PrimaryEntityType = s.PrimaryEntityType, RequiredPermission = s.RequiredPermission, UpdatedAt = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT (IntentCode, DisplayName, Description, OwningModule, PrimaryEntityType, RequiredPermission)
                      VALUES (s.IntentCode, s.DisplayName, s.Description, s.OwningModule, s.PrimaryEntityType, s.RequiredPermission);
GO

/* ---------- Layout rules: 1 = Panel, 2-6 = Cards, 7+ = List ---------- */
MERGE ss.LayoutRule AS t
USING (VALUES (0, '*', 'Panel', 1, 1), (0, '*', 'Cards', 2, 6), (0, '*', 'List', 7, NULL))
      AS s (TenantId, IntentCode, Layout, MinResults, MaxResults)
ON t.TenantId = s.TenantId AND t.IntentCode = s.IntentCode AND t.Layout = s.Layout
WHEN MATCHED THEN UPDATE SET MinResults = s.MinResults, MaxResults = s.MaxResults
WHEN NOT MATCHED THEN INSERT (TenantId, IntentCode, Layout, MinResults, MaxResults)
                      VALUES (s.TenantId, s.IntentCode, s.Layout, s.MinResults, s.MaxResults);
GO

/* ---------- View templates ----------
   FieldMapJson contract (see FieldMap.cs):
     title / subtitle : text with {Field} or {Field:date} placeholders
     fields           : rows shown in Panel and Cards (and columns in List)
                        type = text | date | datetime | person | badge
     badges           : field rendered as a coloured pill, toneMap value -> tone
     primaryLink      : link key from the ILinkProvider (main click target)
     secondaryLinks   : extra link keys shown as actions
     listOnly / panelOnly flags let one field show only in some layouts
   ----------------------------------------------------------------------- */
DECLARE @visit nvarchar(max) = N'{
  "title": "{FirstName} {LastName}",
  "subtitle": "DOB {DateOfBirth:date} · MRN {Mrn}",
  "fields": [
    { "key": "VisitDate",    "label": "Last visit", "type": "datetime" },
    { "key": "VisitType",    "label": "Visit type", "type": "text" },
    { "key": "ProviderName", "label": "Provider",   "type": "person" },
    { "key": "Program",      "label": "Program",    "type": "text", "panelOnly": true },
    { "key": "Location",     "label": "Location",   "type": "text", "panelOnly": true }
  ],
  "badges": [
    { "key": "Status", "toneMap": { "Signed": "success", "Completed": "success", "Draft": "warning", "No show": "danger", "Cancelled": "danger" } }
  ],
  "primaryLink": "VisitDetail",
  "secondaryLinks": [ "ClientChart", "ClientVisits" ]
}';

DECLARE @visits nvarchar(max) = N'{
  "title": "{FirstName} {LastName}",
  "subtitle": "{VisitDate:datetime}",
  "fields": [
    { "key": "VisitType",    "label": "Visit type", "type": "text" },
    { "key": "ProviderName", "label": "Provider",   "type": "person" },
    { "key": "Program",      "label": "Program",    "type": "text" }
  ],
  "badges": [
    { "key": "Status", "toneMap": { "Signed": "success", "Completed": "success", "Draft": "warning", "No show": "danger", "Cancelled": "danger" } }
  ],
  "primaryLink": "VisitDetail",
  "secondaryLinks": [ "ClientChart" ]
}';

DECLARE @client nvarchar(max) = N'{
  "title": "{FirstName} {LastName}",
  "subtitle": "DOB {DateOfBirth:date} · MRN {Mrn}",
  "fields": [
    { "key": "Gender",          "label": "Gender",          "type": "text" },
    { "key": "PrimaryProgram",  "label": "Program",         "type": "text" },
    { "key": "PrimaryClinician","label": "Clinician",       "type": "person" },
    { "key": "LastVisitDate",   "label": "Last visit",      "type": "date" },
    { "key": "Phone",           "label": "Phone",           "type": "text", "panelOnly": true }
  ],
  "badges": [
    { "key": "ClientStatus", "toneMap": { "Active": "success", "Discharged": "neutral", "Inactive": "warning" } }
  ],
  "primaryLink": "ClientChart",
  "secondaryLinks": [ "ClientVisits" ]
}';

;WITH s AS
(
    SELECT IntentCode, EntityType, Layout, Json
    FROM (VALUES
        ('GetLatestVisit', 'Visit',  'Panel', @visit),
        ('GetLatestVisit', 'Visit',  'Cards', @visit),
        ('GetLatestVisit', 'Visit',  'List',  @visit),
        ('GetVisits',      'Visit',  'Panel', @visits),
        ('GetVisits',      'Visit',  'Cards', @visits),
        ('GetVisits',      'Visit',  'List',  @visits),
        ('FindClient',     'Client', 'Panel', @client),
        ('FindClient',     'Client', 'Cards', @client),
        ('FindClient',     'Client', 'List',  @client)
    ) v (IntentCode, EntityType, Layout, Json)
)
INSERT ss.ViewTemplate (IntentCode, EntityType, Layout, TenantId, FieldMapJson)
SELECT s.IntentCode, s.EntityType, s.Layout, 0, s.Json
FROM s
WHERE NOT EXISTS (SELECT 1 FROM ss.ViewTemplate t
                  WHERE t.IntentCode = s.IntentCode AND t.EntityType = s.EntityType
                    AND t.Layout = s.Layout AND t.TenantId = 0 AND t.IsActive = 1);
GO

/* ---------- Common nicknames (extend as needed) ---------- */
;WITH s AS
(
    SELECT Alias, CanonicalName FROM (VALUES
        (N'bob', N'robert'), (N'rob', N'robert'), (N'bill', N'william'), (N'will', N'william'),
        (N'liz', N'elizabeth'), (N'beth', N'elizabeth'), (N'kate', N'katherine'), (N'katie', N'katherine'),
        (N'mike', N'michael'), (N'jim', N'james'), (N'jen', N'jennifer'), (N'tom', N'thomas'),
        (N'chris', N'christopher'), (N'dave', N'david'), (N'dan', N'daniel'), (N'sam', N'samuel'),
        (N'alex', N'alexander'), (N'tony', N'anthony'), (N'nick', N'nicholas'), (N'sue', N'susan')
    ) v (Alias, CanonicalName)
)
INSERT ss.NameAlias (TenantId, Alias, CanonicalName, Source)
SELECT 0, s.Alias, s.CanonicalName, 'Seed'
FROM s
WHERE NOT EXISTS (SELECT 1 FROM ss.NameAlias a WHERE a.TenantId = 0 AND a.Alias = s.Alias AND a.CanonicalName = s.CanonicalName);
GO
