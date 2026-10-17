# Janus RFID Platform — Plateforme Sécurisée de Billettique Virtuelle

[![CI/CD DevSecOps](https://github.com/nacefmoula/janus-rfid-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/nacefmoula/janus-rfid-platform/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4.svg)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-336791.svg)](https://www.postgresql.org/)
[![OpenTelemetry](https://img.shields.io/badge/OpenTelemetry-1.20-blue.svg)](https://opentelemetry.io/)
[![Prometheus](https://img.shields.io/badge/Prometheus-v2.54.1-e6522c.svg)](https://prometheus.io/)
[![Grafana](https://img.shields.io/badge/Grafana-11.2.0-f46800.svg)](https://grafana.com/)
[![Security: Trivy](https://img.shields.io/badge/Security-Trivy%20Clean-brightgreen.svg)](https://aquasecurity.github.io/trivy/)

Projet PFE d'ingénierie logicielle : plateforme générique de billettique RFID pour les réseaux de transport public, intégrant un pipeline **DevSecOps**, un débit transactionnel atomique, la protection contre les rejeux, une séparation stricte des privilèges (RBAC) et une observabilité complète (OpenTelemetry, Prometheus, Grafana).

---

## 1. Architecture Technique & Composants

* **Backend API :** ASP.NET Core 10 (Minimal APIs, C# 13, .NET 10 Release).
* **Persistance & Données :** PostgreSQL 18 orchestré avec Entity Framework Core 10 et migrations versionnées automatiques (`20261015151954_InitialCreate`, `20261015155513_AddFareTransactionsAndBalance`, `20261015165352_AddCardAuditLogs`).
* **Intégrité Financière :** Débit atomique sous verrouillage pessimiste PostgreSQL (`SELECT ... FOR UPDATE`), calcul en millimes entiers (aucun arrondi flottant), ledger immutable de transactions (`FareTransactions`).
* **Idempotence & Anti-Rejeu :** Clé unique composée `(TerminalId, EventId)` avec hachage SHA-256 canonique du payload pour distinguer les rejeux légitimes (aucun débit supplémentaire) des conflits de payload (`409 Conflict`).
* **Contrôle d'Accès (RBAC) & Moindre Privilège :**
  * `TerminalAuth:DemoToken` / `TerminalAuth:Terminal2Token` : Rôles bornes de validation (`POST /api/validations`, `GET /api/cards/{uid}`, `GET /api/simulator/cards`, `GET /api/dashboard/summary`).
  * `AdminAuth:DemoToken` : Rôle administrateur (`PUT /api/cards/{uid}`, `GET /api/cards/{uid}/audit`, `GET /api/demo/cards`).
  * `MetricsAuth:ScrapeToken` : Rôle collecteur Prometheus (`PROMETHEUS-SCRAPER`) limité exclusivement à `GET /metrics` (aucun accès aux cartes, validations ou audits).
* **Observabilité & Télémétrie :**
  * OpenTelemetry .NET 10 (`JanusMetrics`) avec histogramme de latence sub-seconde explicite et compteurs de sécurité (`janus_security_auth_failures_total`, `janus_security_authz_denials_total`).
  * Prometheus 2.54.1 (scraping toutes les 2s sur `GET /metrics` avec jeton dédié `METRICS_SCRAPE_TOKEN`, publié uniquement sur `127.0.0.1:9090`).
  * Grafana 11.2.0 lié à `127.0.0.1:3000` (accès anonyme désactivé par défaut `GRAFANA_ANONYMOUS_ENABLED=false`).
* **Pipeline DevSecOps :** GitHub Actions automatisant le build natif .NET 10, les suites de tests d'intégration (.NET, Python, Angular), le scénario E2E, le contrôle d'ingestion Prometheus et le scan de vulnérabilités/secrets conteneur Trivy.

---

## 2. Topologie des Services & Ports

| Service | Image | Interface & Port Hôte | Port Conteneur | Rôle / Accès |
| :--- | :--- | :---: | :---: | :--- |
| **Frontend Web** | `janus-rfid-platform-web` | `0.0.0.0:4280` | `80` | SPA Angular & Reverse-Proxy `/api/*`, `/health` (`http://localhost:4280`) |
| **API Janus** | `janus-rfid-platform-api` | `0.0.0.0:5080` | `8080` | API Kestrel (`http://localhost:5080`) |
| **PostgreSQL** | `postgres:18` | `0.0.0.0:5433` | `5432` | Base relationnelle (`janus_db`) |
| **Prometheus** | `prom/prometheus:v2.54.1` | `127.0.0.1:9090` | `9090` | Collecteur de métriques (accès local uniquement : `http://127.0.0.1:9090`) |
| **Grafana** | `grafana/grafana:11.2.0` | `127.0.0.1:3000` | `3000` | Supervision locale authentifiée (`http://127.0.0.1:3000`) |

---

## 3. Démarrage Rapide

### Prérequis
* Linux (Ubuntu / Fedora / Debian recommandé) ou macOS / WSL2
* Docker Engine 24+ & Docker Compose v2
* Python 3.10+ (pour les scripts de test et de simulation)
* SDK .NET 10 (optionnel pour exécution locale hors conteneur)

### Lancement de la stack complète
```bash
# 1. Cloner le dépôt
git clone https://github.com/nacefmoula/janus-rfid-platform.git
cd janus-rfid-platform

# 2. Configurer les variables d'environnement locales (optionnel, valeurs par défaut sécurisées pour le dev)
cp .env.example .env

# 3. Démarrer l'ensemble des services isolés
docker compose up --build -d

# 4. Vérifier l'état de santé de l'API et des services
curl -s http://localhost:5080/health
# Réponse attendue : {"status":"healthy","service":"janus-api"}
```

---

## 4. Simulation & Démonstrateur E2E

Le dépôt intègre deux simulateurs Python permettant de valider les flux sans matériel RFID physique :

### A. Scénario de démonstration complet (Idéal pour soutenance)
Exécute le parcours complet de bout en bout (création administrative d'une carte, passage nominal, rejeu idempotent exact, épuisement de solde, rejets solde insuffisant et carte révoquée, isolation des rôles RBAC et consultation d'audit) :
```bash
python3 simulator/demo_scenario.py
```

### B. Simulateur de bornes interactif
```bash
# Validation nominale (carte active avec solde suffisant)
python3 simulator/janus_simulator.py

# Simulation de coupure réseau / rejeu exact de l'événement (duplicate=true)
python3 simulator/janus_simulator.py --mode replay

# Simulation d'attaque par falsification de payload sur le même eventId (409 Conflict)
python3 simulator/janus_simulator.py --mode conflict

# Validation sur carte révoquée (rejet CARD_REVOKED)
python3 simulator/janus_simulator.py --mode revoked

# Carte inconnue (rejet UNKNOWN_CARD)
python3 simulator/janus_simulator.py --mode unknown

# Jeton d'authentification de borne invalide (401 Unauthorized)
python3 simulator/janus_simulator.py --mode bad-token
```

---

## 5. Tests Automatiques & Observabilité

### A. Tests d'intégration et de concurrence .NET 10 (xUnit + PostgreSQL)
```bash
dotnet test tests/Janus.Api.Tests/Janus.Api.Tests.csproj
```
Exécute 14 tests déterministes pilotés par `SimulatedTimeProvider` sous PostgreSQL réel : fenêtre temporelle anti-passback, concurrence réelle pessimiste (`FOR UPDATE`), rejeux idempotents prioritaires, cartes révoquées, solde insuffisant et multi-bornes.

### B. Suite complète de 31 tests d'intégration HTTP (Python)
```bash
python3 tests/test_api.py
```
Couvre l'ensemble des flux API : concurrence pessimiste sous charge, transactions concurrentes avec solde partagé, pagination des historiques, conservation des statuts d'audit, contrôles d'accès RBAC et règles d'anti-passback.

### C. Tests unitaires frontend Angular
```bash
cd src/Janus.Web && npm test -- --watch=false
```
Exécute 36 tests unitaires Karma / Headless Chrome validant les composants, services, simulateur et affichage de l'anti-passback.

### D. Vérifier l'ingestion des métriques Prometheus
```bash
# Vérifie en direct que Prometheus collecte les séries attendues (ACCEPTED, REJECTED, CONFLICT, PASSBACK_VIOLATION)
python3 tests/verify_metrics.py

# Exécuter les auto-tests unitaires du vérificateur d'observabilité
python3 tests/verify_metrics.py --self-test
```

### Visualiser dans Grafana (Administration locale)
Accédez localement à **`http://127.0.0.1:3000`** :
* **Isolation réseau :** Les ports de Prometheus (`127.0.0.1:9090`) et Grafana (`127.0.0.1:3000`) sont liés exclusivement à l'interface de bouclage locale (`127.0.0.1`) et ne sont pas exposés sur les interfaces externes. Le reverse-proxy Nginx public (`:4280`) n'expose pas `/metrics`.
* **Authentification requise par défaut :** L'accès anonyme à Grafana est désactivé par défaut (`GRAFANA_ANONYMOUS_ENABLED=false`). Connectez-vous avec les identifiants définis dans `.env` (`GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD`).
* **Dashboard préconfiguré :** Rendez-vous dans *Dashboards* $\rightarrow$ **Janus RFID — Observabilité & Métriques DevSecOps** (`janus-rfid-overview`).
* **Indicateurs affichés :** Taux d'acceptation, débit de validations, latence sub-seconde réelle (p50 / p95 / p99), ventilation des motifs de refus, recettes collectées en Dinars Tunisiens (DT), pool de connexions Npgsql et mémoire runtime .NET 10.

---

## 6. Endpoints de l'API REST & Limitation de Débit

| Méthode | Route | Rôle requis | Politique Rate-Limit (défaut) | Description |
| :--- | :--- | :---: | :---: | :--- |
| `POST` | `/api/validations` | **Terminal** | `ValidationsPolicy` (30 req/s, burst 60 / borne) | Validation d'un passage RFID avec débit atomique et protection d'idempotence. |
| `GET` | `/api/cards/{uid}` | **Terminal / Admin** | `CardsReadPolicy` (60 req/s / identité) | Consultation du solde, du statut et de l'historique des transactions (`?limit=10&offset=0`). |
| `GET` | `/api/simulator/cards` | **Terminal / Admin** | `CardsReadPolicy` (60 req/s / identité) | Liste minimale des cartes (`uid`, `status`, `label`, `balanceMillimes`) pour le simulateur RFID. |
| `GET` | `/api/dashboard/summary` | **Terminal / Admin** | `CardsReadPolicy` (60 req/s / identité) | Synthèse métier agrégée pour le tableau de bord Angular (sans exposer `/metrics`). |
| `GET` | `/api/demo/cards` | **Admin** | `AdminPolicy` (15 req/s / admin) | Registre administratif des cartes. |
| `PUT` | `/api/cards/{uid}` | **Admin** | `AdminPolicy` (15 req/s / admin) | Création, rechargement ou mise à jour de statut avec enregistrement de trace d'audit. |
| `GET` | `/api/cards/{uid}/audit` | **Admin** | `AdminPolicy` (15 req/s / admin) | Consultation du journal d'audit administratif paginé (interdit aux bornes avec `403 Forbidden`). |
| `GET` | `/metrics` | **Scraper / Admin** | `CardsReadPolicy` (60 req/s / identité) | Exportation des métriques OpenTelemetry au format Prometheus (`MetricsAuth:ScrapeToken`). |
| `GET` | `/health` | *Public* | `GlobalLimiter` (30 req/s / IP anonyme) | Sonde de santé de l'API. |

---

## 7. Règles DevSecOps & Maîtrise de la Cardinalité

* **Zéro fuite d'UID dans les métriques :** Pour respecter le RGPD et prévenir toute explosion de cardinalité (*cardinality bomb*) dans les séries temporelles de Prometheus, aucun identifiant de carte (`cardUid`), jeton ou horodatage brut n'est injecté comme label de métrique.
* **Labels strictement finis :** Seules les dimensions `decision` (`ACCEPTED`, `REJECTED`, `CONFLICT`), `reason` (`ACTIVE_CARD`, `INSUFFICIENT_FUNDS`, `PASSBACK_VIOLATION`, etc.), `duplicate` (`true`, `false`) et `terminal_id` sont exposées.
* **Règle Anti-Passback configurable :** Empêche le passage successif frauduleux d'un même titre sur un même terminal dans une fenêtre glissante (30s par défaut, paramétrable via `ANTIPASSBACK_WINDOW_SECONDS` ou désactivable via `ANTIPASSBACK_ENABLED`). L'évaluation s'exécute sous verrou pessimiste SQL (`FOR UPDATE`) basé exclusivement sur l'horloge serveur (`ReceivedAt`), préservant strictement l'idempotence préalable. Tout refus `PASSBACK_VIOLATION` est sans débit financier.
* **Audit administratif isolé :** Les traces nominatives d'audit sont stockées en base relationnelle protégée et accessibles uniquement par le personnel habilité via l'endpoint sécurisé `/api/cards/{uid}/audit`.

---

## 8. Arrêt et Réinitialisation

```bash
# Arrêter les conteneurs en conservant les volumes de données
docker compose down

# Réinitialisation complète (effacement des volumes PostgreSQL, Prometheus et Grafana)
docker compose down -v
```

---

## 9. Limites Académiques & Périmètre PFE

* **Couche matérielle :** Le projet se concentre sur l'architecture logicielle distribuée, la cohérence financière transactionnelle, la sécurité d'accès et l'observabilité. Il émule les identifiants UID sans prétendre remplacer la cryptographie d'une puce physique MIFARE DESFire EV3 (authentification mutuelle 3DES/AES).
* **Conditions de mesure des performances :** Les latences mesurées localement (médiane ~3.5 ms, p95 ~8.7 ms) reflètent le temps de traitement serveur (API Kestrel + EF Core Npgsql + transaction PostgreSQL). Elles ne constituent pas un SLA de production intégrant les délais radio physiques RFID (50-150 ms) ou la latence de réseaux cellulaires distants (4G/5G).

---

## Licence & Auteur
* **Auteur :** Nacef Moula (`nacef.moula@esprit.tn`)
* **Projet :** Projet de Fin d'Études (PFE) — ESPRIT
