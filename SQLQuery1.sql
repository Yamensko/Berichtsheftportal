
CREATE TABLE Roles (
    RoleID INT PRIMARY KEY NOT NULL, 
    RoleName NVARCHAR(50) NOT NULL
);

CREATE TABLE Users (
    UserID UNIQUEIDENTIFIER PRIMARY KEY, -- GUID von Microsoft Entra ID
    Email NVARCHAR(255) NOT NULL UNIQUE,
    FirstName NVARCHAR(50) NOT NULL,
    LastName NVARCHAR(50) NOT NULL,
    RoleID INT NOT NULL, 
    AusbildungBeginn DATE NULL, 
    AusbildungEnde DATE NULL, 
    MentorID UNIQUEIDENTIFIER NULL,
    CONSTRAINT FK_User_Role FOREIGN KEY (RoleID) REFERENCES Roles(RoleID),
    CONSTRAINT FK_User_Mentor FOREIGN KEY (MentorID) REFERENCES Users(UserID)
);

CREATE TABLE Reports (
    ReportID INT PRIMARY KEY IDENTITY(1,1),
    AzubiID UNIQUEIDENTIFIER NOT NULL,
    CalendarWeek INT NOT NULL,
    Year INT NOT NULL,
    Content NVARCHAR(MAX) NULL,
    Status INT NOT NULL DEFAULT 0, -- 0=Draft, 1=Submitted, 2=Approved[cite: 1]
    LastModified DATETIME DEFAULT GETDATE(),
    CONSTRAINT FK_Report_Azubi FOREIGN KEY (AzubiID) REFERENCES Users(UserID)
);

CREATE TABLE Comments (
    CommentID INT PRIMARY KEY IDENTITY(1,1),
    ReportID INT NOT NULL,
    CommentorID UNIQUEIDENTIFIER NOT NULL,
    CommentText NVARCHAR(MAX) NOT NULL,
    CreatedAt DATETIME DEFAULT GETDATE(),
    CONSTRAINT FK_Comment_Report FOREIGN KEY (ReportID) REFERENCES Reports(ReportID),
    CONSTRAINT FK_Comment_User FOREIGN KEY (CommentorID) REFERENCES Users(UserID)
);

INSERT INTO Roles (RoleID, RoleName) 
VALUES (1, 'Auszubildender'), (2, 'Ausbilder');

-- Erst den Ausbilder (Mentor) anlegen
DECLARE @MentorID UNIQUEIDENTIFIER = NEWID(); -- Erzeugt eine Test-GUID
INSERT INTO Users (UserID, Email, FirstName, LastName, RoleID, MentorID)
VALUES (@MentorID, 'schmidt@pica.de', 'Thomas', 'Schmidt', 2, NULL);

-- Dann den Azubi anlegen und dem Mentor zuordnen
DECLARE @AzubiID UNIQUEIDENTIFIER = NEWID(); -- Erzeugt eine zweite Test-GUID
INSERT INTO Users (UserID, Email, FirstName, LastName, RoleID)
VALUES (@AzubiID, 'chyan@pica.de', 'Chyan', 'Kuhn', 1);


CREATE UNIQUE INDEX IX_Report_Unique
ON Reports (AzubiId, Year, CalendarWeek);

INSERT INTO Users(
    UserID, 
    Email, 
    FirstName, 
    LastName, 
    RoleID, 
    AusbildungBeginn, 
    AusbildungEnde, 
    MentorID
)
VALUES (
    NEWID(), -- Erzeugt eine neue eindeutige GUID
    'chyan@pica.de', 
    'Chyan', 
    'Kuhn', 
    1, 
    '2025-09-01', -- Datum im ISO-Format (YYYY-MM-DD)
    '2028-08-31', 
    '2300617d-4d36-46f3-be83-18458bd2a5d7'
);