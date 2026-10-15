# Janus RFID Platform — Validation sécurisée de titres de transport

Prototype local sans matériel : ASP.NET Core 10 + PostgreSQL + simulateur Python + tests HTTP.

## Prérequis
Ubuntu, Docker Engine + Compose plugin, Python 3.10+.

## Démarrage
```bash
cp .env.example .env
docker compose up --build -d
docker compose ps
curl -i http://localhost:5080/health
```
Token local : `dev-terminal-token-change-me`. Réservé au développement.

## Simuler
```bash
python3 simulator/janus_simulator.py
python3 simulator/janus_simulator.py --mode replay
python3 simulator/janus_simulator.py --mode conflict
python3 simulator/janus_simulator.py --mode revoked
python3 simulator/janus_simulator.py --mode unknown
python3 simulator/janus_simulator.py --mode bad-token
```

## Tests automatiques
```bash
python3 tests/test_api.py
```

## API
`POST /api/validations`
```json
{"eventId":"UUID","cardUid":"04A1B2C3D4E5F6","occurredAt":"2026-10-14T12:00:00Z","eventType":"TRANSPORT_VALIDATION"}
```
L'identité du terminal est déduite du Bearer token côté serveur, pas du JSON. L'index unique `(terminal_id,event_id)` empêche les doublons. Même ID avec un contenu différent => `409 Conflict`.

UID actif : `04A1B2C3D4E5F6`. UID révoqué : `04DEAD00000001`.

## Arrêter / réinitialiser
```bash
docker compose down
# Efface les données de test :
docker compose down -v
docker compose up --build -d
```

## Limites importantes
Ce prototype valide le flux logiciel, pas la radio NFC ni l'authentification cryptographique DESFire EV3. Un UID n'est pas une preuve cryptographique d'identité de carte. Pas de validation hors ligne automatique. `EnsureCreated` sert au prototype ; passer à des migrations versionnées avant d'étendre le schéma. Le token par défaut est uniquement local et doit être remplacé en environnement réel.
