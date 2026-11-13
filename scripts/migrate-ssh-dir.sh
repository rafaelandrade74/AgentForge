#!/bin/sh
# Migra as chaves SSH do AgentForge do path antigo (~/.ssh ou /home/app/.ssh)
# para o novo padrão (~/.agentforge/ssh).
# Seguro para rodar mais de uma vez: não sobrescreve se o destino já existe.

set -e

OLD_DIR="${1:-$HOME/.ssh}"
NEW_DIR="${2:-$HOME/.agentforge/ssh}"

if [ ! -f "$OLD_DIR/id_ed25519" ]; then
    echo "Nenhuma chave AgentForge encontrada em $OLD_DIR — nada a migrar."
    exit 0
fi

if [ -f "$NEW_DIR/id_ed25519" ]; then
    echo "Chave já existe em $NEW_DIR — migração não necessária."
    exit 0
fi

mkdir -p "$NEW_DIR"
chmod 700 "$NEW_DIR"

for f in id_ed25519 id_ed25519.pub known_hosts; do
    if [ -f "$OLD_DIR/$f" ]; then
        cp -p "$OLD_DIR/$f" "$NEW_DIR/$f"
        echo "Copiado: $OLD_DIR/$f → $NEW_DIR/$f"
    fi
done

echo ""
echo "Migração concluída. Verifique que tudo funciona e depois remova os arquivos antigos:"
echo "  rm -f $OLD_DIR/id_ed25519 $OLD_DIR/id_ed25519.pub $OLD_DIR/known_hosts"
