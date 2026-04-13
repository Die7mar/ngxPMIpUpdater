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
Werden **keine** IPs aufgelöst und keine statischen Blöcke konfiguriert, wird das Update übersprungen – so kann sich kein Nutzer selbst aussperren.

---

## Konfiguration

Alle Einstellungen befinden sich in `appsettings.json` (oder als Umgebungsvariablen für Docker):

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

      "ApiToken": "dein-langer-api-token-aus-dem-npm-ui",

      "Email": "admin@example.com",
      "Password": "geheimesPasswort",

      "AccessListId": 1
    }
  }
}
```

| Feld | Beschreibung |
|------|-------------|
| `IntervalMinutes` | Wie oft (in Minuten) die IPs aktualisiert werden (Standard: 60) |
| `Domains` | Liste von Hostnamen/DynDNS-Domains – werden per DNS aufgelöst |
| `StaticIpBlocks` | Feste IP-Adressen oder CIDR-Blöcke (z. B. internes Netz), die immer erlaubt bleiben |
| `Npm.BaseUrl` | URL des Nginx Proxy Manager (inkl. Port, ohne `/api`) |
| `Npm.ApiToken` | **Empfohlen (NPM ≥ v2.13):** Langlebiger API-Token, erstellt im NPM UI unter *Access → API Tokens*. Wenn gesetzt, werden Email/Password ignoriert. |
| `Npm.Email` | Login-E-Mail – nur wenn kein ApiToken gesetzt (ältere NPM-Versionen) |
| `Npm.Password` | Passwort – nur wenn kein ApiToken gesetzt (ältere NPM-Versionen) |
| `Npm.AccessListId` | ID der zu aktualisierenden NPM Access List |

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
  -e AppSettings__Npm__BaseUrl=http://192.168.1.10:81 \
  -e AppSettings__Npm__Email=admin@example.com \
  -e AppSettings__Npm__Password=geheimesPasswort \
  -e AppSettings__Npm__AccessListId=1 \
  ngx-pm-ip-updater
```

> Umgebungsvariablen überschreiben Werte aus `appsettings.json`.  
> Doppelter Unterstrich `__` entspricht einem Doppelpunkt `:` in der Konfigurationshierarchie.

### Mit docker-compose

```yaml
services:
  ngx-pm-ip-updater:
    build: .
    restart: unless-stopped
    environment:
      - AppSettings__Npm__BaseUrl=http://nginx-proxy-manager:81
      - AppSettings__Npm__Email=admin@example.com
      - AppSettings__Npm__Password=geheimesPasswort
      - AppSettings__Npm__AccessListId=1
      - AppSettings__IntervalMinutes=60
```

---

## Lokal starten

```bash
cd ngxPMIpUpdater
dotnet run
```

---

## Projektstruktur

```
ngxPMIpUpdater/
├── Models/
│   ├── AppSettings.cs       # Konfigurationsmodell
│   └── NpmModels.cs         # NPM API Datenmodelle (Token, AccessList, Client)
├── Services/
│   ├── DnsResolverService.cs  # DNS-Auflösung → IPv4 → CIDR /32
│   └── NpmApiService.cs       # NPM REST API (Login, GET/PUT Access List)
├── Worker.cs                # Hintergrund-Worker-Loop
├── Program.cs               # Dependency Injection Setup
├── appsettings.json         # Konfigurationsdatei
└── Dockerfile               # Multi-stage Linux Docker Build
```

---

## Logs (Beispiel)

```
info: ngxPMIpUpdater.Worker[0]
      ngxPMIpUpdater started. Interval: 60 min, Domains: mein-haus.dyndns.org
info: ngxPMIpUpdater.Worker[0]
      Starting IP update cycle at 13.04.2026 17:42:00 +00:00
info: ngxPMIpUpdater.Services.NpmApiService[0]
      Fetched access list 'Meine Liste' (ID 1) with 2 client(s).
info: ngxPMIpUpdater.Services.NpmApiService[0]
      Access list 'Meine Liste' updated with 3 IP(s): 1.2.3.4/32, 192.168.1.0/24, 10.0.0.5/32
info: ngxPMIpUpdater.Worker[0]
      Update cycle completed. 3 IPs written (1 dynamic, 2 static).
```
