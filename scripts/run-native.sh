#!/bin/sh
# Modo 3 — tudo no computador: a API roda aqui (e serve a interface), com o claude, git e dotnet deste computador.
# Precisa de um PostgreSQL. Sem um instalado, use --start-postgres (sobe só o banco num container).
# Uso: ./scripts/run-native.sh [--start-postgres] [--rebuild]
set -eu
repo="$(cd "$(dirname "$0")/.." && pwd)"
env_file="$repo/.env"
[ -e "$env_file" ] || { cp "$repo/.env.example" "$env_file"; echo "AVISO: .env criado a partir do exemplo: troque as senhas (ou rode scripts/init-env.sh antes)." >&2; }
get() { v="$(sed -n "s/^$1=//p" "$env_file" | head -n 1)"; [ -n "$v" ] && echo "$v" || echo "${2:-}"; }

start_pg=0; rebuild=0
for a in "$@"; do
  case "$a" in --start-postgres) start_pg=1 ;; --rebuild) rebuild=1 ;; *) echo "opção desconhecida: $a" >&2; exit 2 ;; esac
done

for tool in dotnet node git; do command -v "$tool" >/dev/null 2>&1 || { echo "'$tool' não está no PATH." >&2; exit 1; }; done
command -v claude >/dev/null 2>&1 || echo "AVISO: 'claude' não está no PATH: a tela Agentes vai mostrar 'CLI não instalado'." >&2

db_password="$(get POSTGRES_PASSWORD change-me)"; db_user="$(get POSTGRES_USER agentforge)"
if [ "$start_pg" = 1 ]; then
  command -v docker >/dev/null 2>&1 || { echo "docker não encontrado (use um PostgreSQL instalado)." >&2; exit 1; }
  if docker ps -a --format '{{.Names}}' | grep -qx agentforge-native-db; then docker start agentforge-native-db >/dev/null
  else docker run -d --name agentforge-native-db -e POSTGRES_DB=agentforge -e "POSTGRES_USER=$db_user" -e "POSTGRES_PASSWORD=$db_password" \
        -p 127.0.0.1:5432:5432 -v agentforge-native-pgdata:/var/lib/postgresql/data postgres:16-alpine >/dev/null; fi
  echo "PostgreSQL em localhost:5432 (container agentforge-native-db)."; sleep 4
fi

dist="$repo/frontend/dist/frontend/browser"
if [ "$rebuild" = 1 ] || [ ! -e "$dist/index.html" ]; then
  echo "Compilando a interface (npm ci + ng build)..."
  (cd "$repo/frontend" && npm ci && npx ng build)
fi

workspaces="${AGENTFORGE_WORKSPACES:-$repo/.agentforge/repos}"
mkdir -p "$workspaces"

export ConnectionStrings__Default="$(get NATIVE_DB_CONNECTION "Host=localhost;Port=5432;Database=agentforge;Username=$db_user;Password=$db_password")"
export Auth__AdminUsername="$(get ADMIN_USERNAME admin)"
export Auth__AdminPassword="$(get ADMIN_PASSWORD)"
export Auth__JwtSecret="$(get JWT_SECRET)"
export Orchestrator__MaxConcurrency="$(get ORCHESTRATOR_CONCURRENCY 1)"
export Workspaces__Root="$workspaces"
export Frontend__Path="$dist"
export Prompts__Directory="$repo/prompts"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:8080}"
export DOTNET_ROLL_FORWARD=Major
export Agents__Launcher=local

echo "AgentForge em $ASPNETCORE_URLS  |  clones em $workspaces"
exec dotnet run --project "$repo/backend/AgentForge.Api" -c Release --no-launch-profile
