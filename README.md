# ngxPMIpUpdater

Ein .NET Worker Service, der stündlich DNS-Domains auflöst, die ermittelten IPs mit festen IP-Blöcken zusammenführt und eine **Nginx Proxy Manager (NPM) Access List** automatisch aktualisiert.

## Ist nur ein Vibe Code Projekt
## Funktionsweise

```
[Konfiguration]
    ├── Domains (z. B. mein-haus.dyndns.org)  →  DNS-Auflösung  →  1.2.3.4/32
    └── StaticIpBlocks (z. B. 192.168.1.0/24) →  immer erlaubt
                              ↓
              NPM Access List via REST API aktualisieren
              (alte Einträge raus, neue rein)
```

Der Dienst läuft als endlose Schleife und wiederholt den Vorgang alle `IntervalMinutes` Minuten.

Optional – oder statt NPM – pflegt er dieselben IPs auch in einer **Cloudflare Zero Trust Access Group**,
für Dienste hinter einem Cloudflare Tunnel (siehe [Cloudflare Tunnel](#cloudflare-tunnel)).

### Schutzmechanismen

Weil der Dienst die Zugangskontrolle eines Reverse Proxy umschreibt, gilt durchgehend **fail closed** – im
Zweifel bleibt die Access List unangetastet, statt eine unvollständige Liste zu schreiben:

- **Basic-Auth-Benutzer bleiben erhalten.** Beim Update werden nur die `clients` der Access List ersetzt.
  Das Feld `items` wird bewusst nicht mitgesendet, weil NPM dabei alle Auth-Benutzer löschen würde.
- **Kein Aussperren bei DNS-Ausfall.** Die letzten erfolgreich aufgelösten IPs werden pro Domain in
  `/data/last-known.json` gespeichert. Löst eine Domain nicht auf, wird der letzte gute Wert verwendet.
  Gibt es auch keinen gespeicherten Wert, wird der Zyklus **abgebrochen** – die Access List behält ihren
  Stand, und der nächste Versuch folgt schon nach 5 Minuten statt nach dem vollen Intervall.
- **Gespeicherte IPs verfallen.** Bestätigt DNS eine Domain länger als `FallbackMaxAgeHours` (Standard: 72 h)
  nicht mehr, wird ihre alte IP aus der Access List entfernt. Eine DynDNS-Adresse wird nach einer Weile
  an einen anderen Anschluss vergeben – sie dauerhaft zu erlauben, würde einem Fremden Zugriff geben.
  Bleibt danach gar keine IP mehr übrig (alle Domains abgelaufen, keine `StaticIpBlocks`), wird die Liste
  auf **`deny all`** gesetzt statt unverändert gelassen. Eine leere Liste wäre keine Option: NPM schreibt
  das abschließende `deny all;` nur, wenn mindestens eine Client-Regel existiert – leer hieße offen.
  Basic-Auth-Benutzer kommen mit „Satisfy Any“ weiterhin rein. Sobald DNS wieder antwortet, wird die
  Sperre beim nächsten Zyklus automatisch aufgehoben.
- **Ungültige Einträge werden verworfen, nicht durchgereicht.** Adressen und CIDR-Blöcke werden geprüft und
  normalisiert (inkl. Leerzeichen und gesetzter Host-Bits). Ein fehlerhafter Eintrag wird geloggt und
  übersprungen, statt das gesamte Update scheitern zu lassen.
- **`0.0.0.0/0` wird abgelehnt**, da es den Proxy für das gesamte Internet öffnen würde – außer
  `AllowOpenBlocks` ist explizit auf `true` gesetzt.
- **Unplausible DNS-Antworten werden gefiltert:** `0.0.0.0` (typisch für abgelaufene DynDNS-Hostnamen),
  Loopback, Link-Local, Broadcast und Multicast werden nie zur `allow`-Regel.

> **Vertrauensmodell:** Wer die Kontrolle über eine konfigurierte Domain erlangt – etwa weil ein
> DynDNS-Name abgelaufen und neu registriert wurde – erhält damit automatisch Zugriff. Nur Domains
> eintragen, die dauerhaft unter eigener Kontrolle stehen.

---

## Konfiguration

Konfiguriert wird über `appsettings.json`, Umgebungsvariablen (Docker) oder User-Secrets.
Fehlt etwas Wesentliches, **bricht der Start sofort mit einer klaren Meldung ab** statt erst im ersten Zyklus.

> **Credentials gehören nicht in `appsettings.json`.** Die versionierte Datei enthält deshalb keine
> Zugangsdaten mehr. Token und Passwort kommen über Umgebungsvariablen, besser noch über
> Secret-Files (siehe unten).

```json
{
  "AppSettings": {
    "IntervalMinutes": 60,

    "Domains": [
      "mein-haus.dyndns.org",
      "buero.example.com"
    ],

    "StaticIpBlocks": [
      "192.168.1.0/24",
      "10.0.0.5/32"
    ],

    "Npm": {
      "BaseUrl": "http://192.168.1.10:81",
      "AccessListId": 1
    }
  }
}
```

### Listen als eine Variable

`Domains` und `StaticIpBlocks` nehmen beliebig viele Einträge, mit **Komma oder Semikolon** getrennt.
Für Docker genügt damit eine Variable pro Liste – kein Abzählen von Index-Slots:

```bash
DOMAINS=mein-haus.dyndns.org,buero.example.com,werkstatt.dyndns.org
STATIC_IPS=192.168.1.0/24,10.0.0.5/32
```

Ein JSON-Array in `appsettings.json` funktioniert unverändert weiter, und ein Array-Eintrag darf selbst
eine Komma-Liste sein – beides landet in derselben flachen Liste.

> ⚠️ **Achtung bei Arrays und Umgebungsvariablen:** `AppSettings__Domains__0` überschreibt nur *Index 0*.
> Stehen in `appsettings.json` mehrere Domains, bleiben die höheren Indizes erhalten und werden weiterhin
> freigeschaltet. Die ausgelieferte `appsettings.json` hat die Listen darum absichtlich leer.

| Feld | Beschreibung |
|------|-------------|
| `IntervalMinutes` | Wie oft (in Minuten) die IPs aktualisiert werden (Standard: 60) |
| `Domains` | Hostnamen/DynDNS-Domains – werden per DNS aufgelöst. JSON-Array oder Komma-Liste |
| `StaticIpBlocks` | Feste IP-Adressen oder CIDR-Blöcke (z. B. internes Netz), die immer erlaubt bleiben. JSON-Array oder Komma-Liste |
| `AllowOpenBlocks` | Erlaubt `0.0.0.0/0` in `StaticIpBlocks` (Standard: `false`) – öffnet den Proxy fürs ganze Internet |
| `StateFilePath` | Ablage der letzten erfolgreich aufgelösten IPs (Standard: `/data/last-known.json`) |
| `FallbackMaxAgeHours` | Wie lange die letzte gute IP einer nicht auflösbaren Domain weiterverwendet wird (Standard: `72`, `0` = unbegrenzt) |
| `ResolveIPv6` | AAAA-Records ebenfalls als `/128` eintragen (Standard: `false`) |
| `Npm.BaseUrl` | URL des Nginx Proxy Manager (inkl. Port, ohne `/api`) |
| `Npm.ApiToken` | **Empfohlen (NPM ≥ v2.13):** Langlebiger API-Token, erstellt im NPM UI unter *Access → API Tokens*. Wenn gesetzt, werden Email/Password ignoriert. |
| `Npm.ApiTokenFile` | Pfad zu einer Datei mit dem Token, z. B. ein Docker-Secret unter `/run/secrets/npm_api_token`. Überschreibt `ApiToken`. |
| `Npm.Email` | Login-E-Mail – nur wenn kein ApiToken gesetzt (ältere NPM-Versionen) |
| `Npm.Password` | Passwort – nur wenn kein ApiToken gesetzt (ältere NPM-Versionen) |
| `Npm.PasswordFile` | Pfad zu einer Datei mit dem Passwort. Überschreibt `Password`. |
| `Npm.AccessListId` | ID der zu aktualisierenden NPM Access List |
| `Cloudflare.AccountId` | Cloudflare-Account-ID (32 Zeichen) – setzt man einen `Cloudflare.*`-Wert, wird Cloudflare aktualisiert |
| `Cloudflare.AccessGroupId` | UUID der Access Group, deren IP-Regeln gepflegt werden |
| `Cloudflare.ApiToken` | API-Token mit *Access: Organizations, Identity Providers, and Groups → Edit* |
| `Cloudflare.ApiTokenFile` | Pfad zu einer Datei mit dem Token. Überschreibt `ApiToken`. |
| `Notifications.Url` | Ziel für Benachrichtigungen (leer = aus), z. B. `https://ntfy.sh/mein-topic` |
| `Notifications.UrlFile` | Pfad zu einer Datei mit der URL. Überschreibt `Url` – gedacht für Discord, wo die URL selbst das Geheimnis ist. |
| `Notifications.Type` | `ntfy` (Standard), `gotify`, `discord` oder `json` |
| `Notifications.Token` | ntfy-Access-Token (`Authorization: Bearer`) bzw. Gotify-App-Token (`X-Gotify-Key`) |
| `Notifications.TokenFile` | Pfad zu einer Datei mit dem Token. Überschreibt `Token`. |
| `Notifications.NotifyOnChange` | Auch bei jeder normalen Änderung melden (Standard: `true`) |

Ohne `Npm.BaseUrl` und mit gesetzten `Cloudflare.*`-Werten läuft der Dienst **nur gegen Cloudflare** – die
`Npm.*`-Felder sind dann nicht nötig.

### Cloudflare Tunnel

Hinter einem Cloudflare Tunnel sieht NPM nur noch die Adresse von `cloudflared`, eine NPM Access List
filtert dort also nichts mehr. Die Prüfung muss bei Cloudflare passieren. Der Dienst pflegt dafür die
IP-Regeln einer **Access Group**; die eigentliche Freigabe macht eine Policy, die diese Gruppe verwendet.

**Einrichtung:**

1. **Access Group anlegen:** *Zero Trust → Access → Access Groups → Add a group*, Name z. B. `Heim-IPs`,
   Include → *IP ranges* mit der aktuellen Heim-IP (wird beim ersten Lauf ersetzt). Die UUID der Gruppe
   steht in der URL der Gruppe, alternativ liefert sie
   `GET https://api.cloudflare.com/client/v4/accounts/<account-id>/access/groups`.
2. **Application absichern:** Für den Tunnel-Hostnamen eine *Self-hosted Application* mit zwei Policies:
   - **Bypass**, Include → *Access Group* `Heim-IPs` – von zu Hause kommt man ohne Login rein
   - **Allow**, Include → z. B. *Emails* – alle anderen müssen sich anmelden (z. B. per E-Mail-Code)

   Das entspricht NPMs *Satisfy Any* mit Basic-Auth. Fehlt die Allow-Policy, ist der Dienst von außerhalb
   der Heim-IPs gar nicht erreichbar.
3. **API-Token erstellen:** *My Profile → API Tokens → Create Custom Token*, Berechtigung
   *Account → Access: Organizations, Identity Providers, and Groups → Edit*, auf den eigenen Account beschränkt.
   Die Account-ID steht in der Dashboard-URL `dash.cloudflare.com/<account-id>/…`.

```bash
CF_ACCOUNT_ID=0123456789abcdef0123456789abcdef
CF_ACCESS_GROUP_ID=8d3f1c2a-5b6e-4f70-9a81-2c3d4e5f6a7b
CF_API_TOKEN=…            # besser als Secret-File, siehe docker-compose.yml
```

**Was der Dienst ändert – und was nicht:**

- Ersetzt werden **nur die `IP ranges`-Regeln im Include** der Gruppe. Andere Include-Regeln sowie
  Exclude und Require bleiben unangetastet – wie die Basic-Auth-Benutzer bei NPM.
- Alle Schutzmechanismen von oben gelten genauso: letzte gute IP bei DNS-Ausfall, Ablauf nach
  `FallbackMaxAgeHours`, kein Schreiben ohne Änderung.
- Die **`deny all`-Sperre** schreibt die Regel `255.255.255.255/32`. Cloudflare erlaubt keine Gruppe ohne
  Include-Regel, und von dieser Adresse kommt nie eine echte Anfrage – die Gruppe passt dann auf niemanden.
  Die Allow-Policy mit Login greift weiterhin.
- Mit NPM **und** Cloudflare wird jedes Ziel für sich aktualisiert. Ist Cloudflare nicht erreichbar, bekommt
  NPM trotzdem die neue IP; der Zyklus gilt aber als fehlgeschlagen und wird nach 5 Minuten wiederholt.

> **IPv6 beachten:** Cloudflare sieht die öffentliche Adresse, mit der der Client Cloudflare erreicht. Hat der
> Heimanschluss IPv6, ist das meist die IPv6-Adresse – die IPv4 aus dem DynDNS passt dann nicht, und der
> Bypass greift nicht (Login statt direktem Zugriff). Abhilfe: `RESOLVE_IPV6=true`, wenn der DynDNS-Name
> einen AAAA-Record hat, oder einen IPv6-Präfix (z. B. das `/56` des Anschlusses) in `STATIC_IPS`.
> Private Netze wie `192.168.1.0/24` sind für Cloudflare dagegen wirkungslos, aber harmlos.

### Benachrichtigungen

Optional meldet der Dienst sich per Push – an [ntfy](https://ntfy.sh), [Gotify](https://gotify.net),
einen Discord-Channel oder einen beliebigen Endpunkt, der einen JSON-POST (`title`, `message`, `level`,
`source`) annimmt:

| Ereignis | Priorität | abschaltbar |
|----------|-----------|-------------|
| Access List geändert (neue DynDNS-IP) | normal | `NotifyOnChange=false` |
| Liste auf **`deny all`** gesperrt | höchste | nein |
| Sperre aufgehoben | normal | nein |
| Seit Ablauf der Health-Frist kein erfolgreicher Zyklus | hoch | nein |
| Wieder erfolgreich nach einem Ausfall | normal | nein |

Ein einzelner fehlgeschlagener Zyklus löst noch nichts aus – der Retry nach 5 Minuten behebt das meist.
Gemeldet wird erst, wenn der Container auch laut Healthcheck `unhealthy` wird (siehe unten), und dann nur
einmal bis zur Erholung.

**Discord:** Im Channel unter *Einstellungen → Integrationen → Webhooks* einen Webhook anlegen und dessen URL
als `Url` mit `Type=discord` eintragen. Die Nachricht kommt als farbiges Embed (grün/orange/rot je nach
Dringlichkeit). Die Webhook-URL **ist** das Zugangsgeheimnis – wer sie kennt, kann in den Channel posten.
Sie gehört darum besser in ein Secret-File (`UrlFile`) als in eine Umgebungsvariable.

Die Nachrichten enthalten **nur Anzahlen, keine IP-Adressen** – auf einem öffentlichen Server wie ntfy.sh
landen sonst Heim-IPs bei einem Dritten. Ein nicht erreichbarer Push-Dienst wird geloggt, bricht aber nie
ein Update ab.

### Secrets sicher übergeben

Umgebungsvariablen sind über `docker inspect` und `/proc/<pid>/environ` für jeden Prozess im Container
lesbar. Für Token und Passwort gibt es darum die `*File`-Varianten nach Docker-Secrets-Konvention:

```yaml
services:
  ngx-pm-ip-updater:
    environment:
      - AppSettings__Npm__ApiTokenFile=/run/secrets/npm_api_token
    secrets:
      - npm_api_token

secrets:
  npm_api_token:
    file: ./secrets/npm_api_token
```

Zeigt ein `*File`-Pfad auf eine fehlende oder leere Datei, **bricht der Start ab** – ein stillschweigend
leeres Secret würde sonst unbemerkt auf einen anderen Auth-Pfad zurückfallen.

Liegt das Secret trotzdem in der `.env`, sollte die Datei nur dir gehören:

```bash
chmod 600 .env
```

### Verbindung zum NPM verschlüsseln

`http://…:81` überträgt API-Token bzw. Admin-Passwort **im Klartext**. Innerhalb eines privaten
Docker-Netzes (`http://nginx-proxy-manager:81`) ist das vertretbar, weil der Verkehr den Host nicht
verlässt. Das häufig kopierte Beispiel `http://192.168.1.10:81` geht dagegen über das LAN – dort besser
den Container ins gleiche Docker-Netz wie NPM hängen oder HTTPS verwenden.

### Access List ID ermitteln

1. Im NPM-Dashboard unter **Access Lists** die gewünschte Liste öffnen
2. Die ID steht in der URL: `.../access-lists/**3**/edit` → ID = `3`

---

## Docker

### Image bauen

```bash
cd ngxPMIpUpdater
docker build -t ngx-pm-ip-updater .
```

### Container starten

```bash
docker run -d \
  --name ngx-pm-ip-updater \
  --restart unless-stopped \
  -v ngx-pm-ip-updater-state:/data \
  -e AppSettings__Npm__BaseUrl=http://192.168.1.10:81 \
  -e AppSettings__Npm__ApiToken=dein-api-token \
  -e AppSettings__Npm__AccessListId=1 \
  -e AppSettings__Domains__0=mein-haus.dyndns.org,buero.example.com \
  ngx-pm-ip-updater
```

> Umgebungsvariablen überschreiben Werte aus `appsettings.json`.  
> Doppelter Unterstrich `__` entspricht einem Doppelpunkt `:` in der Konfigurationshierarchie.

Der Container läuft als **non-root** (UID 1654). Das Volume auf `/data` ist kein Muss, aber ohne es
verliert der Aussperr-Schutz bei jedem Restart seine Historie – der Dienst warnt dann einmalig im Log.

### Healthcheck

Das Image bringt einen `HEALTHCHECK` mit. Nach jedem erfolgreichen Zyklus schreibt der Dienst eine Frist von
**2 × `IntervalMinutes` + 5 Minuten**; vergeht sie ohne Erfolg (Token widerrufen, NPM nicht erreichbar, DNS
dauerhaft kaputt), meldet der Container `unhealthy`:

```bash
docker ps                      # STATUS: Up 3 hours (healthy)
docker inspect --format '{{json .State.Health}}' ngx-pm-ip-updater
```

Der Check ruft nur `dotnet ngxPMIpUpdater.dll --healthcheck` auf und prüft die Frist-Datei – kein
HTTP-Port, kein curl im Image. Uptime Kuma, Portainer oder `autoheal` können den Status direkt auswerten.

### Mit docker-compose

```yaml
services:
  ngx-pm-ip-updater:
    build: .
    restart: unless-stopped
    environment:
      - AppSettings__Npm__BaseUrl=http://nginx-proxy-manager:81
      - AppSettings__Npm__ApiToken=dein-api-token
      - AppSettings__Npm__AccessListId=1
      - AppSettings__IntervalMinutes=60
    volumes:
      # Bewahrt die letzten guten IPs über einen Restart hinweg (Aussperr-Schutz)
      - ngx-pm-ip-updater-state:/data
    networks:
      - npm   # dasselbe Netz wie NPM, sonst ist "nginx-proxy-manager" nicht auflösbar

volumes:
  ngx-pm-ip-updater-state:

networks:
  npm:
    external: true
    name: nginx-proxy-manager_default   # prüfen mit: docker network ls
```

Die mitgelieferte `docker-compose.yml` liest den Netzwerknamen aus `NPM_NETWORK_NAME` in der `.env`.

---

## Lokal starten

```bash
cd ngxPMIpUpdater
dotnet run
```

---

## Tests

```bash
dotnet test
```

## Projektstruktur

```
ngxPMIpUpdater/
├── Models/
│   ├── AppSettings.cs       # Konfigurationsmodell
│   ├── CloudflareModels.cs  # Cloudflare Access Group (Roh-JSON, damit nichts verloren geht)
│   └── NpmModels.cs         # NPM API Datenmodelle (Token, AccessList, Client)
├── Services/
│   ├── AppSettingsValidator.cs # Startup-Validierung (fail fast)
│   ├── ClientRules.cs          # Ziel-Regeln der Access List (inkl. deny-all-Sperre)
│   ├── ConfigList.cs           # Komma-Listen und JSON-Arrays vereinheitlichen
│   ├── DnsResolverService.cs   # DNS-Auflösung → CIDR
│   ├── ChangeNotification.cs   # Änderung der Access List → Benachrichtigungstext
│   ├── CloudflareApiService.cs # Cloudflare API (GET/PUT Access Group, nur IP-Regeln)
│   ├── DynamicIpCollector.cs   # DNS-Ergebnisse + Fallback auf letzte gute IPs (mit Ablaufzeit)
│   ├── HealthMonitor.cs        # Frist-Datei für den Docker-Healthcheck, Ausfall-Erkennung
│   ├── IpValidator.cs          # Prüft/normalisiert alles, was zur allow-Regel wird
│   ├── LastKnownIpStore.cs     # Letzte gute IPs pro Domain (Aussperr-Schutz)
│   ├── Notifier.cs             # Push an ntfy / Gotify / JSON-Webhook
│   ├── NpmApiService.cs        # NPM REST API (Login, GET/PUT Access List)
│   └── SecretFiles.cs          # Docker-Secrets: *File-Einstellungen einlesen
├── Worker.cs                # Hintergrund-Worker-Loop
├── Program.cs               # Dependency Injection Setup
├── appsettings.json         # Konfigurationsdatei
└── Dockerfile               # Multi-stage Linux Docker Build
```

---

## Logs (Beispiel)

```
[17:42:00 INF] ngxPMIpUpdater.Worker - ngxPMIpUpdater started. Interval: 60 min, Domains: mein-haus.dyndns.org
[17:42:00 INF] ngxPMIpUpdater.Worker - Starting IP update cycle at 13.04.2026 17:42:00 +00:00
[17:42:00 INF] ngxPMIpUpdater.Services.NpmApiService - Using static NPM API token for authentication.
[17:42:00 INF] ngxPMIpUpdater.Services.NpmApiService - Fetched access list 'Meine Liste' (ID 1) with 2 client(s) and 1 auth user(s).
[17:42:00 INF] ngxPMIpUpdater.Services.NpmApiService - Access list 'Meine Liste' updated with 3 IP(s).
[17:42:00 INF] ngxPMIpUpdater.Worker - Update cycle completed. 3 IPs (1 dynamic, 2 static).
```

Die einzelnen IP-Adressen stehen absichtlich nur auf `Debug`: Heim-IPs sind personenbezogene Daten und
sollen nicht dauerhaft in den Container-Logs liegen. Zum Nachvollziehen einer Änderung genügt
`"MinimumLevel": { "Default": "Debug" }` in der Serilog-Konfiguration.

Hat sich nichts geändert, wird kein PUT gesendet – das erspart NPM einen nginx-Reload pro Stunde:

```
[18:42:00 INF] ngxPMIpUpdater.Services.NpmApiService - Access list 'Meine Liste' already holds the target 3 IP(s) – skipping update.
```
