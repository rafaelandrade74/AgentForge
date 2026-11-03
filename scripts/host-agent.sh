#!/bin/sh
# Inicia o host agent (modo 1: API no Docker, claude/git neste computador).
# Deixe rodando enquanto usar o AgentForge. Na primeira vez cria HOST_AGENT_TOKEN e HOST_WORKSPACES_ROOT no .env.
# Uso: ./scripts/host-agent.sh [pasta-dos-clones] [urls]
#   no Linux com Docker nativo, escute no gateway do Docker:  ./scripts/host-agent.sh "" http://0.0.0.0:8787
set -eu
repo="$(cd "$(dirname "$0")/.." && pwd)"
env_file="$repo/.env"
[ -e "$env_file" ] || cp "$repo/.env.example" "$env_file"

get() { sed -n "s/^$1=//p" "$env_file" | head -n 1; }
set_env() {
  if grep -q "^$1=" "$env_file"; then sed -i.bak "s|^$1=.*|$1=$2|" "$env_file" && rm -f "$env_file.bak"
  else printf '%s=%s\n' "$1" "$2" >> "$env_file"; fi
}

root="${1:-}"
urls="${2:-http://127.0.0.1:8787}"

token="$(get HOST_AGENT_TOKEN)"
if [ "${#token}" -lt 24 ]; then
  token="$(head -c 48 /dev/urandom | base64 | tr -d '/+=\n' | head -c 40)"
  set_env HOST_AGENT_TOKEN "$token"
  echo "HOST_AGENT_TOKEN criado no .env."
fi
[ -n "$root" ] || root="$(get HOST_WORKSPACES_ROOT)"
[ -n "$root" ] || root="$repo/.agentforge/repos"
set_env HOST_WORKSPACES_ROOT "$root"

for tool in dotnet git claude; do
  command -v "$tool" >/dev/null 2>&1 || echo "AVISO: '$tool' não está no PATH." >&2
done

export HostAgent__Token="$token"
export HostAgent__Roots__0="$root"
claude_path="$(command -v claude 2>/dev/null || true)"
[ -z "$claude_path" ] || export HostAgent__Executables__claude="$claude_path"
export DOTNET_ROLL_FORWARD=Major

echo "Host agent em $urls  |  pasta de trabalho: $root"
echo "Agora, em outro terminal:  docker compose -f docker-compose.yml -f docker-compose.host-agent.yml up -d --build"
exec dotnet run --project "$repo/backend/AgentForge.HostAgent" -c Release --no-launch-profile -- --urls="$urls"
