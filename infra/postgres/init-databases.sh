#!/bin/bash
set -e

for db in ticketnow_catalog ticketnow_booking ticketnow_payment ticketnow_identity; do
  echo "Creating database $db"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<-EOSQL
    CREATE DATABASE $db;
EOSQL
done