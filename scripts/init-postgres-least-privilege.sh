#!/usr/bin/env bash
# ==============================================================================
# JANUS RFID — Initialisation & Application du Moindre Privilège PostgreSQL
# ==============================================================================
# Sépare strictement :
# 1. Le rôle de migration (propriétaire du schéma et des tables, droits DDL)
# 2. Le rôle runtime de l'API (droits DML minimaux, pas de DDL, pas de DROP/DELETE)
# ==============================================================================

set -euo pipefail

DB_NAME="${POSTGRES_DB:-janus_db}"
ADMIN_USER="${POSTGRES_USER:-janus_app}"
ADMIN_PASSWORD="${POSTGRES_PASSWORD:-local-dev-db-password-change-me}"
RUNTIME_USER="${POSTGRES_RUNTIME_USER:-janus_runtime}"
RUNTIME_PASSWORD="${POSTGRES_RUNTIME_PASSWORD:-local-dev-runtime-password-change-me}"

PSQL_ARGS=()
if [ -n "${PGHOST:-}" ]; then
    PSQL_ARGS+=("-h" "${PGHOST}")
    if [ -n "${PGPORT:-}" ]; then
        PSQL_ARGS+=("-p" "${PGPORT}")
    fi
elif [ -f "/.dockerenv" ] || [ -d "/var/lib/postgresql" ]; then
    # Dans le conteneur Docker : connexion via socket Unix local
    PSQL_ARGS=()
else
    # Hors conteneur sur la machine hôte : port mappé 5433 par défaut
    PSQL_ARGS+=("-h" "localhost" "-p" "${PGPORT:-5433}")
fi

echo "==> Application des privilèges PostgreSQL de sécurité (Base: ${DB_NAME}, Rôle Runtime: ${RUNTIME_USER})..."

PGPASSWORD="${ADMIN_PASSWORD}" psql "${PSQL_ARGS[@]}" -U "${ADMIN_USER}" -d "${DB_NAME}" -v ON_ERROR_STOP=1 <<-EOSQL
    -- 1. Création idempotente du rôle runtime (sans privilèges superuser ni DDL)
    DO \$\$
    BEGIN
        IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = '${RUNTIME_USER}') THEN
            CREATE ROLE ${RUNTIME_USER} WITH LOGIN NOINHERIT NOCREATEDB NOCREATEROLE NOSUPERUSER;
        END IF;
    END \$\$;

    -- Configuration / mise à jour du mot de passe runtime depuis l'environnement
    ALTER ROLE ${RUNTIME_USER} WITH PASSWORD '${RUNTIME_PASSWORD}';

    -- 2. Sécurisation globale du schéma et de la base de données
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
    REVOKE ALL ON DATABASE "${DB_NAME}" FROM PUBLIC;

    GRANT CONNECT ON DATABASE "${DB_NAME}" TO ${RUNTIME_USER};
    GRANT USAGE ON SCHEMA public TO ${RUNTIME_USER};

    -- 3. Droits d'accès stricts au niveau des tables (DML uniquement)
    -- Cards : lecture, création et mise à jour de solde/statut par l'API
    GRANT SELECT, INSERT, UPDATE ON TABLE "Cards" TO ${RUNTIME_USER};

    -- Validations : registre immuable d'événements de passage (append-only)
    GRANT SELECT, INSERT ON TABLE "Validations" TO ${RUNTIME_USER};

    -- FareTransactions : grand livre financier (append-only)
    GRANT SELECT, INSERT ON TABLE "FareTransactions" TO ${RUNTIME_USER};

    -- CardAuditLogs : piste d'audit administrative (append-only, AUCUN UPDATE ni DELETE)
    GRANT SELECT, INSERT ON TABLE "CardAuditLogs" TO ${RUNTIME_USER};

    -- 4. Droits sur les séquences d'identifiants
    GRANT USAGE, SELECT ON SEQUENCE "Cards_Id_seq" TO ${RUNTIME_USER};
    GRANT USAGE, SELECT ON SEQUENCE "Validations_Id_seq" TO ${RUNTIME_USER};
    GRANT USAGE, SELECT ON SEQUENCE "FareTransactions_Id_seq" TO ${RUNTIME_USER};
    GRANT USAGE, SELECT ON SEQUENCE "CardAuditLogs_Id_seq" TO ${RUNTIME_USER};

    -- 5. Protection contre les dérives futures de privilèges par défaut
    ALTER DEFAULT PRIVILEGES FOR ROLE ${ADMIN_USER} IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC;
    ALTER DEFAULT PRIVILEGES FOR ROLE ${ADMIN_USER} IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC;
EOSQL

echo "==> Privilèges appliqués avec succès pour le rôle '${RUNTIME_USER}'."
