# Janus Web Demonstrator (Angular 19)

Ce projet est le démonstrateur web officiel de la plateforme **Janus RFID**, dédié à la présentation et au test fonctionnel du système billettique temps réel.

## 1. Versions & Compatibilité

* **Angular** : `19.2.x` (LTS, CLI v19.2.27) — Compatible Node.js 18/20/22.
* **Node.js** : `v22.23.1` (hôte) / `22-alpine` (Docker builder).
* **TypeScript** : `~5.7.2`.
* **Serveur Web / Proxy** : `nginx:1.27-alpine`.

## 2. Direction UI/UX & Design Tokens

L'interface adopte une direction artistique **B2B Light Theme** inspirée des logiciels de gestion billettique professionnelle (type Ventra / Cubic) :
* Fond applicatif : `#F4F7FB`
* Surfaces et cartes : `#FFFFFF` avec bordures subtiles `#E2E8F0`
* Couleur principale : Bleu professionnel `#2457A7` (action hover `#1A4485`)
* Typographie et textes : Bleu nuit `#172B4D` (ratio de contraste supérieur à 10:1, conforme WCAG AAA)
* États sémantiques accessibles (icône SVG + libellé textuel systématique) :
  * Vert succès (`#00875A` / `#E3FCEF`)
  * Orange/Ambre attention (`#B76E00` / `#FFFAE6`)
  * Rouge erreur (`#DE350B` / `#FFEBE6`)
  * Bleu info (`#0052CC` / `#DEEBFF`)

## 3. Sécurité & Authentification Volatile

* **Zéro persistance locale** : Aucun token de démonstration n'est conservé dans `localStorage`, `sessionStorage` ou les cookies.
* Gestion d'état volatile en mémoire vive via **Angular Signals** (`SessionService`).
* Sélecteur de rôle en en-tête permettant d'émuler un lecteur RFID (*Terminal*), un administrateur (*Admin*) ou un utilisateur public (*Public*) pour tester les contrôles RBAC du backend ASP.NET Core 10 (HTTP 401 / 403).

## 4. Modules Applicatifs

1. **Tableau de bord (Dashboard)** :
   * Métriques OpenTelemetry (`/metrics`) distinguées visuellement des données transactionnelles persistées.
   * Compteurs d'observabilité, état de santé (`/health`), flux des scans de la session et raccourci Grafana (`:3000`).
2. **Gestion des cartes (Cards)** :
   * Inventaire des cartes de test (`GET /api/demo/cards`), recherche et filtres de statut.
   * Tiroir de détail avec historique des transactions `FareTransactions` (`GET /api/cards/{uid}`).
   * Modal de recharge et de changement de statut (`PUT /api/cards/{uid}`) en millimes avec motif d'audit obligatoire.
3. **Simulateur de pupitre RFID (Simulator)** :
   * Kiosque de validation virtuel avec écran LCD, diodes LED d'état et surface de scan.
   * Scénarios de test : passage nominal, détection anti-passback/doublon (`duplicate=true`), rejet de concurrence (HTTP 409) et rejet d'authentification (HTTP 401).
4. **Piste d'audit & RBAC (Audit)** :
   * Consultation sécurisée de `GET /api/cards/{uid}/audit` avec démonstration du refus 403 (Terminal) et de l'autorisation 200 (Admin).

## 5. Commandes Utiles

```bash
# Lancement local avec proxy vers l'API Janus (http://localhost:5080)
npm start

# Compilation de production
npm run build

# Exécution des 35 tests unitaires en mode headless
npm test -- --watch=false

# Build de l'image Docker
docker build -t janus-web:test .
```
