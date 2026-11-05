/* =====================================================================
   EHR Smart Search - database schema (SQL Server 2019+)
   Schema: ss

   Assumptions (adjust to match your existing tables):
     - TenantId is INT. 0 means "system default" in config tables.
     - UserId is INT, ClientId / VisitId are BIGINT.
     - Clients and visits stay in your existing tables. This script only
       adds Smart Search configuration, audit and name-matching tables.
   ===================================================================== */

IF SCHEMA_ID('ss') IS NULL EXEC('CREATE SCHEMA ss AUTHORIZATION dbo');
GO

/* ---------------------------------------------------------------------
   1. INTENT CATALOGUE
   One row per intent the system supports. Code (ISearchCapability) is the
   source of truth; on startup the app checks that every registered
   capability has an enabled row here, and fails fast if not.
   Description is sent to Bedrock as the enum description in the tool schema.
   --------------------------------------------------------------------- */
CREATE TABLE ss.IntentDefinition
(
    IntentCode          varchar(64)    NOT NULL CONSTRAINT PK_IntentDefinition PRIMARY KEY,
    DisplayName         nvarchar(100)  NOT NULL,
    Description         nvarchar(500)  NOT NULL,
    OwningModule        varchar(64)    NOT NULL,          -- e.g. Clients, Visits
    PrimaryEntityType   varchar(32)    NOT NULL,          -- e.g. Client, Visit
    RequiredPermission  varchar(100)   NULL,
    IsEnabled           bit            NOT NULL CONSTRAINT DF_IntentDefinition_IsEnabled DEFAULT (1),
    CreatedAt           datetime2(3)   NOT NULL CONSTRAINT DF_IntentDefinition_CreatedAt DEFAULT (sysutcdatetime()),
    UpdatedAt           datetime2(3)   NULL
);
GO

/* Per-tenant on/off switch for an intent (row absent = use IntentDefinition.IsEnabled) */
CREATE TABLE ss.IntentTenantSetting
(
    TenantId    int          NOT NULL,
    IntentCode  varchar(64)  NOT NULL CONSTRAINT FK_IntentTenantSetting_Intent REFERENCES ss.IntentDefinition (IntentCode),
    IsEnabled   bit          NOT NULL,
    UpdatedAt   datetime2(3) NOT NULL CONSTRAINT DF_IntentTenantSetting_UpdatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_IntentTenantSetting PRIMARY KEY (TenantId, IntentCode)
);
GO

/* ---------------------------------------------------------------------
   2. VIEW CONFIGURATION
   ViewTemplate decides WHICH fields appear, in what order, with which labels,
   badges and links. The Razor views decide HOW it looks. When the final
   design arrives you change the .cshtml/.css only; these rows stay as they are.
   --------------------------------------------------------------------- */
CREATE TABLE ss.ViewTemplate
(
    TemplateId    int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_ViewTemplate PRIMARY KEY,
    IntentCode    varchar(64)    NOT NULL CONSTRAINT FK_ViewTemplate_Intent REFERENCES ss.IntentDefinition (IntentCode),
    EntityType    varchar(32)    NOT NULL,
    Layout        varchar(10)    NOT NULL CONSTRAINT CK_ViewTemplate_Layout CHECK (Layout IN ('Panel','Cards','List')),
    TenantId      int            NOT NULL CONSTRAINT DF_ViewTemplate_TenantId DEFAULT (0),   -- 0 = system default
    FieldMapJson  nvarchar(max)  NOT NULL CONSTRAINT CK_ViewTemplate_Json CHECK (ISJSON(FieldMapJson) = 1),
    Version       int            NOT NULL CONSTRAINT DF_ViewTemplate_Version DEFAULT (1),
    IsActive      bit            NOT NULL CONSTRAINT DF_ViewTemplate_IsActive DEFAULT (1),
    CreatedBy     int            NULL,
    CreatedAt     datetime2(3)   NOT NULL CONSTRAINT DF_ViewTemplate_CreatedAt DEFAULT (sysutcdatetime()),
    UpdatedBy     int            NULL,
    UpdatedAt     datetime2(3)   NULL
);
GO
/* Only one active template per intent/entity/layout/tenant */
CREATE UNIQUE INDEX UX_ViewTemplate_Active
    ON ss.ViewTemplate (IntentCode, EntityType, Layout, TenantId)
    WHERE IsActive = 1;
GO

/* How many results pick which layout. IntentCode '*' = applies to all intents.
   Lookup order: (tenant, intent) -> (tenant, '*') -> (0, intent) -> (0, '*') */
CREATE TABLE ss.LayoutRule
(
    TenantId    int          NOT NULL CONSTRAINT DF_LayoutRule_TenantId DEFAULT (0),
    IntentCode  varchar(64)  NOT NULL CONSTRAINT DF_LayoutRule_Intent DEFAULT ('*'),
    Layout      varchar(10)  NOT NULL CONSTRAINT CK_LayoutRule_Layout CHECK (Layout IN ('Panel','Cards','List')),
    MinResults  int          NOT NULL,
    MaxResults  int          NULL,        -- NULL = no upper bound
    CONSTRAINT PK_LayoutRule PRIMARY KEY (TenantId, IntentCode, Layout),
    CONSTRAINT CK_LayoutRule_Range CHECK (MaxResults IS NULL OR MaxResults >= MinResults)
);
GO

/* The layout a user last chose with the toggle, per intent */
CREATE TABLE ss.UserPreference
(
    TenantId         int          NOT NULL,
    UserId           int          NOT NULL,
    IntentCode       varchar(64)  NOT NULL,
    PreferredLayout  varchar(10)  NOT NULL CONSTRAINT CK_UserPreference_Layout CHECK (PreferredLayout IN ('Panel','Cards','List')),
    PageSize         smallint     NULL,
    UpdatedAt        datetime2(3) NOT NULL CONSTRAINT DF_UserPreference_UpdatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_UserPreference PRIMARY KEY (TenantId, UserId, IntentCode)
);
GO

/* ---------------------------------------------------------------------
   3. QUERY AUDIT + ANALYTICS  (HIPAA access log)
   Raw prompt text can contain PHI (patient names), so it is stored encrypted
   by the app (or use Always Encrypted). PromptHash supports de-duplication
   and analytics without decrypting.
   --------------------------------------------------------------------- */
CREATE TABLE ss.QueryLog
(
    QueryId               uniqueidentifier NOT NULL CONSTRAINT DF_QueryLog_QueryId DEFAULT (newsequentialid())
                                                    CONSTRAINT PK_QueryLog PRIMARY KEY,
    TenantId              int              NOT NULL,
    UserId                int              NOT NULL,
    ParentQueryId         uniqueidentifier NULL,      -- set for re-runs (layout change, did-you-mean pick, paging)
    Source                varchar(10)      NOT NULL CONSTRAINT CK_QueryLog_Source CHECK (Source IN ('Text','Voice','Rerun')),
    RawTextEncrypted      varbinary(max)   NULL,
    PromptHash            binary(32)       NULL,      -- SHA-256 of normalized prompt
    IntentCode            varchar(64)      NULL,      -- NULL when classification failed
    IntentJson            nvarchar(max)    NULL CONSTRAINT CK_QueryLog_IntentJson CHECK (IntentJson IS NULL OR ISJSON(IntentJson) = 1),
    UsedFallback          bit              NOT NULL CONSTRAINT DF_QueryLog_UsedFallback DEFAULT (0),
    NameResolutionStatus  varchar(10)      NULL CONSTRAINT CK_QueryLog_NameStatus CHECK (NameResolutionStatus IN ('Matched','Suggest','NotFound','NotNeeded')),
    ResultCount           int              NOT NULL CONSTRAINT DF_QueryLog_ResultCount DEFAULT (0),
    Layout                varchar(10)      NULL,
    ModelId               varchar(100)     NULL,
    InputTokens           int              NULL,
    OutputTokens          int              NULL,
    BedrockLatencyMs      int              NULL,
    TotalLatencyMs        int              NULL,
    ErrorCode             varchar(50)      NULL,
    CreatedAt             datetime2(3)     NOT NULL CONSTRAINT DF_QueryLog_CreatedAt DEFAULT (sysutcdatetime())
);
GO
CREATE INDEX IX_QueryLog_Tenant_Created ON ss.QueryLog (TenantId, CreatedAt) INCLUDE (UserId, IntentCode, ResultCount);
CREATE INDEX IX_QueryLog_User_Created   ON ss.QueryLog (TenantId, UserId, CreatedAt);
GO

/* Which records were shown for a query. Lets you answer
   "who has seen client X through smart search?" */
CREATE TABLE ss.QueryResultEntity
(
    QueryId     uniqueidentifier NOT NULL CONSTRAINT FK_QueryResultEntity_Query REFERENCES ss.QueryLog (QueryId),
    Rank        smallint         NOT NULL,
    TenantId    int              NOT NULL,
    EntityType  varchar(32)      NOT NULL,
    EntityId    bigint           NOT NULL,
    ClientId    bigint           NULL,
    IsSuggestion bit             NOT NULL CONSTRAINT DF_QueryResultEntity_IsSuggestion DEFAULT (0),
    CreatedAt   datetime2(3)     NOT NULL CONSTRAINT DF_QueryResultEntity_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_QueryResultEntity PRIMARY KEY (QueryId, Rank)
);
GO
CREATE INDEX IX_QueryResultEntity_Client ON ss.QueryResultEntity (TenantId, ClientId, CreatedAt) INCLUDE (QueryId);
GO

/* Link / suggestion clicks - measures usefulness and tunes the matcher */
CREATE TABLE ss.QueryClick
(
    ClickId     bigint           IDENTITY(1,1) NOT NULL CONSTRAINT PK_QueryClick PRIMARY KEY,
    QueryId     uniqueidentifier NOT NULL CONSTRAINT FK_QueryClick_Query REFERENCES ss.QueryLog (QueryId),
    TenantId    int              NOT NULL,
    UserId      int              NOT NULL,
    ClickType   varchar(12)      NOT NULL CONSTRAINT CK_QueryClick_Type CHECK (ClickType IN ('Link','Suggestion','FollowUp','Layout')),
    EntityType  varchar(32)      NULL,
    EntityId    bigint           NULL,
    ClientId    bigint           NULL,
    LinkKey     varchar(64)      NULL,      -- e.g. VisitDetail, ClientChart
    Rank        smallint         NULL,
    ClickedAt   datetime2(3)     NOT NULL CONSTRAINT DF_QueryClick_ClickedAt DEFAULT (sysutcdatetime())
);
GO
CREATE INDEX IX_QueryClick_Query ON ss.QueryClick (QueryId);
GO

/* Optional thumbs up / down on a result set */
CREATE TABLE ss.QueryFeedback
(
    QueryId    uniqueidentifier NOT NULL CONSTRAINT PK_QueryFeedback PRIMARY KEY
                                         CONSTRAINT FK_QueryFeedback_Query REFERENCES ss.QueryLog (QueryId),
    UserId     int              NOT NULL,
    IsHelpful  bit              NOT NULL,
    Comment    nvarchar(500)    NULL,
    CreatedAt  datetime2(3)     NOT NULL CONSTRAINT DF_QueryFeedback_CreatedAt DEFAULT (sysutcdatetime())
);
GO

/* ---------------------------------------------------------------------
   4. NAME MATCHING (spelling tolerance)
   A search-optimised copy of client name parts. Phonetic keys are computed
   in .NET (Double Metaphone), because SQL SOUNDEX is too weak for this.
   Kept in sync by the Clients module on create/update (domain event),
   plus a nightly backfill job.
   --------------------------------------------------------------------- */
CREATE TABLE ss.ClientNameIndex
(
    TenantId        int            NOT NULL,
    ClientId        bigint         NOT NULL,
    NamePart        tinyint        NOT NULL CONSTRAINT CK_ClientNameIndex_Part CHECK (NamePart IN (1,2,3,4)),
                                   -- 1 First, 2 Last, 3 Preferred / alias, 4 Middle
    Seq             tinyint        NOT NULL CONSTRAINT DF_ClientNameIndex_Seq DEFAULT (1),
    NameNormalized  nvarchar(100)  NOT NULL,   -- lower-case, diacritics and punctuation removed
    PhoneticPrimary varchar(12)    NOT NULL,
    PhoneticAlt     varchar(12)    NULL,
    Prefix2         AS CAST(LEFT(NameNormalized, 2) AS nvarchar(2)) PERSISTED,
    IsActiveClient  bit            NOT NULL CONSTRAINT DF_ClientNameIndex_Active DEFAULT (1),
    UpdatedAt       datetime2(3)   NOT NULL CONSTRAINT DF_ClientNameIndex_UpdatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_ClientNameIndex PRIMARY KEY (TenantId, ClientId, NamePart, Seq)
);
GO
CREATE INDEX IX_ClientNameIndex_Normalized ON ss.ClientNameIndex (TenantId, NameNormalized) INCLUDE (ClientId, NamePart, IsActiveClient);
CREATE INDEX IX_ClientNameIndex_Phonetic   ON ss.ClientNameIndex (TenantId, PhoneticPrimary) INCLUDE (ClientId, NamePart, NameNormalized, IsActiveClient);
CREATE INDEX IX_ClientNameIndex_PhoneticAlt ON ss.ClientNameIndex (TenantId, PhoneticAlt) INCLUDE (ClientId, NamePart, NameNormalized, IsActiveClient) WHERE PhoneticAlt IS NOT NULL;
CREATE INDEX IX_ClientNameIndex_Prefix2    ON ss.ClientNameIndex (TenantId, Prefix2) INCLUDE (ClientId, NamePart, NameNormalized, IsActiveClient);
GO

/* Nicknames and common variants: Bob -> Robert, Bill -> William */
CREATE TABLE ss.NameAlias
(
    AliasId        int           IDENTITY(1,1) NOT NULL CONSTRAINT PK_NameAlias PRIMARY KEY,
    TenantId       int           NOT NULL CONSTRAINT DF_NameAlias_TenantId DEFAULT (0),
    Alias          nvarchar(50)  NOT NULL,
    CanonicalName  nvarchar(50)  NOT NULL,
    Source         varchar(10)   NOT NULL CONSTRAINT DF_NameAlias_Source DEFAULT ('Seed')
                                 CONSTRAINT CK_NameAlias_Source CHECK (Source IN ('Seed','Admin','Learned')),
    CreatedAt      datetime2(3)  NOT NULL CONSTRAINT DF_NameAlias_CreatedAt DEFAULT (sysutcdatetime())
);
GO
CREATE UNIQUE INDEX UX_NameAlias ON ss.NameAlias (TenantId, Alias, CanonicalName);
GO

/* Table type used by the backfill job and the Clients module to upsert name parts */
CREATE TYPE ss.ClientNameRow AS TABLE
(
    ClientId        bigint        NOT NULL,
    NamePart        tinyint       NOT NULL,
    Seq             tinyint       NOT NULL,
    NameNormalized  nvarchar(100) NOT NULL,
    PhoneticPrimary varchar(12)   NOT NULL,
    PhoneticAlt     varchar(12)   NULL,
    IsActiveClient  bit           NOT NULL,
    PRIMARY KEY (ClientId, NamePart, Seq)
);
GO

/* Replaces all name rows for the clients in @Rows */
CREATE OR ALTER PROCEDURE ss.usp_ClientNameIndex_Replace
    @TenantId int,
    @Rows     ss.ClientNameRow READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRAN;

    DELETE n
    FROM ss.ClientNameIndex n
    WHERE n.TenantId = @TenantId
      AND n.ClientId IN (SELECT DISTINCT ClientId FROM @Rows);

    INSERT ss.ClientNameIndex (TenantId, ClientId, NamePart, Seq, NameNormalized, PhoneticPrimary, PhoneticAlt, IsActiveClient)
    SELECT @TenantId, ClientId, NamePart, Seq, NameNormalized, PhoneticPrimary, PhoneticAlt, IsActiveClient
    FROM @Rows;

    COMMIT;
END;
GO

/* Candidate lookup for one search token. Returns up to @Take rows; the .NET
   NameMatcher scores them (Jaro-Winkler + Damerau-Levenshtein).
   IMPORTANT: join your caseload / client-access view where marked so that
   suggestions never include clients the user cannot see. */
CREATE OR ALTER PROCEDURE ss.usp_FindNameCandidates
    @TenantId        int,
    @UserId          int,
    @Token           nvarchar(100),   -- normalized search token, e.g. 'vatsl'
    @PhoneticPrimary varchar(12),
    @PhoneticAlt     varchar(12) = NULL,
    @Take            int = 200
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Prefix2 nvarchar(2) = LEFT(@Token, 2);

    ;WITH hits AS
    (
        /* exact / prefix */
        SELECT ClientId, NamePart, NameNormalized, 1 AS MatchKind
        FROM ss.ClientNameIndex
        WHERE TenantId = @TenantId AND IsActiveClient = 1
          AND NameNormalized LIKE @Token + N'%'
        UNION ALL
        /* nickname -> canonical */
        SELECT n.ClientId, n.NamePart, n.NameNormalized, 2
        FROM ss.NameAlias a
        JOIN ss.ClientNameIndex n
          ON n.TenantId = @TenantId AND n.IsActiveClient = 1
         AND n.NameNormalized = LOWER(a.CanonicalName)
        WHERE a.TenantId IN (0, @TenantId) AND LOWER(a.Alias) = @Token
        UNION ALL
        /* sounds-like */
        SELECT ClientId, NamePart, NameNormalized, 3
        FROM ss.ClientNameIndex
        WHERE TenantId = @TenantId AND IsActiveClient = 1
          AND (PhoneticPrimary IN (@PhoneticPrimary, @PhoneticAlt)
               OR PhoneticAlt  IN (@PhoneticPrimary, @PhoneticAlt))
        UNION ALL
        /* same first two letters (catches typos that change the sound) */
        SELECT ClientId, NamePart, NameNormalized, 4
        FROM ss.ClientNameIndex
        WHERE TenantId = @TenantId AND IsActiveClient = 1
          AND Prefix2 = @Prefix2
    ),
    ranked AS
    (
        SELECT ClientId, NamePart, NameNormalized, MatchKind,
               ROW_NUMBER() OVER (PARTITION BY ClientId, NamePart ORDER BY MatchKind) AS rn
        FROM hits
    )
    SELECT TOP (@Take) r.ClientId, r.NamePart, r.NameNormalized, r.MatchKind
    FROM ranked r
    /* >>> JOIN dbo.<YourClientAccessView> acc ON acc.ClientId = r.ClientId AND acc.UserId = @UserId <<< */
    WHERE r.rn = 1
    ORDER BY r.MatchKind, r.NameNormalized;
END;
GO
