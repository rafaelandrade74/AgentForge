#!/bin/sh
# Cria o .env a partir do .env.example com segredos aleatórios. Não sobrescreve um .env existente.
# A senha do administrador é mostrada uma única vez, no final.
set -eu
cd "$(dirname "$0")/.."

if [ -e .env ]; then
  echo ".env já existe; nada foi alterado." >&2
  exit 1
fi

rand() { head -c 48 /dev/urandom | base64 | tr -d '/+=\n' | head -c "$1"; }
admin_password=$(rand 20)
jwt_secret=$(rand 48)
db_password=$(rand 24)

sed -e "s|^ADMIN_PASSWORD=.*|ADMIN_PASSWORD=${admin_password}|" \
    -e "s|^JWT_SECRET=.*|JWT_SECRET=${jwt_secret}|" \
    -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=${db_password}|" \
    .env.example > .env
chmod 600 .env

echo ".env criado com segredos aleatórios."
echo "Usuário: admin    Senha: ${admin_password}    (guarde; só o hash fica no banco)"
