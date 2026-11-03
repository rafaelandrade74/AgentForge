#!/bin/sh
# Cria o repositório de origem do projeto de exemplo (bare, com um commit em main) a partir de examples/tasks-api.
# Uso, dentro do container da API (examples/ é montado em /examples):
#   docker compose exec api sh /examples/make-sample-repo.sh
# Depois cadastre o projeto com a URL  /data/repos/_sample/tasks-api.git
set -eu

src="$(cd "$(dirname "$0")/tasks-api" && pwd)"
target="${1:-/data/repos/_sample/tasks-api.git}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ -e "$target" ]; then
  echo "$target já existe; nada foi feito." >&2
  exit 1
fi

cp -R "$src/." "$work/"
cd "$work"
git init -q -b main
git add .
git -c user.name="AgentForge" -c user.email="agentforge@localhost" commit -q -m "TaskApi: projeto de exemplo"
mkdir -p "$(dirname "$target")"
git clone -q --bare "$work" "$target"
echo "Repositório de exemplo criado em $target"
