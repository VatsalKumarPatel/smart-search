/* =====================================================================
   EHR Smart Search - adds the "Statements" layout
   (one sentence per result, with links inside the sentence).
   Run after 001 and 002. Safe to re-run.
   ===================================================================== */

/* ---------- allow 'Statements' in the layout check constraints ---------- */
IF OBJECT_ID('ss.CK_ViewTemplate_Layout', 'C') IS NOT NULL ALTER TABLE ss.ViewTemplate DROP CONSTRAINT CK_ViewTemplate_Layout;
ALTER TABLE ss.ViewTemplate ADD CONSTRAINT CK_ViewTemplate_Layout CHECK (Layout IN ('Panel','Cards','List','Statements'));

IF OBJECT_ID('ss.CK_LayoutRule_Layout', 'C') IS NOT NULL ALTER TABLE ss.LayoutRule DROP CONSTRAINT CK_LayoutRule_Layout;
ALTER TABLE ss.LayoutRule ADD CONSTRAINT CK_LayoutRule_Layout CHECK (Layout IN ('Panel','Cards','List','Statements'));

IF OBJECT_ID('ss.CK_UserPreference_Layout', 'C') IS NOT NULL ALTER TABLE ss.UserPreference DROP CONSTRAINT CK_UserPreference_Layout;
ALTER TABLE ss.UserPreference ADD CONSTRAINT CK_UserPreference_Layout CHECK (PreferredLayout IN ('Panel','Cards','List','Statements'));
GO

/* ---------- statement templates ----------
   "statement" syntax (see FieldMap.cs):
     {Field} / {Field:date|datetime}  a value
     [text](LinkKey)                  link to an existing page. The URL comes from ILinkProvider by key,
                                      never from this text. If the user may not open that page,
                                      the text still shows, just without a link.
     **text**                         emphasis
     ~~text~~                         muted (secondary detail)
   Badges and links work the same as in the other layouts.
   ------------------------------------------------------------------------ */
DECLARE @visitStatement nvarchar(max) = N'{
  "title": "{FirstName} {LastName}",
  "statement": "[{FirstName} {LastName}](ClientChart) was seen for [{VisitType}](VisitDetail) by **{ProviderName}** on {VisitDate:datetime} ~~at {Location}~~",
  "fields": [],
  "badges": [
    { "key": "Status", "toneMap": { "Signed": "success", "Completed": "success", "Draft": "warning", "No show": "danger", "Cancelled": "danger" } }
  ],
  "primaryLink": "VisitDetail",
  "secondaryLinks": [ "ClientChart" ]
}';

DECLARE @clientStatement nvarchar(max) = N'{
  "title": "{FirstName} {LastName}",
  "statement": "[{FirstName} {LastName}](ClientChart) ~~(DOB {DateOfBirth:date})~~ is in **{PrimaryProgram}** with {PrimaryClinician}; last seen on [{LastVisitDate:date}](ClientVisits)",
  "fields": [],
  "badges": [
    { "key": "ClientStatus", "toneMap": { "Active": "success", "Discharged": "neutral", "Inactive": "warning" } }
  ],
  "primaryLink": "ClientChart",
  "secondaryLinks": [ "ClientVisits" ]
}';

;WITH s AS
(
    SELECT IntentCode, EntityType, Json
    FROM (VALUES
        ('GetLatestVisit', 'Visit',  @visitStatement),
        ('GetVisits',      'Visit',  @visitStatement),
        ('FindClient',     'Client', @clientStatement)
    ) v (IntentCode, EntityType, Json)
)
INSERT ss.ViewTemplate (IntentCode, EntityType, Layout, TenantId, FieldMapJson)
SELECT s.IntentCode, s.EntityType, 'Statements', 0, s.Json
FROM s
WHERE NOT EXISTS (SELECT 1 FROM ss.ViewTemplate t
                  WHERE t.IntentCode = s.IntentCode AND t.EntityType = s.EntityType
                    AND t.Layout = 'Statements' AND t.TenantId = 0 AND t.IsActive = 1);
GO

/* Optional: make Statements the DEFAULT for an intent instead of only a toggle option.
   Example: GetVisits shows Panel for 1 result and Statements for 2 or more.

INSERT ss.LayoutRule (TenantId, IntentCode, Layout, MinResults, MaxResults)
VALUES (0, 'GetVisits', 'Panel', 1, 1),
       (0, 'GetVisits', 'Statements', 2, NULL);
*/
