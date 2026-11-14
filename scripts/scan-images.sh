#!/usr/bin/env bash
set -euo pipefail

# Escaneia todas as imagens Docker do projeto com Trivy.
# Instale: https://aquasecurity.github.io/trivy
# Uso: ./scripts/scan-images.sh

RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'

if ! command -v trivy &>/dev/null; then
    echo "Trivy não encontrado. Instale: https://aquasecurity.github.io/trivy"
    echo "  Alpine: apk add trivy"
    echo "  Debian: apt install trivy"
    echo "  macOS:  brew install trivy"
    exit 1
fi

IMAGES=(
    "mcr.microsoft.com/dotnet/sdk:8.0-alpine"
    "node:20-alpine"
    "postgres:16-alpine"
)

FAILED=0
for img in "${IMAGES[@]}"; do
    echo ""
    echo "=== Escaneando $img ==="
    if trivy image --severity HIGH,CRITICAL --exit-code 1 "$img"; then
        echo -e "${GREEN}✓ $img — sem vulnerabilidades HIGH/CRITICAL${NC}"
    else
        echo -e "${RED}✗ $img — vulnerabilidades encontradas!${NC}"
        FAILED=1
    fi
done

echo ""
if [ $FAILED -eq 1 ]; then
    echo -e "${RED}FALHA: uma ou mais imagens têm vulnerabilidades HIGH/CRITICAL${NC}"
    exit 1
else
    echo -e "${GREEN}OK: todas as imagens estão limpas${NC}"
fi
