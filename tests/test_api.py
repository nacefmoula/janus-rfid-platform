#!/usr/bin/env python3
import concurrent.futures
import json
import os
import unittest
import urllib.request
import urllib.error
import uuid
from datetime import datetime, timezone

BASE = os.environ.get("JANUS_BASE_URL", os.environ.get("RFID_BASE_URL", "http://localhost:5080"))
TERMINAL_TOKEN = os.environ.get("TERMINAL_DEMO_TOKEN", "dev-terminal-token-change-me")
ADMIN_TOKEN = os.environ.get("ADMIN_DEMO_TOKEN", "dev-admin-token-change-me")

def call(path, method="GET", payload=None, token=TERMINAL_TOKEN):
    h = {}
    if token is not None:
        h["Authorization"] = f"Bearer {token}"
    data = None
    if payload is not None:
        data = json.dumps(payload).encode()
        h["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE + path, data=data, headers=h, method=method)
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            body = json.loads(raw)
        except ValueError:
            body = raw
        return e.code, body

def get_card(uid, token=TERMINAL_TOKEN, query=""):
    q = f"?{query}" if query else ""
    return call(f"/api/cards/{uid}{q}", token=token)

def get_audit(uid, token=ADMIN_TOKEN, limit=None, offset=None, query=""):
    params = []
    if limit is not None:
        params.append(f"limit={limit}")
    if offset is not None:
        params.append(f"offset={offset}")
    if query:
        params.append(query)
    q = f"?{'&'.join(params)}" if params else ""
    return call(f"/api/cards/{uid}/audit{q}", token=token)

def prepare_card(uid, balance_millimes, status="Active", label=None, reason=None, token=ADMIN_TOKEN):
    payload = {
        "balanceMillimes": balance_millimes,
        "label": label or f"Card {uid}"
    }
    if status is not None:
        payload["status"] = status
    if reason is not None:
        payload["reason"] = reason
    return call(f"/api/cards/{uid}", method="PUT", payload=payload, token=token)

def event(uid="04A1B2C3D4E5F6", event_id=None):
    return {
        "eventId": event_id or str(uuid.uuid4()),
        "cardUid": uid,
        "occurredAt": datetime.now(timezone.utc).isoformat(),
        "eventType": "TRANSPORT_VALIDATION"
    }

class ApiTests(unittest.TestCase):
    def setUp(self):
        # Garantit l'état nominal des cartes communes de démo avant chaque test via le rôle admin
        prepare_card("04A1B2C3D4E5F6", balance_millimes=10000, status="Active", label="Carte démo active")
        prepare_card("04DEAD00000001", balance_millimes=5000, status="Revoked", label="Carte démo révoquée")

    # --- 8 Tests de référence initiaux conservés ---
    def test_health(self):
        s, b = call("/health")
        self.assertEqual(s, 200)
        self.assertEqual(b["status"], "healthy")
        self.assertEqual(b["service"], "janus-api")

    def test_active_card(self):
        s, b = call("/api/validations", "POST", event())
        self.assertEqual(s, 200)
        self.assertEqual(b["decision"], "ACCEPTED")
        self.assertFalse(b["duplicate"])

    def test_exact_replay(self):
        p = event()
        s1, _ = call("/api/validations", "POST", p)
        s2, b2 = call("/api/validations", "POST", p)
        self.assertEqual((s1, s2), (200, 200))
        self.assertTrue(b2["duplicate"])

    def test_changed_payload_conflicts(self):
        p = event()
        s1, _ = call("/api/validations", "POST", p)
        p2 = dict(p)
        p2["cardUid"] = "04DEAD00000001"
        s2, b2 = call("/api/validations", "POST", p2)
        self.assertEqual(s1, 200)
        self.assertEqual(s2, 409)
        self.assertEqual(b2["error"], "idempotency_conflict")

    def test_revoked_card(self):
        s, b = call("/api/validations", "POST", event("04DEAD00000001"))
        self.assertEqual(s, 200)
        self.assertEqual(b["decision"], "REJECTED")
        self.assertEqual(b["reason"], "CARD_REVOKED")

    def test_unknown_card(self):
        s, b = call("/api/validations", "POST", event("04FFFFFFFFFFFF"))
        self.assertEqual(s, 200)
        self.assertEqual(b["reason"], "UNKNOWN_CARD")

    def test_bad_token(self):
        s, _ = call("/api/validations", "POST", event(), token="wrong")
        self.assertEqual(s, 401)

    def test_client_cannot_choose_terminal_identity(self):
        p = event()
        p["terminalId"] = "ATTACKER"
        s, b = call("/api/validations", "POST", p)
        self.assertEqual(s, 200)
        self.assertEqual(b["terminalId"], "TERMINAL-DEMO-01")

    # --- Tests pour le débit billettique atomique ---

    def test_1_debit_accepted_with_sufficient_balance(self):
        """1. Débit accepté avec solde suffisant : vérifie le solde et la transaction créée."""
        uid = f"04DEBIT{uuid.uuid4().hex[:8].upper()}"
        s_prep, _ = prepare_card(uid, balance_millimes=10000, status="Active")
        self.assertEqual(s_prep, 200)

        s0, c0 = get_card(uid)
        self.assertEqual(s0, 200)
        init_balance = c0["balanceMillimes"]
        init_tx_count = c0["transactionsCount"]
        self.assertEqual(init_balance, 10000)

        p = event(uid)
        s, b = call("/api/validations", "POST", p)
        self.assertEqual(s, 200)
        self.assertEqual(b["decision"], "ACCEPTED")
        self.assertEqual(b["amountDebited"], 1500)
        self.assertEqual(b["balanceMillimes"], init_balance - 1500)

        s1, c1 = get_card(uid)
        self.assertEqual(s1, 200)
        self.assertEqual(c1["balanceMillimes"], init_balance - 1500)
        self.assertEqual(c1["transactionsCount"], init_tx_count + 1)
        last_tx = c1["transactions"][0]
        self.assertEqual(last_tx["amountMillimes"], 1500)
        self.assertEqual(last_tx["balanceAfterMillimes"], init_balance - 1500)
        self.assertEqual(last_tx["eventId"], p["eventId"])

    def test_2_insufficient_funds_rejected_balance_unchanged(self):
        """2. Solde insuffisant : rejet INSUFFICIENT_FUNDS, solde inchangé, aucune transaction."""
        uid = f"04LOW{uuid.uuid4().hex[:9].upper()}"
        s_prep, _ = prepare_card(uid, balance_millimes=500, status="Active")
        self.assertEqual(s_prep, 200)

        s0, c0 = get_card(uid)
        self.assertEqual(s0, 200)
        init_balance = c0["balanceMillimes"]
        init_tx_count = c0["transactionsCount"]
        self.assertLess(init_balance, 1500)

        s, b = call("/api/validations", "POST", event(uid))
        self.assertEqual(s, 200)
        self.assertEqual(b["decision"], "REJECTED")
        self.assertEqual(b["reason"], "INSUFFICIENT_FUNDS")
        self.assertEqual(b.get("amountDebited", 0), 0)

        s1, c1 = get_card(uid)
        self.assertEqual(s1, 200)
        self.assertEqual(c1["balanceMillimes"], init_balance)
        self.assertEqual(c1["transactionsCount"], init_tx_count)

    def test_3_revoked_card_no_debit(self):
        """3. Carte révoquée : aucun débit, solde inchangé, aucune transaction financière."""
        uid = f"04REV{uuid.uuid4().hex[:9].upper()}"
        s_prep, _ = prepare_card(uid, balance_millimes=5000, status="Revoked")
        self.assertEqual(s_prep, 200)

        s0, c0 = get_card(uid)
        self.assertEqual(s0, 200)
        init_balance = c0["balanceMillimes"]
        init_tx_count = c0["transactionsCount"]

        s, b = call("/api/validations", "POST", event(uid))
        self.assertEqual(s, 200)
        self.assertEqual(b["decision"], "REJECTED")
        self.assertEqual(b["reason"], "CARD_REVOKED")

        s1, c1 = get_card(uid)
        self.assertEqual(s1, 200)
        self.assertEqual(c1["balanceMillimes"], init_balance)
        self.assertEqual(c1["transactionsCount"], init_tx_count)

    def test_4_unknown_card_no_debit(self):
        """4. Carte inconnue : aucun débit, carte introuvable."""
        s, b = call("/api/validations", "POST", event("04FFFFFFFFFFFF"))
        self.assertEqual(s, 200)
        self.assertEqual(b["decision"], "REJECTED")
        self.assertEqual(b["reason"], "UNKNOWN_CARD")

        s_card, _ = get_card("04FFFFFFFFFFFF")
        self.assertEqual(s_card, 404)

    def test_5_exact_replay_single_debit(self):
        """5. Rejeu exact : un seul débit financier même avec plusieurs transmissions."""
        uid = f"04REP{uuid.uuid4().hex[:9].upper()}"
        s_prep, _ = prepare_card(uid, balance_millimes=10000, status="Active")
        self.assertEqual(s_prep, 200)

        s0, c0 = get_card(uid)
        init_balance = c0["balanceMillimes"]
        init_tx_count = c0["transactionsCount"]

        p = event(uid)
        s1, b1 = call("/api/validations", "POST", p)
        self.assertEqual(s1, 200)
        self.assertFalse(b1["duplicate"])

        s2, b2 = call("/api/validations", "POST", p)
        self.assertEqual(s2, 200)
        self.assertTrue(b2["duplicate"])

        s1, c1 = get_card(uid)
        self.assertEqual(c1["balanceMillimes"], init_balance - 1500)
        self.assertEqual(c1["transactionsCount"], init_tx_count + 1)

    def test_6_changed_payload_conflict_single_debit(self):
        """6. Même eventId avec payload différent : conflit 409 et un seul débit pour la première carte."""
        eid = str(uuid.uuid4())
        card_a = f"04CNFA{uuid.uuid4().hex[:8].upper()}"
        card_b = f"04CNFB{uuid.uuid4().hex[:8].upper()}"
        prepare_card(card_a, balance_millimes=10000, status="Active")
        prepare_card(card_b, balance_millimes=5000, status="Revoked")

        s0_a, c0_a = get_card(card_a)
        s0_r, c0_r = get_card(card_b)

        p1 = event(card_a, event_id=eid)
        s1, b1 = call("/api/validations", "POST", p1)
        self.assertEqual(s1, 200)

        p2 = dict(p1)
        p2["cardUid"] = card_b
        s2, b2 = call("/api/validations", "POST", p2)
        self.assertEqual(s2, 409)
        self.assertEqual(b2["error"], "idempotency_conflict")

        _, c1_a = get_card(card_a)
        _, c1_r = get_card(card_b)
        self.assertEqual(c1_a["balanceMillimes"], c0_a["balanceMillimes"] - 1500)
        self.assertEqual(c1_r["balanceMillimes"], c0_r["balanceMillimes"])
        self.assertEqual(c1_r["transactionsCount"], c0_r["transactionsCount"])

    def test_7_concurrent_exact_replay_single_debit(self):
        """7. Deux requêtes concurrentes utilisant le même événement : exactement un seul débit."""
        uid = f"04CREP{uuid.uuid4().hex[:8].upper()}"
        prepare_card(uid, balance_millimes=10000, status="Active")

        s0, c0 = get_card(uid)
        init_balance = c0["balanceMillimes"]
        init_tx_count = c0["transactionsCount"]

        p = event(uid)
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            f1 = executor.submit(call, "/api/validations", "POST", p)
            f2 = executor.submit(call, "/api/validations", "POST", p)
            r1 = f1.result()
            r2 = f2.result()

        self.assertEqual(r1[0], 200)
        self.assertEqual(r2[0], 200)
        duplicates = [r1[1]["duplicate"], r2[1]["duplicate"]]
        self.assertIn(True, duplicates)
        self.assertIn(False, duplicates)

        _, c1 = get_card(uid)
        self.assertEqual(c1["balanceMillimes"], init_balance - 1500)
        self.assertEqual(c1["transactionsCount"], init_tx_count + 1)

    def test_8_concurrent_distinct_events_exceeding_balance(self):
        """8. Deux événements distincts concurrents dont le cumul dépasse le solde :
           Solde initial 2000 millimes, tarif 1500. Au plus un débit accepté, solde final 500, JAMAIS négatif."""
        uid = f"04RACE{uuid.uuid4().hex[:8].upper()}"
        s_prep, _ = prepare_card(uid, balance_millimes=2000, status="Active", label="Carte test concurrence")
        self.assertEqual(s_prep, 200)

        s0, c0 = get_card(uid)
        self.assertEqual(s0, 200)
        self.assertEqual(c0["balanceMillimes"], 2000)
        self.assertEqual(c0["transactionsCount"], 0)

        p1 = event(uid)
        p2 = event(uid)

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            f1 = executor.submit(call, "/api/validations", "POST", p1)
            f2 = executor.submit(call, "/api/validations", "POST", p2)
            r1 = f1.result()
            r2 = f2.result()

        self.assertEqual(r1[0], 200)
        self.assertEqual(r2[0], 200)
        decisions = [r1[1]["decision"], r2[1]["decision"]]
        self.assertIn("ACCEPTED", decisions)
        self.assertIn("REJECTED", decisions)

        _, c1 = get_card(uid)
        self.assertEqual(c1["balanceMillimes"], 500)
        self.assertGreaterEqual(c1["balanceMillimes"], 0)
        self.assertEqual(c1["transactionsCount"], 1)

    def test_9_failed_operation_no_partial_financial_write(self):
        """9. Échec de validation d'entrée : aucune écriture financière partielle."""
        uid = f"04FAIL{uuid.uuid4().hex[:8].upper()}"
        prepare_card(uid, balance_millimes=10000, status="Active")
        s0, c0 = get_card(uid)

        # EventId vide
        p_bad = {"eventId": "00000000-0000-0000-0000-000000000000", "cardUid": uid, "eventType": "TRANSPORT_VALIDATION"}
        s, b = call("/api/validations", "POST", p_bad)
        self.assertEqual(s, 400)

        # Type d'événement non supporté
        p_unsupported = {"eventId": str(uuid.uuid4()), "cardUid": uid, "eventType": "PARKING_ACCESS"}
        s2, b2 = call("/api/validations", "POST", p_unsupported)
        self.assertEqual(s2, 400)

        s1, c1 = get_card(uid)
        self.assertEqual(c1["balanceMillimes"], c0["balanceMillimes"])
        self.assertEqual(c1["transactionsCount"], c0["transactionsCount"])

    def test_10_missing_or_invalid_card_endpoint_protection(self):
        """10. Séparation stricte des rôles : le terminal ne peut pas administrer les cartes."""
        # Consultation sans token ou avec mauvais token -> 401
        s, _ = call("/api/cards/04A1B2C3D4E5F6", token="bad")
        self.assertEqual(s, 401)

        s_no_tok, _ = call("/api/cards/04A1B2C3D4E5F6", token=None)
        self.assertEqual(s_no_tok, 401)

        # PUT avec token terminal -> 403 Forbidden (Terminal credential rejeté)
        s_put_terminal, b_put_terminal = call("/api/cards/04A1B2C3D4E5F6", method="PUT", payload={"balanceMillimes": 10000}, token=TERMINAL_TOKEN)
        self.assertEqual(s_put_terminal, 403)
        self.assertEqual(b_put_terminal.get("error"), "forbidden")

        # PUT sans token -> 401 Unauthorized
        s_put_bad, _ = call("/api/cards/04A1B2C3D4E5F6", method="PUT", payload={"balanceMillimes": 10000}, token="bad")
        self.assertEqual(s_put_bad, 401)

        # GET /api/cards/{uid}/audit avec token terminal -> 403 Forbidden
        s_audit_terminal, b_audit_terminal = call("/api/cards/04A1B2C3D4E5F6/audit", token=TERMINAL_TOKEN)
        self.assertEqual(s_audit_terminal, 403)
        self.assertEqual(b_audit_terminal.get("error"), "forbidden")

        # GET /api/cards/{uid}/audit sans token ou mauvais token -> 401 Unauthorized
        s_audit_bad, _ = call("/api/cards/04A1B2C3D4E5F6/audit", token="bad")
        self.assertEqual(s_audit_bad, 401)

        # POST /api/validations avec token Admin -> 401 Unauthorized (l'admin ne se fait pas passer pour une borne)
        s_post_admin, _ = call("/api/validations", method="POST", payload=event(), token=ADMIN_TOKEN)
        self.assertEqual(s_post_admin, 401)

    def test_11_concurrent_different_payloads_conflict_single_debit(self):
        """11. Deux requêtes concurrentes avec le MÊME eventId mais des payloads DIFFÉRENTS :
           Exactement une gagne (200), l'autre reçoit un conflit (409), exactement un seul débit."""
        eid = str(uuid.uuid4())
        card_a = f"04RACA{uuid.uuid4().hex[:8].upper()}"
        card_b = f"04RACB{uuid.uuid4().hex[:8].upper()}"
        prepare_card(card_a, balance_millimes=10000, status="Active")
        prepare_card(card_b, balance_millimes=10000, status="Active")

        s0_a, c0_a = get_card(card_a)
        s0_b, c0_b = get_card(card_b)
        self.assertEqual((s0_a, s0_b), (200, 200))
        self.assertEqual((c0_a["balanceMillimes"], c0_b["balanceMillimes"]), (10000, 10000))
        self.assertEqual((c0_a["transactionsCount"], c0_b["transactionsCount"]), (0, 0))

        p1 = event(card_a, event_id=eid)
        p2 = event(card_b, event_id=eid)

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            f1 = executor.submit(call, "/api/validations", "POST", p1)
            f2 = executor.submit(call, "/api/validations", "POST", p2)
            r1 = f1.result()
            r2 = f2.result()

        status_codes = [r1[0], r2[0]]
        self.assertIn(200, status_codes)
        self.assertIn(409, status_codes)

        # Identifier la carte gagnante et la carte perdante
        if r1[0] == 200:
            win_card, lose_card = card_a, card_b
            err_body = r2[1]
        else:
            win_card, lose_card = card_b, card_a
            err_body = r1[1]

        self.assertEqual(err_body.get("error"), "idempotency_conflict")

        # Vérifier les soldes et transactions finales
        _, c1_win = get_card(win_card)
        _, c1_lose = get_card(lose_card)

        # La carte gagnante est débitée de 1500 millimes et a 1 transaction financière
        self.assertEqual(c1_win["balanceMillimes"], 8500)
        self.assertEqual(c1_win["transactionsCount"], 1)
        self.assertEqual(c1_win["transactions"][0]["eventId"], eid)
        self.assertEqual(c1_win["transactions"][0]["amountMillimes"], 1500)

        # La carte perdante n'est JAMAIS débitée et n'a aucune transaction
        self.assertEqual(c1_lose["balanceMillimes"], 10000)
        self.assertEqual(c1_lose["transactionsCount"], 0)

    def test_12_transactions_pagination(self):
        """12. Pagination de l'historique des transactions sur GET /api/cards/{uid}."""
        uid = f"04PAGE{uuid.uuid4().hex[:8].upper()}"
        prepare_card(uid, balance_millimes=10000, status="Active")

        # Effectuer deux débits
        call("/api/validations", "POST", event(uid))
        call("/api/validations", "POST", event(uid))

        # Requête avec limit=1, offset=0
        s, b = get_card(uid, query="limit=1&offset=0")
        self.assertEqual(s, 200)
        self.assertEqual(b["transactionsCount"], 2)
        self.assertEqual(b["limit"], 1)
        self.assertEqual(b["offset"], 0)
        self.assertEqual(len(b["transactions"]), 1)

        # Requête avec limit=1, offset=1
        s2, b2 = get_card(uid, query="limit=1&offset=1")
        self.assertEqual(s2, 200)
        self.assertEqual(len(b2["transactions"]), 1)
        self.assertNotEqual(b["transactions"][0]["id"], b2["transactions"][0]["id"])

    # --- Nouveaux tests pour l'intégrité des statuts et l'audit administratif ---

    def test_13_audit_revocation_preserved_on_balance_update(self):
        """Test A — Conservation d'une révocation :
           Modifier uniquement le solde d'une carte révoquée ne la réactive pas, et l'audit indique Revoked -> Revoked."""
        uid = f"04REVA{uuid.uuid4().hex[:8].upper()}"
        s0, _ = prepare_card(uid, balance_millimes=5000, status="Revoked", label="Carte révoquée initiale")
        self.assertEqual(s0, 200)

        # Mise à jour du solde SANS fournir status
        s_up, b_up = prepare_card(uid, balance_millimes=8000, status=None, label="Carte révoquée solde maj")
        self.assertEqual(s_up, 200)
        self.assertEqual(b_up["status"], "Revoked")
        self.assertEqual(b_up["balanceMillimes"], 8000)

        # Vérification de l'état de la carte (GET /api/cards/{uid} sans auditLogs)
        _, card = get_card(uid)
        self.assertEqual(card["status"], "Revoked")
        self.assertEqual(card["balanceMillimes"], 8000)
        self.assertNotIn("auditLogs", card)

        # Vérification du journal d'audit via GET /api/cards/{uid}/audit (réservé admin)
        s_aud, aud_body = get_audit(uid)
        self.assertEqual(s_aud, 200)
        self.assertGreaterEqual(aud_body["totalCount"], 2)
        latest_audit = aud_body["auditLogs"][0]
        self.assertEqual(latest_audit["action"], "ADMIN_CARD_UPDATED")
        self.assertEqual(latest_audit["previousStatus"], "Revoked")
        self.assertEqual(latest_audit["newStatus"], "Revoked")
        self.assertEqual(latest_audit["previousBalanceMillimes"], 5000)
        self.assertEqual(latest_audit["newBalanceMillimes"], 8000)

    def test_14_audit_active_status_preserved_on_balance_update(self):
        """Test B — Conservation d'un statut actif :
           Modifier uniquement le solde d'une carte active conserve son statut Active et trace Active -> Active."""
        uid = f"04ACTB{uuid.uuid4().hex[:8].upper()}"
        prepare_card(uid, balance_millimes=6000, status="Active")

        # Mise à jour du solde sans spécifier de statut
        s_up, b_up = prepare_card(uid, balance_millimes=9000, status=None)
        self.assertEqual(s_up, 200)
        self.assertEqual(b_up["status"], "Active")

        _, card = get_card(uid)
        self.assertEqual(card["status"], "Active")
        self.assertNotIn("auditLogs", card)

        s_aud, aud_body = get_audit(uid)
        self.assertEqual(s_aud, 200)
        latest_audit = aud_body["auditLogs"][0]
        self.assertEqual(latest_audit["previousStatus"], "Active")
        self.assertEqual(latest_audit["newStatus"], "Active")
        self.assertEqual(latest_audit["previousBalanceMillimes"], 6000)
        self.assertEqual(latest_audit["newBalanceMillimes"], 9000)

    def test_15_audit_creation_defaults_to_active(self):
        """Test C — Création sans statut explicite :
           Créer une carte sans fournir Status l'initialise à Active et trace NewStatus=Active sans PreviousStatus."""
        uid = f"04NEWC{uuid.uuid4().hex[:8].upper()}"
        s, b = prepare_card(uid, balance_millimes=3500, status=None)
        self.assertEqual(s, 200)
        self.assertEqual(b["status"], "Active")
        self.assertEqual(b["balanceMillimes"], 3500)

        _, card = get_card(uid)
        self.assertEqual(card["status"], "Active")
        self.assertNotIn("auditLogs", card)

        s_aud, aud_body = get_audit(uid)
        self.assertEqual(s_aud, 200)
        latest_audit = aud_body["auditLogs"][0]
        self.assertEqual(latest_audit["action"], "ADMIN_CARD_CREATED")
        self.assertIsNone(latest_audit["previousStatus"])
        self.assertIsNone(latest_audit["previousBalanceMillimes"])
        self.assertEqual(latest_audit["newStatus"], "Active")
        self.assertEqual(latest_audit["newBalanceMillimes"], 3500)

    def test_16_audit_explicit_status_transitions(self):
        """Test D — Changement explicite de statut :
           Vérifie le passage explicite de Active à Revoked, puis de Revoked à Active."""
        uid = f"04CHGD{uuid.uuid4().hex[:8].upper()}"
        prepare_card(uid, balance_millimes=4000, status="Active")

        # 1. Révocation explicite
        s_rev, b_rev = prepare_card(uid, balance_millimes=4000, status="Revoked")
        self.assertEqual(s_rev, 200)
        self.assertEqual(b_rev["status"], "Revoked")

        _, card_rev = get_card(uid)
        self.assertEqual(card_rev["status"], "Revoked")
        self.assertNotIn("auditLogs", card_rev)

        s_aud_rev, aud_rev_body = get_audit(uid)
        self.assertEqual(s_aud_rev, 200)
        audit_rev = aud_rev_body["auditLogs"][0]
        self.assertEqual(audit_rev["previousStatus"], "Active")
        self.assertEqual(audit_rev["newStatus"], "Revoked")

        # 2. Réactivation explicite (insensible à la casse 'active')
        s_act, b_act = prepare_card(uid, balance_millimes=4000, status="active")
        self.assertEqual(s_act, 200)
        self.assertEqual(b_act["status"], "Active")

        _, card_act = get_card(uid)
        self.assertEqual(card_act["status"], "Active")
        self.assertNotIn("auditLogs", card_act)

        s_aud_act, aud_act_body = get_audit(uid)
        self.assertEqual(s_aud_act, 200)
        audit_act = aud_act_body["auditLogs"][0]
        self.assertEqual(audit_act["previousStatus"], "Revoked")
        self.assertEqual(audit_act["newStatus"], "Active")

    def test_17_invalid_status_rejected_with_400(self):
        """Test E — Statut invalide :
           Envoyer un statut non reconnu renvoie HTTP 400 et ne modifie ni la carte ni son journal d'audit."""
        uid = f"04INVE{uuid.uuid4().hex[:8].upper()}"
        prepare_card(uid, balance_millimes=5000, status="Active")
        _, card_before = get_card(uid)
        self.assertNotIn("auditLogs", card_before)

        s_aud_b, aud_before = get_audit(uid)
        self.assertEqual(s_aud_b, 200)
        audits_count_before = aud_before["totalCount"]

        # Requête avec statut inconnu
        payload = {"balanceMillimes": 7000, "status": "SUSPENDED_UNKNOWN"}
        s, b = call(f"/api/cards/{uid}", method="PUT", payload=payload, token=ADMIN_TOKEN)
        self.assertEqual(s, 400)
        self.assertEqual(b.get("error"), "invalid_status")

        # Vérification qu'aucune modification n'a eu lieu
        _, card_after = get_card(uid)
        self.assertEqual(card_after["status"], "Active")
        self.assertEqual(card_after["balanceMillimes"], 5000)
        self.assertNotIn("auditLogs", card_after)

        s_aud_a, aud_after = get_audit(uid)
        self.assertEqual(s_aud_a, 200)
        self.assertEqual(aud_after["totalCount"], audits_count_before)

    def test_18_audit_reason_independent_of_label(self):
        """Test F — Motif d'audit indépendant du libellé :
           Envoyer Reason explicite est tracé dans CardAuditLogs sans affecter le Label commercial de la carte."""
        uid = f"04RSNF{uuid.uuid4().hex[:8].upper()}"
        s, b = prepare_card(uid, balance_millimes=7500, status="Active",
                            label="Pass Navigo Démo #99",
                            reason="Remboursement suite réclamation voyageur #1042")
        self.assertEqual(s, 200)
        self.assertEqual(b["label"], "Pass Navigo Démo #99")

        _, card = get_card(uid)
        self.assertEqual(card["label"], "Pass Navigo Démo #99")
        self.assertNotIn("auditLogs", card)

        # Test endpoint dédié GET /api/cards/{uid}/audit avec token administrateur
        s_aud, audit_body = get_audit(uid)
        self.assertEqual(s_aud, 200)
        self.assertEqual(audit_body["cardUid"], uid)
        self.assertGreaterEqual(audit_body["totalCount"], 1)
        latest_audit = audit_body["auditLogs"][0]
        self.assertEqual(latest_audit["reason"], "Remboursement suite réclamation voyageur #1042")
        self.assertNotEqual(latest_audit["reason"], card["label"])

    def test_19_audit_endpoint_permissions_and_pagination(self):
        """Test G — Sécurité d'accès et pagination sur GET /api/cards/{uid}/audit :
           - Terminal credentials -> 403 Forbidden sur /audit.
           - Mauvais token ou absence de token -> 401 Unauthorized sur /audit.
           - Terminal credentials sur GET /api/cards/{uid} -> 200 sans auditLogs.
           - Admin credentials -> 200 OK sur /audit avec pagination (limit, offset, totalCount, auditLogs)."""
        uid = f"04SECG{uuid.uuid4().hex[:8].upper()}"
        # 1. Création puis 2 modifications pour accumuler 3 traces d'audit
        prepare_card(uid, balance_millimes=1000, status="Active", reason="Création carte")
        prepare_card(uid, balance_millimes=2000, reason="Recharge 1")
        prepare_card(uid, balance_millimes=3000, reason="Recharge 2")

        # 2. Terminal token sur GET /api/cards/{uid}/audit -> 403 Forbidden
        s_term, b_term = get_audit(uid, token=TERMINAL_TOKEN)
        self.assertEqual(s_term, 403)
        self.assertEqual(b_term.get("error"), "forbidden")
        self.assertIn("Terminal credentials cannot view", b_term.get("message", ""))

        # 3. Bad token / Missing token sur GET /api/cards/{uid}/audit -> 401 Unauthorized
        s_bad, _ = get_audit(uid, token="bad-token")
        self.assertEqual(s_bad, 401)
        s_none, _ = get_audit(uid, token=None)
        self.assertEqual(s_none, 401)

        # 4. Terminal token sur GET /api/cards/{uid} -> 200 OK sans auditLogs
        s_card_term, b_card_term = get_card(uid, token=TERMINAL_TOKEN)
        self.assertEqual(s_card_term, 200)
        self.assertEqual(b_card_term["uid"], uid)
        self.assertNotIn("auditLogs", b_card_term)

        # 5. Admin token sur GET /api/cards/{uid}/audit -> 200 OK avec pagination
        s_aud, b_aud = get_audit(uid, token=ADMIN_TOKEN)
        self.assertEqual(s_aud, 200)
        self.assertEqual(b_aud["cardUid"], uid)
        self.assertEqual(b_aud["totalCount"], 3)
        self.assertEqual(len(b_aud["auditLogs"]), 3)

        # Test pagination limit=1, offset=0
        s_p1, b_p1 = get_audit(uid, limit=1, offset=0)
        self.assertEqual(s_p1, 200)
        self.assertEqual(b_p1["limit"], 1)
        self.assertEqual(b_p1["offset"], 0)
        self.assertEqual(b_p1["totalCount"], 3)
        self.assertEqual(len(b_p1["auditLogs"]), 1)
        self.assertEqual(b_p1["auditLogs"][0]["reason"], "Recharge 2")
        first_id = b_p1["auditLogs"][0]["id"]

        # Test pagination limit=1, offset=1
        s_p2, b_p2 = get_audit(uid, limit=1, offset=1)
        self.assertEqual(s_p2, 200)
        self.assertEqual(b_p2["limit"], 1)
        self.assertEqual(b_p2["offset"], 1)
        self.assertEqual(len(b_p2["auditLogs"]), 1)
        self.assertEqual(b_p2["auditLogs"][0]["reason"], "Recharge 1")
        self.assertNotEqual(first_id, b_p2["auditLogs"][0]["id"])

if __name__ == "__main__":
    unittest.main(verbosity=2)
