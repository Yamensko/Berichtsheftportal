# Mailversand / Microsoft Graph - Statusnotiz

Stand: 2026-05-12

## Ziel

Beim Freigeben eines Wochenberichts soll eine E-Mail ueber Microsoft Graph versendet werden.
Die Azure-Berechtigung soll weiterhin delegiert bleiben:

- Microsoft Graph
- `Mail.Send`
- Typ: Delegated
- Admin Consent fuer PICA GMBH erteilt

## Ausgangsproblem

Der alte `MailService` verwendete `ClientSecretCredential` mit dem Scope
`https://graph.microsoft.com/.default` und versendete ueber
`/users/{sender}/sendMail`.

Das ist ein App-only/Confidential-Client-Muster und passt nicht zu einer
delegierten `Mail.Send`-Berechtigung. Delegierter Versand muss als angemeldeter
Benutzer erfolgen, also ueber `/me/sendMail`.

## Bisherige Fehler

1. `user_null`

   Beim ersten Umbau auf `ITokenAcquisition.GetAccessTokenForUserAsync` konnte
   MSAL keinen Benutzer im Token-Cache finden. Ursache war, dass die App neu
   gestartet wurde und nur In-Memory-Token-Caching aktiv war. Ausserdem kam der
   Tokenabruf aus einer Blazor-Server-Interaktion.

2. `AADSTS700025`

   Azure meldete:

   `Client is public so neither 'client_assertion' nor 'client_secret' should be presented.`

   Die App-Registration wird in Azure aktuell als Public Client behandelt. Der
   Code hat aber einen Confidential-Client-Flow mit `ClientSecret` verwendet.

3. `AADSTS9002327`

   Azure meldete:

   `Tokens issued for the 'Single-Page Application' client-type may only be redeemed via cross-origin requests.`

   Die Azure App Registration soll unveraendert bleiben und ist als
   SPA/Public Client konfiguriert. ASP.NET Core loest den Authorization Code
   normalerweise serverseitig ohne Browser-`Origin`-Header ein. Fuer SPA-Clients
   verlangt Microsoft Entra ID aber einen Cross-Origin Token Request. Deshalb
   wurde der OpenID-Connect-Backchannel mit `Origin: https://localhost:7014`
   konfiguriert.

## Aktueller Code-Stand

Der Code wurde an die aktuelle Azure-Registration als Public Client angepasst:

- `ClientSecret` wurde aus `appsettings.json` entfernt.
- `EnableTokenAcquisitionToCallDownstreamApi(...)` wurde aus `Program.cs`
  entfernt, damit Microsoft.Identity.Web keinen Confidential-Client-Token-Cache
  mit Secret verwendet.
- OpenID Connect wird mit Authorization Code + PKCE konfiguriert.
- Beim Login werden `Mail.Send` und `offline_access` direkt angefordert.
- Der OIDC-Backchannel sendet `Origin: https://localhost:7014`, damit Entra ID
  den Authorization Code fuer die SPA/Public-Client-Registration akzeptiert.
- `SaveTokens = true` ist aktiv.
- Beim Login wird der Graph Access Token aus dem Auth-Ticket als Claim
  `graph_access_token` an den `ClaimsPrincipal` gehaengt. Das ist noetig, weil
  `HttpContext.GetTokenAsync()` in Blazor-Server-SignalR-Events nicht
  zuverlaessig verfuegbar war.
- `MailService` liest den Claim `graph_access_token` aus dem angemeldeten User
  und ruft Microsoft Graph `https://graph.microsoft.com/v1.0/me/sendMail` auf.
- Vor der DB-Aenderung wird geprueft, ob ein Access Token vorhanden ist, damit
  bei Login-/Token-Problemen nicht erneut Kommentare oder Statusaenderungen
  geschrieben werden.

## Wichtige Dateien

- `Berichtsportal/Program.cs`
- `Berichtsportal/Services/MailService.cs`
- `Berichtsportal/Services/ReportServices.cs`
- `Berichtsportal/Components/Pages/Ausbilder-Dashboard.razor`
- `Berichtsportal/appsettings.json`

## Teststatus

`dotnet build Berichtsportal.sln --no-restore` laeuft erfolgreich.

Es bleiben bestehende Warnungen:

- `Microsoft.IdentityModel 7.0.0` ist fuer .NET Framework restored worden und
  kann inkompatibel zu `net9.0` sein.
- Scaffold-Warnung zur Connection String Ablage.
- Nullable-Warnung in `Ausbilder-Dashboard.razor` bei `MarkupString`.

## Naechste Testschritte

1. Laufende App stoppen.
2. App neu starten.
3. Browser nicht direkt auf `/signin-oidc` oeffnen. Das ist nur die Callback-URL.
4. Auf `https://localhost:7014/` oder `https://localhost:7014/ausbilder-dashboard`
   gehen.
5. Einmal komplett abmelden und neu anmelden, damit der neue Login
   `https://graph.microsoft.com/Mail.Send` als Scope anfordert und der Access
   Token als Claim `graph_access_token` im neuen Auth-Cookie landet.
6. Einen Bericht freigeben und pruefen, ob Graph `me/sendMail` erfolgreich
   versendet.

## Architektur-Hinweis

Die aktuelle Loesung ist bewusst an die bestehende Azure-Registration als
SPA/Public Client angepasst, weil die Azure-Einstellungen unveraendert bleiben
sollen.

Fuer eine klassische Blazor-Server-Produktionsarchitektur waere sauberer:

- Azure App Registration als Web/Confidential Client konfigurieren
- Redirect URI unter Platform `Web`: `https://localhost:7014/signin-oidc`
- Public client flows deaktivieren
- Client Secret oder Zertifikat verwenden
- Dann kann Microsoft.Identity.Web Token Acquisition mit persistentem Cache
  verwendet werden

Die Berechtigung `Mail.Send` kann auch in dieser saubereren Variante weiterhin
delegiert bleiben.
