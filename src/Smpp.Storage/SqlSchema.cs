namespace Smpp.Storage
{
    public static class SqlSchema
    {
        public const string TableScript = @"
IF OBJECT_ID(N'dbo.Accounts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Accounts
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_Accounts PRIMARY KEY,
        Name nvarchar(100) NOT NULL,
        SystemId nvarchar(16) NOT NULL,
        Password nvarchar(64) NOT NULL,
        AllowedIps nvarchar(500) NULL,
        TrafficType int NOT NULL,
        WebhookUrl nvarchar(500) NULL,
        DefaultCountry nvarchar(8) NULL,
        ThroughputPerSecond int NOT NULL,
        Enabled bit NOT NULL
    );
    CREATE UNIQUE INDEX UX_Accounts_SystemId ON dbo.Accounts(SystemId);
END

IF OBJECT_ID(N'dbo.Providers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Providers
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_Providers PRIMARY KEY,
        Name nvarchar(100) NOT NULL,
        Kind int NOT NULL,
        Country nvarchar(8) NULL,
        Host nvarchar(200) NULL,
        Port int NULL,
        SystemId nvarchar(16) NULL,
        Password nvarchar(16) NULL,
        SystemType nvarchar(13) NULL,
        SourceTon tinyint NULL,
        SourceNpi tinyint NULL,
        DestTon tinyint NULL,
        DestNpi tinyint NULL,
        UseTls bit NOT NULL,
        AllowInvalidCertificate bit NOT NULL,
        HttpUrl nvarchar(500) NULL,
        HttpAuthHeader nvarchar(500) NULL,
        ThroughputPerSecond int NOT NULL,
        WindowSize int NOT NULL,
        InboundSystemId nvarchar(16) NULL,
        WebhookPath nvarchar(200) NULL,
        Enabled bit NOT NULL
    );
    CREATE UNIQUE INDEX UX_Providers_Name ON dbo.Providers(Name);
END

IF COL_LENGTH(N'dbo.Providers', N'WebhookPath') IS NULL
    ALTER TABLE dbo.Providers ADD WebhookPath nvarchar(200) NULL;

IF OBJECT_ID(N'dbo.Routes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Routes
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_Routes PRIMARY KEY,
        Country nvarchar(8) NOT NULL,
        TrafficType int NOT NULL,
        ProviderId uniqueidentifier NOT NULL,
        Priority int NOT NULL,
        NumberPrefixes nvarchar(200) NULL,
        Enabled bit NOT NULL,
        CONSTRAINT FK_Routes_Providers FOREIGN KEY (ProviderId) REFERENCES dbo.Providers(Id)
    );
    CREATE INDEX IX_Routes_Country ON dbo.Routes(Country, TrafficType, Priority);
END

IF COL_LENGTH(N'dbo.Routes', N'NumberPrefixes') IS NULL
    ALTER TABLE dbo.Routes ADD NumberPrefixes nvarchar(200) NULL;

IF OBJECT_ID(N'dbo.SmsMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SmsMessages
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_SmsMessages PRIMARY KEY,
        AccountId uniqueidentifier NULL,
        Direction int NOT NULL,
        TrafficType int NOT NULL,
        Source nvarchar(21) NOT NULL,
        Destination nvarchar(21) NOT NULL,
        Text nvarchar(max) NOT NULL,
        Country nvarchar(8) NULL,
        Status int NOT NULL,
        Error nvarchar(400) NULL,
        ProviderId uniqueidentifier NULL,
        ClientMessageId nvarchar(64) NULL,
        TargetSystemId nvarchar(16) NULL,
        ReceiptNotified bit NOT NULL,
        CreatedAt datetime2 NOT NULL
    );
    CREATE INDEX IX_SmsMessages_CreatedAt ON dbo.SmsMessages(CreatedAt);
    CREATE INDEX IX_SmsMessages_ClientMessageId ON dbo.SmsMessages(ClientMessageId);
END

IF OBJECT_ID(N'dbo.MessageParts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MessageParts
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MessageParts PRIMARY KEY,
        MessageId uniqueidentifier NOT NULL,
        PartNumber int NOT NULL,
        PartCount int NOT NULL,
        ProviderMessageId nvarchar(64) NULL,
        Status int NOT NULL,
        CONSTRAINT FK_MessageParts_SmsMessages FOREIGN KEY (MessageId) REFERENCES dbo.SmsMessages(Id)
    );
    CREATE INDEX IX_MessageParts_MessageId ON dbo.MessageParts(MessageId);
END

IF OBJECT_ID(N'dbo.ProviderReceiptLinks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProviderReceiptLinks
    (
        ProviderId uniqueidentifier NOT NULL,
        ProviderMessageId nvarchar(64) NOT NULL,
        MessageId uniqueidentifier NOT NULL,
        PartId bigint NOT NULL,
        CONSTRAINT PK_ProviderReceiptLinks PRIMARY KEY (ProviderId, ProviderMessageId)
    );
END
";
    }
}
