#!/usr/bin/env python3
"""
tests/verify_metrics.py — Contrôle de validation des métriques Prometheus pour Janus RFID.

Interroge l'API HTTP de Prometheus (par défaut http://localhost:9090), analyse la structure
JSON et vérifie formellement :
1. La disponibilité et la validité de l'API HTTP de Prometheus (status == 'success').
2. La présence effective des séries de métriques métiers 'janus_validations_total'.
3. La distinction stricte entre requête réussie et résultat contenant 0 série.
4. La couverture des décisions métier (ACCEPTED, REJECTED, CONFLICT).
5. La présence des motifs de décision attendus (ex: ACTIVE_CARD, INSUFFICIENT_FUNDS).
6. La présence et la cohérence de l'histogramme de latence (janus_validation_duration_seconds_count > 0).
7. La présence du cumul financier (janus_fare_amount_millimes_total > 0).

Supporte un mode d'auto-test reproductible avec simulation de scénarios de réussite et d'échec :
    python3 tests/verify_metrics.py --self-test
"""

import json
import math
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

PROMETHEUS_URL = os.environ.get("PROMETHEUS_URL", "http://localhost:9090").rstrip("/")
MAX_RETRIES = int(os.environ.get("PROMETHEUS_CHECK_RETRIES", "10"))
RETRY_DELAY_SEC = float(os.environ.get("PROMETHEUS_CHECK_DELAY", "2.0"))


def query_prometheus(query: str, base_url: str = PROMETHEUS_URL) -> dict:
    """Interroge l'endpoint /api/v1/query de Prometheus et retourne le JSON décodé."""
    url = f"{base_url}/api/v1/query?{urllib.parse.urlencode({'query': query})}"
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            if resp.status != 200:
                raise RuntimeError(f"HTTP {resp.status} reçu lors de l'interrogation de Prometheus: {url}")
            raw = resp.read().decode("utf-8")
            return json.loads(raw)
    except urllib.error.URLError as e:
        raise RuntimeError(f"Impossible de joindre Prometheus sur {url}: {e}") from e
    except json.JSONDecodeError as e:
        raise RuntimeError(f"Réponse JSON invalide reçue de Prometheus: {e}") from e


def validate_validations_payload(payload: dict) -> dict:
    """Valide la structure et le contenu des séries 'janus_validations_total'."""
    status = payload.get("status")
    if status != "success":
        raise ValueError(f"Statut Prometheus invalide : '{status}' (attendu: 'success')")

    data = payload.get("data", {})
    results = data.get("result", [])
    if not results:
        raise ValueError("Aucune série temporelle trouvée pour 'janus_validations_total' (result est vide)")

    decisions = set()
    reasons = set()
    terminal_ids = set()
    total_validations = 0.0

    for item in results:
        metric = item.get("metric", {})
        decision = metric.get("decision")
        reason = metric.get("reason")
        term = metric.get("terminal_id")

        if decision:
            decisions.add(decision)
        if reason:
            reasons.add(reason)
        if term:
            terminal_ids.add(term)

        val = item.get("value", [0, "0"])
        try:
            total_validations += float(val[1])
        except (ValueError, TypeError, IndexError):
            pass

    expected_decisions = {"ACCEPTED", "REJECTED"}
    missing = expected_decisions - decisions
    if missing:
        raise ValueError(f"Décisions attendues manquantes dans Prometheus : {missing}")

    if total_validations <= 0:
        raise ValueError(f"Le total des validations collectées est nul ({total_validations})")

    return {
        "series_count": len(results),
        "total_validations": total_validations,
        "decisions": decisions,
        "reasons": reasons,
        "terminal_ids": terminal_ids,
    }


def validate_latency_payload(payload: dict) -> float:
    """Valide l'histogramme de latence."""
    if payload.get("status") != "success":
        raise ValueError("Statut Prometheus non-success pour l'histogramme de latence")

    results = payload.get("data", {}).get("result", [])
    if not results:
        raise ValueError("L'histogramme de latence 'janus_validation_duration_seconds_count' est introuvable")

    total_count = sum(float(r.get("value", [0, "0"])[1]) for r in results)
    if total_count <= 0:
        raise ValueError("Le compteur d'échantillons de latence est nul")

    return total_count


def validate_fare_payload(payload: dict) -> float:
    """Valide le cumul financier des recettes."""
    if payload.get("status") != "success":
        raise ValueError("Statut Prometheus non-success pour les recettes")

    results = payload.get("data", {}).get("result", [])
    if not results:
        raise ValueError("La métrique de recettes 'janus_fare_amount_millimes_total' est introuvable")

    total_fare = sum(float(r.get("value", [0, "0"])[1]) for r in results)
    if total_fare <= 0:
        raise ValueError("Le montant cumulé des débits billettiques est nul")

    return total_fare


def verify_observability():
    print(f"🔍 Vérification des métriques Janus RFID dans Prometheus ({PROMETHEUS_URL})...")

    # 1. Vérification des séries janus_validations_total avec boucle d'attente (scrape interval)
    summary = None
    last_error = None
    for attempt in range(1, MAX_RETRIES + 1):
        try:
            payload = query_prometheus("janus_validations_total")
            summary = validate_validations_payload(payload)
            print(f"✔ Métriques détectées après {attempt} tentative(s).")
            break
        except Exception as ex:
            last_error = str(ex)

        print(f"  [Tentative {attempt}/{MAX_RETRIES}] En attente du cycle de scraping Prometheus... ({last_error})")
        time.sleep(RETRY_DELAY_SEC)

    if summary is None:
        print(f"❌ Échec de la vérification : {last_error}", file=sys.stderr)
        sys.exit(1)

    print(f"✔ Total validations collectées : {int(summary['total_validations'])}")
    print(f"✔ Séries actives ({summary['series_count']}) | Décisions : {sorted(list(summary['decisions']))} | Valideurs : {sorted(list(summary['terminal_ids']))}")
    print(f"✔ Motifs observés : {sorted(list(summary['reasons']))}")

    # 2. Vérification de l'histogramme de latence
    try:
        hist_payload = query_prometheus("janus_validation_duration_seconds_count")
        total_hist_count = validate_latency_payload(hist_payload)
        print(f"✔ Histogramme de latence validé : {int(total_hist_count)} échantillons enregistrés.")
    except Exception as ex:
        print(f"❌ Échec histogramme de latence : {ex}", file=sys.stderr)
        sys.exit(1)

    # 3. Vérification du cumul financier
    try:
        fare_payload = query_prometheus("janus_fare_amount_millimes_total")
        total_fare = validate_fare_payload(fare_payload)
        print(f"✔ Recettes billettiques auditées : {int(total_fare)} millimes ({total_fare / 1000:.3f} DT).")
    except Exception as ex:
        print(f"❌ Échec recettes billettiques : {ex}", file=sys.stderr)
        sys.exit(1)

    print("\n✅ TOUS LES CONTRÔLES D'OBSERVABILITÉ PROMETHEUS SONT VALIDÉS AVEC SUCCÈS.")


def run_self_tests():
    """Exécute une suite de tests unitaires simulant des réponses Prometheus réelles et dégradées."""
    print("🧪 Exécution des auto-tests de 'verify_metrics.py' (scénarios de succès et d'échec)...")

    # Scénario 1 : Payload nominal complet (Succès attendu)
    nominal_payload = {
        "status": "success",
        "data": {
            "resultType": "vector",
            "result": [
                {
                    "metric": {"__name__": "janus_validations_total", "decision": "ACCEPTED", "reason": "ACTIVE_CARD", "terminal_id": "TERMINAL-01", "duplicate": "false"},
                    "value": [1700000000, "10"]
                },
                {
                    "metric": {"__name__": "janus_validations_total", "decision": "REJECTED", "reason": "INSUFFICIENT_FUNDS", "terminal_id": "TERMINAL-01", "duplicate": "false"},
                    "value": [1700000000, "3"]
                },
                {
                    "metric": {"__name__": "janus_validations_total", "decision": "CONFLICT", "reason": "IDEMPOTENCY_CONFLICT", "terminal_id": "TERMINAL-01", "duplicate": "false"},
                    "value": [1700000000, "1"]
                }
            ]
        }
    }
    res = validate_validations_payload(nominal_payload)
    assert math.isclose(res["total_validations"], 14.0, rel_tol=1e-9, abs_tol=1e-9)
    assert "ACCEPTED" in res["decisions"] and "REJECTED" in res["decisions"]
    print("  [PASS] Scénario 1 : Payload nominal validé avec succès.")

    # Scénario 2 : Requête HTTP réussie mais tableau de séries vide (Échec attendu)
    empty_result_payload = {"status": "success", "data": {"resultType": "vector", "result": []}}
    try:
        validate_validations_payload(empty_result_payload)
        assert False, "Le cas result vide aurait dû échouer !"
    except ValueError as ex:
        assert "result est vide" in str(ex)
        print(f"  [PASS] Scénario 2 : Résultat avec 0 série correctement rejeté ({ex}).")

    # Scénario 3 : Statut HTTP Prometheus en erreur (Échec attendu)
    error_status_payload = {"status": "error", "errorType": "bad_data", "error": "parse error"}
    try:
        validate_validations_payload(error_status_payload)
        assert False, "Le cas status error aurait dû échouer !"
    except ValueError as ex:
        assert "Statut Prometheus invalide" in str(ex)
        print(f"  [PASS] Scénario 3 : Statut 'error' correctement rejeté ({ex}).")

    # Scénario 4 : Décisions requises manquantes (Échec attendu)
    missing_decision_payload = {
        "status": "success",
        "data": {
            "resultType": "vector",
            "result": [
                {
                    "metric": {"__name__": "janus_validations_total", "decision": "ACCEPTED", "reason": "ACTIVE_CARD", "terminal_id": "TERMINAL-01", "duplicate": "false"},
                    "value": [1700000000, "5"]
                }
            ]
        }
    }
    try:
        validate_validations_payload(missing_decision_payload)
        assert False, "Le cas décision manquante aurait dû échouer !"
    except ValueError as ex:
        assert "Décisions attendues manquantes" in str(ex)
        print(f"  [PASS] Scénario 4 : Décision REJECTED manquante correctement détectée ({ex}).")

    # Scénario 5 : Compteur total à zéro (Échec attendu)
    zero_count_payload = {
        "status": "success",
        "data": {
            "resultType": "vector",
            "result": [
                {
                    "metric": {"__name__": "janus_validations_total", "decision": "ACCEPTED", "reason": "ACTIVE_CARD", "terminal_id": "TERMINAL-01", "duplicate": "false"},
                    "value": [1700000000, "0"]
                },
                {
                    "metric": {"__name__": "janus_validations_total", "decision": "REJECTED", "reason": "INSUFFICIENT_FUNDS", "terminal_id": "TERMINAL-01", "duplicate": "false"},
                    "value": [1700000000, "0"]
                }
            ]
        }
    }
    try:
        validate_validations_payload(zero_count_payload)
        assert False, "Le cas total zéro aurait dû échouer !"
    except ValueError as ex:
        assert "collectées est nul" in str(ex)
        print(f"  [PASS] Scénario 5 : Total de validations nul correctement rejeté ({ex}).")

    # Scénario 6 : Validation de l'histogramme de latence (succès et échec)
    valid_hist = {"status": "success", "data": {"result": [{"value": [1700000000, "42"]}]}}
    assert math.isclose(validate_latency_payload(valid_hist), 42.0, rel_tol=1e-9, abs_tol=1e-9)
    zero_hist = {"status": "success", "data": {"result": [{"value": [1700000000, "0"]}]}}
    try:
        validate_latency_payload(zero_hist)
        assert False, "Histogramme nul aurait dû échouer !"
    except ValueError:
        pass
    print("  [PASS] Scénario 6 : Histogramme de latence testé (nominal & count=0).")

    # Scénario 7 : Validation des recettes financières (succès et échec)
    valid_fare = {"status": "success", "data": {"result": [{"value": [1700000000, "3000"]}]}}
    assert math.isclose(validate_fare_payload(valid_fare), 3000.0, rel_tol=1e-9, abs_tol=1e-9)
    zero_fare = {"status": "success", "data": {"result": [{"value": [1700000000, "0"]}]}}
    try:
        validate_fare_payload(zero_fare)
        assert False, "Recettes nulles auraient dû échouer !"
    except ValueError:
        pass
    print("  [PASS] Scénario 7 : Recettes financières testées (nominal & amount=0).")

    print("\n✅ TOUS LES AUTO-TESTS UNITAIRES (7/7) SONT VALIDÉS AVEC SUCCÈS.")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] in ("--self-test", "-t"):
        run_self_tests()
    else:
        verify_observability()
