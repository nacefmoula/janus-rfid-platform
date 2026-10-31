#!/usr/bin/env bash
# ==============================================================================
# JANUS RFID — Initialisation & Application Idempotente du Moindre Privilège PostgreSQL
# ==============================================================================
# Sépare strictement :
# 1. Le rôle de migration (propriétaire du schéma et des tables, droits DDL)
# 2. Le rôle runtime de l'API (droits DML minimaux, pas de DDL, pas de DROP/DELETE)
#
# Compatible avec :
# - Une base PostgreSQL vierge (avant exécution des migrations EF Core)
# - Une base PostgreSQL déjà migrée (ré-exécution idempotente sans perte de données)
# - Une exécution interne au conteneur (/docker-entrypoint-initdb.d/) ou depuis l'hôte
# ==============================================================================

set -euo pipefail

ENVIRONMENT_MODE="${ASPNETCORE_ENVIRONMENT:-${JANUS_ENVIRONMENT:-Development}}"
ENVIRONMENT_LOWER="$(printf '%s' "${ENVIRONMENT_MODE}" | tr '[:upper:]' '[:lower:]')"

is_known_dev_password() {
    local val
    val="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
    case "${val}" in
        "local-dev-db-password-change-me"|"local-dev-runtime-password-change-me"|"change_me_runtime_only"|"postgres"|"password"|"admin"|"change-me"|"change_me"|*change-me*|*change_me*)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

if [ "${ENVIRONMENT_LOWER}" != "development" ]; then
    if [ -z "${POSTGRES_DB:-}" ] || [ -z "${POSTGRES_USER:-}" ] || [ -z "${POSTGRES_PASSWORD:-}" ] || [ -z "${POSTGRES_RUNTIME_USER:-}" ] || [ -z "${POSTGRES_RUNTIME_PASSWORD:-}" ]; then
        echo "ERROR: In non-development environment ('${ENVIRONMENT_MODE}'), POSTGRES_DB, POSTGRES_USER, POSTGRES_PASSWORD, POSTGRES_RUNTIME_USER, and POSTGRES_RUNTIME_PASSWORD must be explicitly set without fallback defaults." >&2
        exit 1
    fi

    if is_known_dev_password "${POSTGRES_PASSWORD}" || [ "${#POSTGRES_PASSWORD}" -lt 12 ]; then
        echo "ERROR: In non-development environment ('${ENVIRONMENT_MODE}'), POSTGRES_PASSWORD must be a strong non-default secret (minimum 12 characters)." >&2
        exit 1
    fi

    if is_known_dev_password "${POSTGRES_RUNTIME_PASSWORD}" || [ "${#POSTGRES_RUNTIME_PASSWORD}" -lt 12 ]; then
        echo "ERROR: In non-development environment ('${ENVIRONMENT_MODE}'), POSTGRES_RUNTIME_PASSWORD must be a strong non-default secret (minimum 12 characters)." >&2
        exit 1
    fi
fi

DB_NAME="${POSTGRES_DB:-janus_db}"
ADMIN_USER="${POSTGRES_USER:-janus_app}"
ADMIN_PASSWORD="${POSTGRES_PASSWORD:-local-dev-db-password-change-me}"
RUNTIME_USER="${POSTGRES_RUNTIME_USER:-janus_runtime}"
RUNTIME_PASSWORD="${POSTGRES_RUNTIME_PASSWORD:-local-dev-runtime-password-change-me}"

if [ -z "${DB_NAME}" ] || [ -z "${ADMIN_USER}" ] || [ -z "${ADMIN_PASSWORD}" ] || [ -z "${RUNTIME_USER}" ] || [ -z "${RUNTIME_PASSWORD}" ]; then
    echo "ERROR: Database name, users, and passwords cannot be empty." >&2
    exit 1
fi

ADMIN_USER_LOWER="$(printf '%s' "${ADMIN_USER}" | tr '[:upper:]' '[:lower:]')"
RUNTIME_USER_LOWER="$(printf '%s' "${RUNTIME_USER}" | tr '[:upper:]' '[:lower:]')"

if [ "${ADMIN_USER_LOWER}" = "${RUNTIME_USER_LOWER}" ]; then
    echo "ERROR: POSTGRES_RUNTIME_USER ('${RUNTIME_USER}') must be distinct from POSTGRES_USER ('${ADMIN_USER}')." >&2
    exit 1
fi

if [ "${RUNTIME_USER_LOWER}" = "postgres" ]; then
    echo "ERROR: POSTGRES_RUNTIME_USER cannot be the 'postgres' cluster superuser." >&2
    exit 1
fi

run_psql() {
    local common_vars=(
        "-q"
        "-v" "ON_ERROR_STOP=1"
        "-v" "db_name=${DB_NAME}"
        "-v" "admin_user=${ADMIN_USER}"
        "-v" "runtime_user=${RUNTIME_USER}"
        "-v" "runtime_password=${RUNTIME_PASSWORD}"
    )

    if [ -n "${PGHOST:-}" ]; then
        local host_args=("-h" "${PGHOST}")
        if [ -n "${PGPORT:-}" ]; then
            host_args+=("-p" "${PGPORT}")
        fi
        PGPASSWORD="${ADMIN_PASSWORD}" psql "${host_args[@]}" -U "${ADMIN_USER}" -d "${DB_NAME}" "${common_vars[@]}"
    elif [ -f "/.dockerenv" ] || [ -f "/run/.containerenv" ]; then
        # Exécution à l'intérieur d'un conteneur Docker/OCI : connexion via socket Unix local
        PGPASSWORD="${ADMIN_PASSWORD}" psql -U "${ADMIN_USER}" -d "${DB_NAME}" "${common_vars[@]}"
    elif [ -n "${POSTGRES_CONTAINER:-}" ]; then
        # Exécution depuis l'hôte en ciblant explicitement un conteneur PostgreSQL nommé
        docker exec -i -e PGPASSWORD="${ADMIN_PASSWORD}" "${POSTGRES_CONTAINER}" \
            psql -U "${ADMIN_USER}" -d "${DB_NAME}" "${common_vars[@]}"
    elif command -v psql >/dev/null 2>&1; then
        # Exécution depuis l'hôte avec client psql natif (port mappé 5433 par défaut)
        PGPASSWORD="${ADMIN_PASSWORD}" psql -h "localhost" -p "${PGPORT:-5433}" -U "${ADMIN_USER}" -d "${DB_NAME}" "${common_vars[@]}"
    elif command -v docker >/dev/null 2>&1; then
        # Repli depuis l'hôte sans client psql local : exécution via le service db de Docker Compose
        docker compose exec -T -e PGPASSWORD="${ADMIN_PASSWORD}" db \
            psql -U "${ADMIN_USER}" -d "${DB_NAME}" "${common_vars[@]}"
    else
        echo "ERROR: Neither 'psql' nor 'docker' is available to apply PostgreSQL privileges." >&2
        exit 1
    fi
}

echo "==> Application des privilèges PostgreSQL de sécurité (Base: ${DB_NAME}, Rôle Migration: ${ADMIN_USER}, Rôle Runtime: ${RUNTIME_USER})..."

run_psql <<-'EOSQL'
    BEGIN;

    -- REV-2B1-02 / Lot 2C : Désactivation locale à la transaction de la journalisation SQL (contexte SUSET).
    -- Prend effet lorsque le rôle d'administration est superutilisateur (cas standard de /docker-entrypoint-initdb.d/)
    -- et s'ignore proprement sous un rôle non-superutilisateur. Ne remplace pas la sécurisation des logs serveur.
    DO $$
    BEGIN
        EXECUTE 'SET LOCAL log_statement = ''none''';
        EXECUTE 'SET LOCAL log_min_duration_statement = -1';
    EXCEPTION
        WHEN insufficient_privilege THEN
            NULL;
    END $$;

    SELECT 1 AS config_loaded
    FROM (
        SELECT set_config('janus.db_name', :'db_name', true),
               set_config('janus.admin_user', :'admin_user', true),
               set_config('janus.runtime_user', :'runtime_user', true),
               set_config('janus.runtime_password', :'runtime_password', true)
    ) AS _cfg;

    DO $$
    DECLARE
        v_db_name text := current_setting('janus.db_name');
        v_admin_user text := current_setting('janus.admin_user');
        v_runtime_user text := current_setting('janus.runtime_user');
        v_runtime_password text := current_setting('janus.runtime_password');
        v_seq text;
        v_sequences text[] := ARRAY[
            'Cards_Id_seq',
            'Validations_Id_seq',
            'FareTransactions_Id_seq',
            'CardAuditLogs_Id_seq'
        ];
    BEGIN
        PERFORM pg_advisory_xact_lock(20261019, 2);

        -- 1. Création ou mise à jour idempotente du rôle runtime (strictement sans privilèges superuser ni DDL)
        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = v_runtime_user) THEN
            EXECUTE format(
                'CREATE ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOCREATEDB NOCREATEROLE NOSUPERUSER NOBYPASSRLS NOREPLICATION',
                v_runtime_user,
                v_runtime_password
            );
        ELSE
            EXECUTE format(
                'ALTER ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOCREATEDB NOCREATEROLE NOSUPERUSER NOBYPASSRLS NOREPLICATION',
                v_runtime_user,
                v_runtime_password
            );
        END IF;

        -- Effacement immédiat du secret en mémoire de session PostgreSQL
        PERFORM set_config('janus.runtime_password', '', true);

        -- 2. Durcissement de la base de données et du schéma public
        EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', v_db_name);
        EXECUTE 'REVOKE ALL ON SCHEMA public FROM PUBLIC';
        EXECUTE format('REVOKE CREATE ON SCHEMA public FROM %I', v_runtime_user);

        EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', v_db_name, v_runtime_user);
        EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', v_runtime_user);

        -- 3. Privilèges par défaut explicites pour les objets créés par le rôle de migration
        EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC', v_admin_user);
        EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC', v_admin_user);
        EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM PUBLIC', v_admin_user);
        EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO %I', v_admin_user, v_runtime_user);

        -- 4. Isolation de la table d'historique des migrations si elle existe déjà
        IF to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
            EXECUTE 'REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM PUBLIC';
            EXECUTE format('REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM %I', v_runtime_user);
        END IF;

        -- 5. Application conditionnelle des droits DML stricts sur les tables existantes
        IF to_regclass('public."Cards"') IS NOT NULL THEN
            EXECUTE 'REVOKE ALL ON TABLE public."Cards" FROM PUBLIC';
            EXECUTE format('REVOKE ALL ON TABLE public."Cards" FROM %I', v_runtime_user);
            EXECUTE format('GRANT SELECT, INSERT, UPDATE ON TABLE public."Cards" TO %I', v_runtime_user);
        END IF;

        IF to_regclass('public."Validations"') IS NOT NULL THEN
            EXECUTE 'REVOKE ALL ON TABLE public."Validations" FROM PUBLIC';
            EXECUTE format('REVOKE ALL ON TABLE public."Validations" FROM %I', v_runtime_user);
            EXECUTE format('GRANT SELECT, INSERT ON TABLE public."Validations" TO %I', v_runtime_user);
        END IF;

        IF to_regclass('public."FareTransactions"') IS NOT NULL THEN
            EXECUTE 'REVOKE ALL ON TABLE public."FareTransactions" FROM PUBLIC';
            EXECUTE format('REVOKE ALL ON TABLE public."FareTransactions" FROM %I', v_runtime_user);
            EXECUTE format('GRANT SELECT, INSERT ON TABLE public."FareTransactions" TO %I', v_runtime_user);
        END IF;

        IF to_regclass('public."CardAuditLogs"') IS NOT NULL THEN
            EXECUTE 'REVOKE ALL ON TABLE public."CardAuditLogs" FROM PUBLIC';
            EXECUTE format('REVOKE ALL ON TABLE public."CardAuditLogs" FROM %I', v_runtime_user);
            EXECUTE format('GRANT SELECT, INSERT ON TABLE public."CardAuditLogs" TO %I', v_runtime_user);
        END IF;

        -- 6. Application conditionnelle des droits sur les séquences existantes
        FOREACH v_seq IN ARRAY v_sequences LOOP
            IF to_regclass(format('public.%I', v_seq)) IS NOT NULL THEN
                EXECUTE format('REVOKE ALL ON SEQUENCE public.%I FROM PUBLIC', v_seq);
                EXECUTE format('REVOKE ALL ON SEQUENCE public.%I FROM %I', v_seq, v_runtime_user);
                EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE public.%I TO %I', v_seq, v_runtime_user);
            END IF;
        END LOOP;
    END $$;

    COMMIT;
EOSQL

echo "==> Privilèges appliqués avec succès pour le rôle '${RUNTIME_USER}'."
