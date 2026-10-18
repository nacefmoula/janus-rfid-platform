# Janus RFID Platform — Plateforme Sécurisée de Billettique Virtuelle

[![CI / DevSecOps Pipeline](https://github.com/nacefmoula/janus-rfid-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/nacefmoula/janus-rfid-platform/actions/workflows/ci.yml)
[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=nacefmoula_janus-rfid-platform&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=nacefmoula_janus-rfid-platform)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4.svg)](https://dotnet.microsoft.com/)
[![Angular](https://img.shields.io/badge/Angular-19.0-dd0031.svg)](https://angular.dev/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-336791.svg)](https://www.postgresql.org/)
[![OpenTelemetry](https://img.shields.io/badge/OpenTelemetry-1.20-blue.svg)](https://opentelemetry.io/)
[![Prometheus](https://img.shields.io/badge/Prometheus-v2.54.1-e6522c.svg)](https://prometheus.io/)
[![Grafana](https://img.shields.io/badge/Grafana-11.2.0-f46800.svg)](https://grafana.com/)
[![Security: Trivy](https://img.shields.io/badge/Security-Trivy%20Clean-brightgreen.svg)](https://aquasecurity.github.io/trivy/)

Projet PFE d'ingénierie logicielle (ESPRIT) : plateforme générique de billettique RFID pour les réseaux de transport public, intégrant un pipeline **DevSecOps**, un débit transactionnel atomique, une protection avancée contre les rejeux et fraudes (Anti-Passback), une séparation stricte des privilèges (RBAC) et une observabilité complète (OpenTelemetry, Prometheus, Grafana).

---

## 1. Présentation du Projet

### A. Contexte et Objectifs
Dans un réseau de transport en commun moderne, la billettique dématérialisée repose sur des flux transactionnels critiques. Chaque validation de titre de transport à une borne doit répondre à des exigences strictes :
- **Intégrité financière absolue :** Impossibilité physique d'engendrer un solde négatif ou une double facturation, même sous concurrence extrême.
- **Résilience réseau :** Traitement transparent des rejeux consécutifs à des micro-coupures de liaison entre les bornes et le système central.
- **Protection anti-fraude :** Empêcher le passage immédiat de plusieurs usagers avec un même titre (règle Anti-Passback).
- **Cloisonnement des privilèges :** Impossibilité pour un valideur physique d'accéder aux données comptables ou administratives sensibles.
- **Observabilité sub-seconde :** Suivi temps réel des débits, des latences et des anomalies sans dégrader les performances ni exposer de données à caractère personnel (RGPD).

### B. Périmètre Implémenté : Opérationnel vs Simulation
Pour garantir une évaluation rigoureuse en environnement académique et de test, le projet distingue explicitement :
* **Composants pleinement opérationnels :**
  - Moteur d'évaluation billettique et de débit financier en base de données relationnelle sous verrouillage pessimiste SQL.
  - Détection d'idempotence exacte par empreinte cryptographique SHA-256 canonique.
  - Moteur Anti-Passback multi-terminaux à fenêtre temporelle glissante configurable.
  - Authentification par jetons Bearer protégée contre les attaques temporelles (*timing attacks*).
  - Contrôle d'accès basé sur les rôles (RBAC) avec 6 politiques d'autorisation distinctes.
  - Journal d'audit administratif complet et immuable des cartes.
  - Limitation de débit adaptative par identité (*Rate Limiting*) et proxy de confiance.
  - Métriques OpenTelemetry sub-seconde, scraping Prometheus et tableau de bord Grafana provisionné.
  - Interface Single Page Application (SPA) Angular 19 servie par Nginx durci non-root.
* **Composants émulés et de démonstration :**
  - **Couche matérielle RFID :** Les badges physiques (MIFARE DESFire EV3) et les lecteurs NFC sont émulés logiciellement par des identifiants UID (format `04DEMO...` ou hexadécimal).
  - **Simulateurs de validation :** Un scénario de démonstration complet en 7 étapes (`simulator/demo_scenario.py`) et un simulateur interactif multi-modes (`simulator/janus_simulator.py`).
  - **Horloge simulée pour les tests :** L'émulateur `SimulatedTimeProvider` permet de valider de façon déterministe sous xUnit les expirations de fenêtres temporelles anti-passback sans dépendre du temps réel.

---

## 2. Architecture Technique & Composants

```
                    ┌────────────────────────────────────────────────────────┐
                    │                   Réseau Externe                       │
                    └──────────────────────────┬─────────────────────────────┘
                                               │
                                               │ Requêtes Web (:4280)
                                               ▼
┌───────────────────────────────────────────────────────────────────────────────────────────────┐
│ Réseau Docker Isolé (janus_net : 172.28.0.0/24)                                               │
│                                                                                               │
│  ┌───────────────────────────────┐                  ┌──────────────────────────────────────┐  │
│  │ janus-web (172.28.0.10)       │                  │ janus-api (127.0.0.1:5080)           │  │
│  │ SPA Angular 19                │  Proxy HTTP/1.1  │ ASP.NET Core 10 (Minimal APIs)       │  │
│  │ Nginx 1.27 Alpine (Non-root)  ├─────────────────►│ Kestrel (Port 8080)                  │  │
│  │ Port conteneur : 8080         │  (/api/*)        │ Middleware : RBAC, Rate-Limit, Auth  │  │
│  └───────────────────────────────┘                  └──────────────────┬───────────────────┘  │
│                                                                        │                      │
│                                                    EF Core 10 / Npgsql │ Port 5432            │
│                                                    (Verrou FOR UPDATE) │                      │
│                                                                        ▼                      │
│                                                     ┌──────────────────────────────────────┐  │
│                                                     │ db (127.0.0.1:5433)                  │  │
│                                                     │ PostgreSQL 18                        │  │
│                                                     │ Tables: Cards, Validations,          │  │
│                                                     │ FareTransactions, CardAuditLogs      │  │
│                                                     │ Contrainte: CK Balance >= 0          │  │
│                                                     └──────────────────────────────────────┘  │
│                                                                        ▲                      │
│  ┌───────────────────────────────┐                                     │                      │
│  │ janus-grafana                 │                                     │                      │
│  │ Grafana 11.2.0                │                                     │                      │
│  │ Port hôte: 127.0.0.1:3000     │                                     │                      │
│  │ Dashboard: janus-overview     │                                     │                      │
│  └───────────────▲───────────────┘                                     │                      │
│                  │ PromQL                                              │                      │
│  ┌───────────────┴───────────────┐           Scrape HTTP toutes les 2s │                      │
│  │ janus-prometheus              ├─────────────────────────────────────┘                      │
│  │ Prometheus 2.54.1             │           (GET /metrics avec METRICS_SCRAPE_TOKEN)         │
│  │ Port hôte: 127.0.0.1:9090     │                                                            │
│  └───────────────────────────────┘                                                            │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
```

* **Backend API :** ASP.NET Core 10 (Minimal APIs, C# 13, .NET 10 Release).
* **Persistance & Données :** PostgreSQL 18 orchestré avec Entity Framework Core 10 via Npgsql. Cinq migrations séquentielles tracent l'historique de schéma dans `__EFMigrationsHistory` :
  1. `20261015151954_InitialCreate` : Tables `Cards` et `Validations`, index unique `IX_Cards_Uid`, index composite `IX_Validations_TerminalId_EventId`.
  2. `20261015155513_AddFareTransactionsAndBalance` : Ajout de la colonne `BalanceMillimes` et de la table financière immuable `FareTransactions`.
  3. `20261015165352_AddCardAuditLogs` : Table `CardAuditLogs` pour la traçabilité des modifications administratives.
  4. `20261016151210_AddAntiPassbackIndexOnValidations` : Index composite `IX_Validations_TerminalId_CardUid_Decision_ReceivedAt` pour l'évaluation sub-seconde de l'anti-passback.
  5. `20261016182125_AddCheckConstraintBalanceNonNegative` : Contrainte SQL native `CK_Cards_BalanceMillimes_NonNegative` interdisant les soldes négatifs.
* **Frontend Web :** Single Page Application en Angular 19 (`src/Janus.Web`), composants autonomes (*standalone*), réactivité par *signals*, interface Material Design avec simulateur temps réel et tableau de bord de supervision.
* **Reverse Proxy & Serveur Web :** Image Nginx 1.27 Alpine durcie s'exécutant sous l'utilisateur non privilégié `nginx` (UID 101) sur le port conteneur `8080`. Route les fichiers statiques de la SPA et relaie les requêtes `/api/` et `/health` vers Kestrel en écrasant l'en-tête `X-Forwarded-For`.
* **Authentification & RBAC :** Gestionnaire d'authentification personnalisé `JanusTokenAuthHandler` validant les jetons Bearer via `CryptographicOperations.FixedTimeEquals` et politique fluide `AddAuthorizationBuilder()` avec 6 politiques d'accès.
* **Observabilité & Télémétrie :**
  - OpenTelemetry .NET 10 exportant nativement au format Prometheus sur `GET /metrics`.
  - Prometheus 2.54.1 collectant les métriques toutes les 2 secondes avec jeton d'authentification dédié.
  - Grafana 11.2.0 préconfiguré avec source de données Prometheus et tableau de bord provisionné.
* **Pipeline DevSecOps :** Workflow automatisé GitHub Actions intégrant compilation .NET 10 Release, tests unitaires Angular Karma, tests d'intégration xUnit sous PostgreSQL réel, tests d'intégration Python, analyse de vulnérabilités conteneurs Trivy, détection de secrets GitGuardian et analyse de qualité SonarCloud.

---

## 3. Topologie des Services & Ports

Source de vérité : [`compose.yaml`](file:///home/jackal/Downloads/PFE_RFID_Virtuel_Complet/janus-rfid-platform/compose.yaml)

| Service | Image / Build | Interface & Port Hôte | Port Interne Conteneur | Adresse IP Réseau Interne | Description / Rôle |
| :--- | :--- | :---: | :---: | :---: | :--- |
| **`web`** | `./src/Janus.Web` | `0.0.0.0:4280` *(ou `${FRONTEND_PORT}`)* | `8080` | `172.28.0.10` | SPA Angular + Reverse Proxy Nginx non-root (`http://localhost:4280`) |
| **`api`** | `./src/Janus.Api` | `127.0.0.1:5080` | `8080` | Dynamique (`janus_net`) | API ASP.NET Core Kestrel (`http://localhost:5080`) |
| **`db`** | `postgres:18` | `127.0.0.1:5433` | `5432` | Dynamique (`janus_net`) | Base de données PostgreSQL (`janus_db`) |
| **`prometheus`** | `prom/prometheus:v2.54.1` | `127.0.0.1:9090` | `9090` | Dynamique (`janus_net`) | Collecteur de métriques sécurisé (`http://127.0.0.1:9090`) |
| **`grafana`** | `grafana/grafana:11.2.0` | `127.0.0.1:3000` | `3000` | Dynamique (`janus_net`) | Supervision graphique locale (`http://127.0.0.1:3000`) |

> **Sécurité réseau :** L'API (`:5080`), la base PostgreSQL (`:5433`), Prometheus (`:9090`) et Grafana (`:3000`) sont liés exclusivement à l'interface de bouclage locale `127.0.0.1`. Seul le port du frontend web (`:4280`) est exposé sur les interfaces externes. L'accès public à `/metrics` est bloqué au niveau du reverse-proxy Nginx.

---

## 4. Sécurité & Robustesse DevSecOps

### A. Séparation Stricte des Rôles (RBAC) & Moindre Privilège
* **Rôle `Terminal` :** Dédié aux bornes de validation physique.
  - Autorisé sur : `POST /api/validations`, `GET /api/cards/{uid}`, `GET /api/simulator/cards`, `GET /api/dashboard/summary`.
  - Rejet systématique avec `HTTP 403 Forbidden` sur les opérations administratives (`PUT /api/cards/{uid}`) et sur les journaux d'audit (`GET /api/cards/{uid}/audit`).
* **Rôle `Admin` :** Dédié aux opérateurs administratifs et commerciaux.
  - Autorisé sur : `PUT /api/cards/{uid}`, `GET /api/cards/{uid}/audit`, `GET /api/demo/cards`, `GET /api/cards/{uid}`.
  - Rejet avec `HTTP 401 Unauthorized` sur l'endpoint de validation des bornes (`POST /api/validations`) s'il n'utilise pas d'identité de borne valide.
* **Rôle `Scraper` :** Dédié à la sonde Prometheus.
  - Autorisé exclusivement sur : `GET /metrics`.
  - Rejet strict avec `HTTP 403 Forbidden` sur l'ensemble des autres routes applicatives (`REV-01`).

### B. Intégrité Financière & Concurrence
* **Calcul en millimes entiers :** Tous les montants financiers (`Fare:AmountMillimes`, solde de carte, débits) sont manipulés sous forme d'entiers stricts en millimes tunisiens (1 TND = 1 000 millimes). Aucun calcul n'utilise de virgule flottante (`float`/`double`), prévenant tout biais d'arrondi.
* **Verrou pessimiste (`SELECT ... FOR UPDATE`) :** Lors de chaque validation billettique ou rechargement administratif, la ligne correspondante dans la table `Cards` est verrouillée au niveau du moteur PostgreSQL. Les requêtes concurrentes ciblant la même carte sont sérialisées au niveau du SGBD.
* **Contrainte de solde natif :** La contrainte SQL `CK_Cards_BalanceMillimes_NonNegative` (`CHECK ("BalanceMillimes" >= 0)`) garantit au niveau physique de la base qu'aucune anomalie applicative ne peut rendre un solde négatif.
* **Ledger financier immuable :** Chaque débit accepté génère une transaction dans `FareTransactions` horodatée en UTC, avec référence de terminal, identifiant d'événement et solde résultant.

### C. Idempotence & Moteur Anti-Passback
* **Idempotence canonique :** Chaque événement de validation comporte un identifiant unique `eventId` (UUID) généré par la borne. L'API calcule l'empreinte SHA-256 canonique du payload reçu.
  - **Rejeu exact :** Si le même `eventId` est renvoyé avec un payload identique (coupure réseau amont), l'API renvoie le résultat d'origine (`duplicate=true`) sans réappliquer de débit financier.
  - **Conflit de payload :** Si un même `eventId` est soumis avec un contenu altéré, l'API rejette immédiatement la requête avec `HTTP 409 Conflict` (`idempotency_conflict`) et n'applique aucun débit.
* **Moteur Anti-Passback :** Empêche le passage successif d'un même titre sur une même borne dans une fenêtre glissante (par défaut 30 secondes, configurable via `ANTIPASSBACK_WINDOW_SECONDS`).
  - L'évaluation s'exécute sous le verrou SQL de la carte, en interrogeant l'index composite dédié `IX_Validations_TerminalId_CardUid_Decision_ReceivedAt`.
  - L'horodatage de référence est l'horloge serveur (`ReceivedAt`), interdisant toute manipulation d'horloge côté terminal.
  - La détection d'anti-passback produit un rejet `PASSBACK_VIOLATION` sans débit financier, tout en préservant la priorité du rejeu idempotent.

### D. Limitation de Débit & Proxies de Confiance
* **Limitation de débit adaptative :** Implémentée nativement via `Microsoft.AspNetCore.RateLimiting` avec partitionnement par identité authentifiée :
  - `ValidationsPolicy` : 30 req/s (burst 60) par borne.
  - `CardsReadPolicy` : 60 req/s par identité.
  - `AdminPolicy` : 15 req/s par administrateur.
  - `GlobalLimiter` : 30 req/s par adresse IP pour les requêtes publiques anonymes.
* **Protection contre l'usurpation d'IP :**
  - Le reverse-proxy Nginx écrase systématiquement l'en-tête client `X-Forwarded-For` avec l'adresse IP de connexion réelle `$remote_addr`.
  - L'API Kestrel n'accepte les en-têtes transférés que depuis l'adresse IP statique explicitement approuvée du conteneur web (`172.28.0.10`).

### E. Durcissement des Conteneurs & Gestion des Secrets
* **Conteneur frontend non-root :** Nginx s'exécute sous le compte non privilégié `nginx` (UID 101) et écoute sur le port non privilégié `8080`.
* **Conteneur API non-root :** Kestrel s'exécute sous l'utilisateur standard non-root du SDK .NET (`USER $APP_UID`).
* **Protection contre les attaques de chaîne d'approvisionnement :** L'installation des paquets npm dans l'image Angular utilise `npm ci --ignore-scripts` pour neutraliser l'exécution de scripts malveillants au build.
* **Contrôle strict des secrets au démarrage :** En environnement autre que `Development`, l'API valide au démarrage que les secrets configurés ne correspondent pas aux valeurs par défaut, comportent au moins 16 caractères et sont distincts les uns des autres.

---

## 5. Endpoints de l'API REST

| Méthode | Route | Rôle Requis | Politique de Débit | Description |
| :--- | :--- | :---: | :---: | :--- |
| `POST` | `/api/validations` | **Terminal** | `ValidationsPolicy` (30 req/s) | Validation d'un passage RFID avec débit atomique, contrôle anti-passback et idempotence. |
| `GET` | `/api/cards/{uid}` | **Terminal / Admin** | `CardsReadPolicy` (60 req/s) | Consultation d'une carte et historique paginé de ses transactions (`?limit=50&offset=0`, max: 100). |
| `GET` | `/api/simulator/cards` | **Terminal / Admin** | `CardsReadPolicy` (60 req/s) | Liste minimale des cartes (`uid`, `status`, `label`) pour le simulateur (principe de moindre privilège, solde exclu). |
| `GET` | `/api/dashboard/summary` | **Terminal / Admin** | `CardsReadPolicy` (60 req/s) | Synthèse métier agrégée pour le tableau de bord Angular (sans exposer `/metrics`). |
| `GET` | `/api/demo/cards` | **Admin** | `AdminPolicy` (15 req/s) | Registre administratif exhaustif des cartes pour la gestion centrale. |
| `PUT` | `/api/cards/{uid}` | **Admin** | `AdminPolicy` (15 req/s) | Création, rechargement de solde ou mise à jour de statut avec enregistrement de trace d'audit. |
| `GET` | `/api/cards/{uid}/audit` | **Admin** | `AdminPolicy` (15 req/s) | Consultation paginée du journal d'audit administratif (interdit aux bornes : `403 Forbidden`). |
| `GET` | `/metrics` | **Scraper / Admin** | `CardsReadPolicy` (60 req/s) | Exportation des métriques OpenTelemetry au format Prometheus (`MetricsAuth:ScrapeToken`). |
| `GET` | `/health` | *Public* | `GlobalLimiter` (30 req/s) | Sonde de santé de l'API (`{"status":"healthy","service":"janus-api"}`). |

---

## 6. Installation & Démarrage Rapide

### Prérequis
* Linux (Ubuntu / Debian / Fedora), macOS ou Windows avec WSL2.
* Docker Engine 24+ et Docker Compose v2.
* Python 3.10+ (pour l'exécution des tests et simulateurs).
* SDK .NET 10 et Node.js 22 (optionnels, uniquement pour la compilation locale hors conteneur).

### Procédure de Démarrage

```bash
# 1. Cloner le dépôt
git clone https://github.com/nacefmoula/janus-rfid-platform.git
cd janus-rfid-platform

# 2. Configurer les variables d'environnement locales
# Note : Les valeurs du fichier .env.example sont des secrets de développement local.
# Elles doivent impérativement être modifiées en environnement de production.
cp .env.example .env

# 3. Démarrer l'ensemble des conteneurs
docker compose up --build -d

# 4. Vérifier l'état de santé des services
curl -s http://localhost:5080/health
# Réponse attendue : {"status":"healthy","service":"janus-api"}

curl -s http://localhost:4280
# Réponse attendue : Document HTML de la Single Page Application Angular

# 5. Accéder aux interfaces web
# - Application Web Janus (Simulateur & Dashboard) : http://localhost:4280
# - Tableau de bord Grafana (Supervision)        : http://localhost:3000 (admin / dev-grafana-password-change-me)
# - Métriques brutes Prometheus (Accès local)    : http://localhost:9090
```

### Arrêt et Nettoyage

```bash
# Arrêter les conteneurs en conservant les données de la base et de télémétrie
docker compose down

# Réinitialisation complète (suppression des conteneurs et des volumes persistants)
docker compose down -v
```

---

## 7. Simulateurs & Parcours de Démonstration

### A. Scénario de Démonstration Automatisé (`demo_scenario.py`)
Exécute en 7 étapes le parcours de bout en bout illustrant l'ensemble des exigences du projet :
```bash
python3 simulator/demo_scenario.py
```
* **Étape 1 :** Création administrative d'une carte avec dotation initiale de 3 000 millimes.
* **Étape 2 :** Premier passage RFID sur borne `TERMINAL-DEMO-01` (débit de 1 500 millimes, solde 1 500 millimes).
* **Étape 3 :** Simulation d'une coupure réseau avec rejeu exact du même événement (détection d'idempotence, aucun débit supplémentaire).
* **Étape 4 :** Tentative de repassage immédiat sur la même borne (rejet `PASSBACK_VIOLATION`, solde préservé).
* **Étape 5 :** Validation légitime sur une autre borne `TERMINAL-DEMO-02` (solde débité à 0 millime).
* **Étape 6 :** Cas d'erreur (rejet `INSUFFICIENT_FUNDS` pour solde insuffisant et `CARD_REVOKED` pour carte révoquée).
* **Étape 7 :** Contrôle RBAC et audit (rejet `403 Forbidden` pour la borne sur `/audit`, consultation réussie par l'administrateur).

### B. Simulateur de Bornes Interactif (`janus_simulator.py`)
```bash
# Validation nominale d'une carte active
python3 simulator/janus_simulator.py

# Simulation de coupure réseau et rejeu idempotent
python3 simulator/janus_simulator.py --mode replay

# Simulation d'attaque par falsification de payload sur même eventId (409 Conflict)
python3 simulator/janus_simulator.py --mode conflict

# Validation sur carte révoquée (rejet CARD_REVOKED)
python3 simulator/janus_simulator.py --mode revoked

# Validation sur carte inconnue (rejet UNKNOWN_CARD)
python3 simulator/janus_simulator.py --mode unknown

# Utilisation d'un jeton de borne invalide (401 Unauthorized)
python3 simulator/janus_simulator.py --mode bad-token
```

---

## 8. Tests Automatisés & Résultats Validés

Les métriques et résultats présentés ci-dessous correspondent à l'état vérifié du dépôt au commit de référence `222d8f9dd40fb1caca5666bab886ba0dc3b51928` (18 octobre 2026). Ces chiffres reflètent la référence d'évaluation factuelle observée lors de l'exécution des suites de tests et ne constituent pas une garantie permanente sans ré-exécution des suites de tests :

| Suite de Tests | Commande d'Exécution | Résultats Observés *(18 Octobre 2026)* | Couverture & Portée |
| :--- | :--- | :---: | :--- |
| **Tests .NET 10 (xUnit)** | `dotnet test tests/Janus.Api.Tests/Janus.Api.Tests.csproj -c Release` | **40 / 40 passés** (0 échec, 0 ignoré) | Verrouillage pessimiste, concurrence financière, anti-passback avec `SimulatedTimeProvider`, contrainte CHECK. |
| **Tests Angular (Karma)** | `cd src/Janus.Web && npm test -- --watch=false` | **79 / 79 passés** (0 échec) | Composants de navigation, simulateur RFID, services de session, formulaires administratifs. |
| **Tests Intégration API (Python)** | `python3 tests/test_api.py` | **38 / 38 passés** (0 échec) | Concurrence sous charge réelle, pagination, politiques de sécurité RBAC, rejeux et conflits. |
| **Scénario E2E (Python)** | `python3 simulator/demo_scenario.py` | **7 / 7 étapes validées** | Parcours complet d'une carte de la création à l'épuisement de solde et l'audit. |
| **Observabilité Prometheus** | `python3 tests/verify_metrics.py` | **10 séries actives validées** | Vérification de l'ingestion Prometheus, cardinalité stricte et histogrammes de latence. |
| **Compilation de Production** | `dotnet build src/Janus.Api/Janus.Api.csproj -c Release`<br>`cd src/Janus.Web && npm run build -- --configuration production` | **Succès (0 erreur, 0 avertissement applicatif)** | Bundles de production Angular minifiés et binaire API optimisé. |

---

## 9. Pipeline CI/CD DevSecOps

Le pipeline d'intégration continue GitHub Actions ([`.github/workflows/ci.yml`](file:///home/jackal/Downloads/PFE_RFID_Virtuel_Complet/janus-rfid-platform/.github/workflows/ci.yml)) s'exécute sur chaque push et pull request ciblant `main` :

1. **`1. Build .NET 10 (Release)` :** Restauration NuGet et compilation stricte de l'API et des projets de tests.
2. **`2. Frontend Angular (Tests & Build)` :** Installation avec `npm ci --ignore-scripts`, exécution des 79 tests unitaires Karma sous Chrome Headless, génération de la couverture LCOV et compilation du bundle de production.
3. **`3. Integration Tests & E2E Validation` :** Démarrage d'une stack éphémère isolée via Docker Compose, vérification des sondes de santé, exécution de la suite xUnit .NET (40 tests), des 38 tests d'intégration Python (exécutés via `python3 tests/test_api.py`, l'étape conservant le libellé historique `Run 31 Integration Tests`), du scénario de démo en 7 étapes et validation de l'ingestion Prometheus.
4. **`4. Container Security Scan (Trivy DevSecOps)` :** Analyse de vulnérabilités des conteneurs (OS Ubuntu 24.04 et dépendances .NET), publication du rapport SARIF dans GitHub Security et contrôle bloquant (*Quality Gate*) sur les vulnérabilités actionnables de niveau `CRITICAL`.
5. **`5. SonarCloud Code Analysis & Quality Gate` :** Analyse statique consolidée du code C# et TypeScript, consolidation des rapports de couverture OpenCover et LCOV, évaluation du Quality Gate SonarCloud.
6. **GitGuardian Security Checks :** Analyse automatisée des commits pour interdire tout dépôt de secret ou de clé privée.

---

## 10. État du Projet, Limites & Travaux Futurs

### A. Jalons Réalisés (Lots P1 à P7B)
- **Lots P1 à P5 :** Architecture initiale, débit atomique PostgreSQL, idempotence canonique, ledger comptable, traçabilité d'audit, métriques OpenTelemetry/Prometheus, tableaux de bord Grafana et Single Page Application Angular 19.
- **Lot P6A :** Hygiène Docker et durcissement des builds (élimination des scripts npm non vérifiés, conteneur Nginx non-root).
- **Lot P7A :** Résolution des diagnostics Roslyn dans le code applicatif (`ASP0025`, `CA1873`, `ASP0027`).
- **Lot P7B :** Résolution des diagnostics Roslyn dans la suite de tests (`CA1822`, `CA1861`).
- **Lot P7C :** Audit formel des migrations EF Core historiques et confirmation de leur intégrité.

### B. Diagnostics Historiques Acceptés (Migrations EF Core)
SonarCloud et Roslyn signalent 3 avertissements `CA1861` (*Prefer 'static readonly' fields over constant array arguments*) dans les classes de migration EF Core :
- `20261015151954_InitialCreate.cs:60` (`columns: new[] { "TerminalId", "EventId" }`)
- `20261015155513_AddFareTransactionsAndBalance.cs:55` (`columns: new[] { "TerminalId", "EventId" }`)
- `20261016151210_AddAntiPassbackIndexOnValidations.cs:16` (`columns: new[] { "TerminalId", "CardUid", "Decision", "ReceivedAt" }`)

**Décision d'ingénierie documentée :** Ces fichiers sont des artefacts historiques auto-générés par l'outillage officiel Entity Framework Core (`dotnet ef migrations add`). La méthode `Up()` n'étant exécutée qu'une seule fois au déploiement initial de la base et jamais en boucle d'exécution critique, l'allocation éphémère d'un tableau constant n'a aucun impact sur les performances. Conformément aux bonnes pratiques de conservation des artefacts de persistance, ces fichiers sont maintenus intacts sans champs statiques artificiels.

### C. Limites Connues et Travaux Complémentaires
* **Absence de module matériel cryptographique physique :** Le projet se concentre sur l'architecture logicielle, la cohérence transactionnelle et la sécurité applicative. Il émule les identifiants UID sans intégrer de module SAM matériel (*Secure Access Module*) ni implémenter l'authentification mutuelle cryptographique d'une puce physique MIFARE DESFire EV3 (AES-128).
* **Environnement de mesure de latence :** Les latences observées en local (~3 à 9 ms) mesurent le traitement logiciel serveur (Kestrel, Npgsql, PostgreSQL). Elles ne constituent pas un engagement de niveau de service (SLA) de production, qui dépendrait des délais de couplage radio RFID (50 à 150 ms) et des réseaux cellulaires distants (4G/5G).
* **Statut du projet :** Le projet valide l'ensemble des exigences académiques, fonctionnelles et DevSecOps d'un Projet de Fin d'Études. Il ne doit pas être qualifié de « prêt pour la production » sans la mise en œuvre préalable d'un gestionnaire d'infrastructure de clés (KMS / HashiCorp Vault), d'un audit de sécurité physique et de tests de charge multi-régions.

---

## 11. Licence & Auteur

* **Auteur :** Nacef Moula (`nacef.moula@esprit.tn`)
* **Projet :** Projet de Fin d'Études (PFE) — ESPRIT
