#!/usr/bin/env python3
"""
Scénario de Démonstration de Bout en Bout — Janus RFID Platform
==============================================================
Démontre le flux opérationnel complet pour le PFE :
1. Provisionnement administratif de carte avec solde initial et audit log
2. Validation RFID nominale par le terminal avec débit atomique
3. Résistance au rejeu d'événement (idempotence stricte)
4. Deuxième débit et épuisement du solde
5. Refus pour solde insuffisant et refus pour carte révoquée (aucun débit financier)
6. Consultation administrative de l'historique financier et de l'audit (avec preuve d'isolation RBAC)
"""

import json
import os
import sys
import urllib.request
import urllib.error
import uuid
from datetime import datetime, timezone

BASE = os.environ.get("JANUS_BASE_URL", "http://localhost:5080")
TERMINAL_TOKEN = os.environ.get("TERMINAL_DEMO_TOKEN", "dev-terminal-token-change-me")
ADMIN_TOKEN = os.environ.get("ADMIN_DEMO_TOKEN", "dev-admin-token-change-me")

def http_call(path, method="GET", payload=None, token=None):
    headers = {}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    data = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(payload).encode()
    req = urllib.request.Request(f"{BASE}{path}", data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            return resp.status, json.loads(resp.read().decode())
    except urllib.error.HTTPError as err:
        body = err.read().decode()
        try:
            return err.code, json.loads(body)
        except Exception:
            return err.code, body

def step(title):
    print("\n" + "=" * 70)
    print(f"▶ {title}")
    print("=" * 70)

def main():
    print("╔════════════════════════════════════════════════════════════════════╗")
    print("║     PARCOURS DE DÉMONSTRATION DE BOUT EN BOUT — JANUS RFID        ║")
    print("║    Billettique atomique, Idempotence, Contrôle RBAC & Audit       ║")
    print("╚════════════════════════════════════════════════════════════════════╝")

    card_uid = f"04DEMO{uuid.uuid4().hex[:8].upper()}"
    revoked_uid = f"04REVOKED{uuid.uuid4().hex[:6].upper()}"
    event_id_1 = str(uuid.uuid4())
    event_id_2 = str(uuid.uuid4())
    event_id_3 = str(uuid.uuid4())

    # ---------------------------------------------------------
    step("ÉTAPE 1 : Provisionnement administratif de la carte (Rôle Administrateur)")
    # ---------------------------------------------------------
    print(f"L'administrateur crée la carte {card_uid} avec un solde initial de 3000 millimes (2 trajets).")
    payload_admin = {
        "balanceMillimes": 3000,
        "status": "Active",
        "label": "Pass Voyageur PFE Demo",
        "reason": "Dotation initiale voyageur pour validation"
    }
    status, body = http_call(f"/api/cards/{card_uid}", method="PUT", payload=payload_admin, token=ADMIN_TOKEN)
    print(f"HTTP {status} : {json.dumps(body, indent=2)}")
    assert status == 200, "Échec création carte"

    # Provisionnement d'une carte révoquée pour le test de refus
    http_call(f"/api/cards/{revoked_uid}", method="PUT", payload={
        "balanceMillimes": 5000,
        "status": "Revoked",
        "label": "Carte révoquée démo",
        "reason": "Mise en liste noire préventive"
    }, token=ADMIN_TOKEN)

    # ---------------------------------------------------------
    step("ÉTAPE 2 : Premier passage RFID — Cas nominal (Rôle Terminal)")
    # ---------------------------------------------------------
    print(f"Borne TERMINAL-DEMO-01 lit la carte {card_uid} (EventId: {event_id_1}).")
    val_payload_1 = {
        "eventId": event_id_1,
        "cardUid": card_uid,
        "occurredAt": datetime.now(timezone.utc).isoformat(),
        "eventType": "TRANSPORT_VALIDATION"
    }
    status, body = http_call("/api/validations", method="POST", payload=val_payload_1, token=TERMINAL_TOKEN)
    print(f"HTTP {status} : {json.dumps(body, indent=2)}")
    assert status == 200 and body["decision"] == "ACCEPTED" and body["balanceMillimes"] == 1500, "Échec premier débit"
    print(f"✔ Décision : {body['decision']} | Montant débité : {body['amountDebited']} millimes | Nouveau solde : {body['balanceMillimes']} millimes")

    # ---------------------------------------------------------
    step("ÉTAPE 3 : Rejeu exact de l'événement (Simulation coupure réseau / rejeu)")
    # ---------------------------------------------------------
    print(f"La borne renvoie le MÊME événement (EventId: {event_id_1}).")
    status, body = http_call("/api/validations", method="POST", payload=val_payload_1, token=TERMINAL_TOKEN)
    print(f"HTTP {status} : {json.dumps(body, indent=2)}")
    assert status == 200 and body["duplicate"] is True, "Échec détection doublon"
    print("✔ Détection d'idempotence réussie : duplicate=True, AUCUN débit financier supplémentaire n'a été appliqué.")

    # ---------------------------------------------------------
    step("ÉTAPE 4 : Deuxième passage RFID — Épuisement du solde")
    # ---------------------------------------------------------
    print(f"Second passage valide avec un nouvel événement (EventId: {event_id_2}).")
    val_payload_2 = {
        "eventId": event_id_2,
        "cardUid": card_uid,
        "occurredAt": datetime.now(timezone.utc).isoformat(),
        "eventType": "TRANSPORT_VALIDATION"
    }
    status, body = http_call("/api/validations", method="POST", payload=val_payload_2, token=TERMINAL_TOKEN)
    print(f"HTTP {status} : {json.dumps(body, indent=2)}")
    assert status == 200 and body["balanceMillimes"] == 0, "Échec deuxième débit"
    print(f"✔ Décision : {body['decision']} | Nouveau solde : {body['balanceMillimes']} millimes (solde épuisé).")

    # ---------------------------------------------------------
    step("ÉTAPE 5 : Cas d'erreur — Solde insuffisant & Carte révoquée")
    # ---------------------------------------------------------
    print(f"1) Tentative de passage avec solde nul (EventId: {event_id_3}) :")
    val_payload_3 = {
        "eventId": event_id_3,
        "cardUid": card_uid,
        "occurredAt": datetime.now(timezone.utc).isoformat(),
        "eventType": "TRANSPORT_VALIDATION"
    }
    status, body = http_call("/api/validations", method="POST", payload=val_payload_3, token=TERMINAL_TOKEN)
    print(f"HTTP {status} : {json.dumps(body, indent=2)}")
    assert status == 200 and body["decision"] == "REJECTED" and body["reason"] == "INSUFFICIENT_FUNDS", "Échec rejet solde"
    print("✔ Passage rejeté : INSUFFICIENT_FUNDS. Solde préservé à 0 millimes.")

    print(f"\n2) Tentative de passage avec carte révoquée {revoked_uid} :")
    val_payload_rev = {
        "eventId": str(uuid.uuid4()),
        "cardUid": revoked_uid,
        "occurredAt": datetime.now(timezone.utc).isoformat(),
        "eventType": "TRANSPORT_VALIDATION"
    }
    status, body = http_call("/api/validations", method="POST", payload=val_payload_rev, token=TERMINAL_TOKEN)
    print(f"HTTP {status} : {json.dumps(body, indent=2)}")
    assert status == 200 and body["decision"] == "REJECTED" and body["reason"] == "CARD_REVOKED", "Échec rejet révocation"
    print("✔ Passage rejeté : CARD_REVOKED. Aucun débit financier appliqué.")

    # ---------------------------------------------------------
    step("ÉTAPE 6 : Traçabilité et Audit — Contrôle RBAC & Cohérence Ledger")
    # ---------------------------------------------------------
    print(f"1) Consultation de la carte par la borne GET /api/cards/{card_uid} (Données billettiques uniquement) :")
    status, body = http_call(f"/api/cards/{card_uid}", token=TERMINAL_TOKEN)
    print(f"HTTP {status} : transactionsCount={body.get('transactionsCount')}, solde={body.get('balanceMillimes')}")
    print(f"Nombre de transactions enregistrées dans le registre financier : {len(body.get('transactions', []))}")
    assert "auditLogs" not in body, "Fuite de journaux d'audit vers le terminal !"
    assert body["transactionsCount"] == 2, "Incohérence du nombre de débits réels"
    print("✔ La borne ne voit AUCUN journal d'audit administratif (isolation des rôles respectée).")

    print(f"\n2) Tentative d'accès de la borne à l'endpoint d'audit GET /api/cards/{card_uid}/audit :")
    status_sec, body_sec = http_call(f"/api/cards/{card_uid}/audit", token=TERMINAL_TOKEN)
    print(f"HTTP {status_sec} : {json.dumps(body_sec)}")
    assert status_sec == 403, "Le terminal ne devrait pas avoir accès aux audits !"
    print("✔ Accès borne refusé avec HTTP 403 Forbidden.")

    print(f"\n3) Consultation autorisée de l'audit par l'administrateur GET /api/cards/{card_uid}/audit :")
    status_aud, body_aud = http_call(f"/api/cards/{card_uid}/audit", token=ADMIN_TOKEN)
    print(f"HTTP {status_aud} : totalCount={body_aud.get('totalCount')}, cardUid={body_aud.get('cardUid')}")
    print("Détails des traces d'audit administratives :")
    for log in body_aud.get("auditLogs", []):
        print(f" - [{log['createdAt']}] Action={log['action']} | Status={log['newStatus']} | Solde={log['newBalanceMillimes']} m | Motif='{log['reason']}'")

    print("\n" + "=" * 70)
    print("✅ TOUTES LES VÉRIFICATIONS DU SCÉNARIO DE DÉMONSTRATION SONT VALIDÉES")
    print("=" * 70)

if __name__ == "__main__":
    main()
