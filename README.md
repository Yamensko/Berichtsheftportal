# Berichtsheftportal

> Interne Web-App bei PICA GmbH München für die digitale Verwaltung der Wochenberichte in der Ausbildung. Entstanden als mein IHK-Abschlussprojekt.

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)
![Blazor](https://img.shields.io/badge/Blazor_Server-512BD4?logo=blazor&logoColor=white)
![EF Core](https://img.shields.io/badge/EF_Core-9-512BD4?logo=.net&logoColor=white)
![SQL Server](https://img.shields.io/badge/SQL_Server-CC2927?logo=microsoftsqlserver&logoColor=white)
![Entra ID](https://img.shields.io/badge/Microsoft_Entra_ID-0078D4?logo=microsoftazure&logoColor=white)

---

## Inhalt

- [Was es macht](#was-es-macht)
- [Screenshots](#screenshots)
- [Tech](#tech)
- [Datenmodell](#datenmodell)
- [Besonderheiten](#besonderheiten)
- [Architektur](#architektur)
- [Projektstruktur](#projektstruktur)
- [Setup](#setup)
- [Konfiguration](#konfiguration)

## Was es macht

Auszubildende schreiben ihre Wochenberichte direkt in der App und reichen sie beim Ausbilder ein. Der prüft sie und gibt sie frei oder weist sie mit Kommentar zurück. Am Ende lassen sich die Berichte als PDF exportieren.

Login läuft über das Firmen-Microsoft-Konto per Single Sign-On (Entra ID), also ohne eigene Passwörter in der App.

Zwei Ansichten je nach Rolle:

- **Auszubildende** bekommen eine Kalenderansicht mit allen Wochen ihres Ausbildungszeitraums, jede Woche als Kachel mit Status-Indikator (Entwurf, Eingereicht, Freigegeben, Abgelehnt).
- **Ausbilder** sehen eine Matrix mit allen ihren Azubis und deren Wochenstatus auf einen Blick, mit Suche und Filter für offene Berichte.

## Screenshots

### Für Auszubildende

Kalenderwochen des eigenen Ausbildungszeitraums als Kacheln mit Status-Indikator (grün = freigegeben, orange = aktuell in Bearbeitung, pink = abgelehnt, grau = noch gesperrt (außerhalb der Ausbildunsgzeitraum)):

<img width="1247" height="593" alt="dashboard-azubi" src="https://github.com/user-attachments/assets/437af594-e9cf-40b8-9779-7837926c840f" />


Der Rich-Text-Editor mit AutoSave-Status in der oberen linken Ecke („Änderungen erkannt…" → „Gespeichert ✓"), unten die Feedback-Historie vom Ausbilder:

<img width="1291" height="713" alt="report-editor" src="https://github.com/user-attachments/assets/1f5c3c9e-9b7a-4e26-9aad-351909f79e68" />


### Für Ausbilder

Matrix mit allen Azubis und deren Wochenstatus auf einen Blick, mit Suche und Filter für offene Berichte:

<img width="1024" height="526" alt="dashboard-ausbilder" src="https://github.com/user-attachments/assets/b604b581-4bdd-46f8-a361-8d6e0bef3d83" />


Bericht-Detail eines Azubis mit der vollständigen Kommentar-Historie:

<img width="1024" height="595" alt="report-detail" src="https://github.com/user-attachments/assets/c3715fe5-9962-4a0b-87f2-1bf203dce1cb" />


Review-Modal mit Freigeben- und Zurückweisen-Buttons plus Kommentarfeld für Feedback:

<img width="1024" height="595" alt="review-modal" src="https://github.com/user-attachments/assets/1d1f20ed-c2cf-4395-bc92-cf254fadfe8e" />


### Export

Berichte als PDF herunterladen, entweder für einen Zeitraum oder alle auf einmal:

<img width="1023" height="541" alt="pdf-export" src="https://github.com/user-attachments/assets/63392089-e681-4b02-85b7-16e2419eb4ab" />


## Tech

**Backend:** C# · ASP.NET Core · Blazor Server (.NET 9)
**Datenbank:** Microsoft SQL Server · Entity Framework Core (Database-First)
**Auth:** Microsoft Entra ID (OpenID Connect · Single Sign-On)
**Mail:** Microsoft Graph API (delegierte Berechtigung)
**Frontend:** Quill.js / Blazox (Rich-Text-Editor)
**Tests:** xUnit · EF Core InMemory (16 Unit Tests · plus Integrationstests gegen echte DB)

## Datenmodell

Vier Haupt-Entitäten:

| Entität   | Zweck                                                               |
| --------- | ------------------------------------------------------------------- |
| `User`    | Benutzer mit Rolle, Email, Ausbildungszeitraum und (für Azubis) MentorID |
| `Report`  | Wochenbericht mit KW, Jahr, Inhalt (HTML) und Status                |
| `Comment` | Feedback-Historie der Ausbilder zu einzelnen Berichten              |
| `Role`    | Lookup für Azubi / Ausbilder                                        |

Das SQL-Schema hab ich selbst in SSMS geschrieben, die Models dann per EF Core Scaffolding aus der DB generieren lassen (Database-First). So spare ich mir das manuelle Nachtippen und riskiere keine Tippfehler zwischen Schema und C#-Model.

## Besonderheiten

**AutoSave im Editor.** Der Bericht wird automatisch zwei Sekunden nach der letzten Eingabe gespeichert. Dafür läuft im Hintergrund ein Timer, der bei jeder Eingabe neu gestartet wird (Debouncing). Fühlt sich an wie Google Docs.

**Verzögerte Mail-Benachrichtigung.** Reicht ein Azubi seinen Bericht ein, bekommt der Ausbilder nicht sofort eine Mail, sondern erst nach 30 Minuten. Zieht der Azubi den Bericht in der Zeit zurück (weil ihm noch was auffällt), wird die Mail wieder abgebrochen. Spart nervige Mail-Fluten bei Korrekturen. Läuft als `BackgroundService` mit einem `ConcurrentDictionary` für die ausstehenden Benachrichtigungen.

**Lazy Creation der Reports.** Berichte werden erst in der Datenbank angelegt wenn man sie wirklich öffnet. Sonst hätte jeder Azubi 156 leere Datensätze über drei Ausbildungsjahre, viele davon nie benutzt.

**Eigener PDF-Service.** Zuerst wollte ich iTextSharp nutzen, aber die Library hatte Abhängigkeitskonflikte mit .NET 9. Nach ein paar Versuchen hab ich mich entschieden die PDF-Generierung selbst zu implementieren. Der `PdfExportService` baut die PDF-Struktur komplett selbst zusammen (Objekte, Seiten, Fonts, Content-Streams), Helvetica als Font weil die in jedem PDF-Viewer eingebaut ist, Latin1/WinAnsiEncoding für saubere Umlaute.

**Token-Verwaltung für Graph API.** Access Tokens von Microsoft laufen schnell ab. Der `GraphTokenProvider` (Singleton) hält sie im Speicher, prüft vor jedem API-Call ob sie noch gültig sind und erneuert sie bei Bedarf automatisch per Refresh-Token. Kein manuelles Neu-Einloggen während der Session.

## Architektur

Klassisch dreischichtig: UI (Blazor Components) → Services (Business-Logik) → EF Core → SQL Server. Services werden über Dependency Injection registriert und sind damit isoliert testbar.

Die wichtigsten Services:

| Service                     | Aufgabe                                                   |
| --------------------------- | --------------------------------------------------------- |
| `ReportService`             | Reports erstellen, laden, speichern, Statusübergänge       |
| `UserServices`              | User-bezogene Operationen                                 |
| `MailService`               | Mail-Versand über Graph                                   |
| `GraphTokenProvider`        | Token-Verwaltung mit automatischer Erneuerung (Singleton) |
| `SubmitNotificationService` | BackgroundService für die verzögerten Mails               |
| `PdfExportService`          | PDF-Generierung ohne externe Library                      |

## Projektstruktur

```
Berichtsheftportal/
├── Berichtsportal/
│   ├── Components/
│   │   ├── Layout/              # Shared Layout-Komponenten
│   │   └── Pages/               # Blazor Pages (Home, Reports, Export, Ausbilder-Dashboard, …)
│   ├── Data/
│   │   └── BerichtsportalDbContext.cs
│   ├── Enums/                   # ReportStatus + Extensions (CSS-Mapping)
│   ├── Models/                  # User, Report, Comment, Role
│   ├── Services/                # Report, User, Mail, GraphToken, SubmitNotification, PdfExport
│   ├── wwwroot/
│   ├── appsettings.json
│   └── Program.cs
├── Berichtsportal.Tests/        # xUnit Test-Suite (16 Unit Tests)
└── Berichtsportal.sln
```

## Setup

**Voraussetzungen:** .NET 9 SDK · SQL Server (LocalDB reicht) · Microsoft Entra ID Tenant mit registrierter App.

```bash
git clone https://github.com/Yamensko/Berichtsheftportal.git
cd Berichtsheftportal/Berichtsportal

# appsettings.json anpassen (siehe Konfiguration unten)
dotnet ef database update
dotnet run
```

## Konfiguration

In `appsettings.json` müssen zwei Bereiche gefüllt werden:

```json
"ConnectionStrings": {
  "DatabaseConnection": "Server=(localdb)\\mssqllocaldb;Database=BerichtsportalDB;Trusted_Connection=True;TrustServerCertificate=True"
},
"AzureAd": {
  "Instance": "https://login.microsoftonline.com/",
  "TenantId": "<dein Entra ID Tenant>",
  "ClientId": "<deine App-Registrierung>",
  "CallbackPath": "/signin-oidc"
}
```

Die App-Registrierung braucht in Entra ID die delegierten Berechtigungen `User.Read` und `Mail.Send` (für den Mail-Versand via Graph).

## Autor

Yamen Machnouk · München · GitHub [@Yamensko](https://github.com/Yamensko)

---

*Transparenzhinweis: Claude (Anthropic) habe ich als Pair-Programmer verwendet, besonders bei der Graph-API-Integration, dem PDF-Export und der Token-Verwaltung. Vorschläge habe ich erst übernommen nachdem ich verstanden habe wie sie funktionieren. Das meiste wofür AI gelaufen ist war aber Repository-Cleanup, Kommentare und Dokumentation, deshalb taucht Claude auch als Co-Author in manchen Commits auf.*
