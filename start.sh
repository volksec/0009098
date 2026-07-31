#!/usr/bin/env bash
# =============================================================================
# Portal do Corretor — sobe o ambiente completo com um comando.
#
#   ./start.sh              instala, migra, popula e inicia backend + frontend
#   ./start.sh --reset      recria o banco do zero antes de iniciar
#   ./start.sh --stop       encerra os serviços
#   ./start.sh --no-seed    pula a carga de dados
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

API_PORT=8080
WEB_PORT=5173
DB_CONTAINER=pdc-secure-db
DB_NAME=portal_do_corretor
DB_MIGRATOR=pdc_migrator
LOG_DIR="$ROOT/.run"

RESET=0; SEED=1; STOP=0
for arg in "$@"; do
  case "$arg" in
    --reset)   RESET=1 ;;
    --no-seed) SEED=0 ;;
    --stop)    STOP=1 ;;
    -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Opção desconhecida: $arg" >&2; exit 2 ;;
  esac
done

BOLD=$'\033[1m'; DIM=$'\033[2m'; GREEN=$'\033[32m'; RED=$'\033[31m'
YELLOW=$'\033[33m'; BLUE=$'\033[34m'; RESET_C=$'\033[0m'

step()  { printf "\n${BOLD}${BLUE}▸ %s${RESET_C}\n" "$1"; }
ok()    { printf "  ${GREEN}✓${RESET_C} %s\n" "$1"; }
warn()  { printf "  ${YELLOW}!${RESET_C} %s\n" "$1"; }
die()   { printf "\n  ${RED}✗ %s${RESET_C}\n\n" "$1" >&2; exit 1; }

# Docker Desktop no Windows instala fora do PATH com frequência
if ! command -v docker >/dev/null 2>&1; then
  for candidate in \
    "$LOCALAPPDATA/Programs/DockerDesktop/resources/bin" \
    "/c/Users/$USER/AppData/Local/Programs/DockerDesktop/resources/bin" \
    "/c/Program Files/Docker/Docker/resources/bin"; do
    [ -x "$candidate/docker.exe" ] && export PATH="$PATH:$candidate" && break
  done
fi

# ---------------------------------------------------------------- parar
if [ "$STOP" = "1" ]; then
  step "Encerrando serviços"
  for name in api web; do
    if [ -f "$LOG_DIR/$name.pid" ]; then
      pid="$(cat "$LOG_DIR/$name.pid")"
      kill "$pid" 2>/dev/null && ok "$name (pid $pid) encerrado" || warn "$name já estava parado"
      rm -f "$LOG_DIR/$name.pid"
    fi
  done
  docker compose stop >/dev/null 2>&1 && ok "contêineres parados" || true
  echo; exit 0
fi

# ---------------------------------------------------------------- 1. pré-requisitos
step "Verificando pré-requisitos"

command -v dotnet >/dev/null 2>&1 || die ".NET SDK 9 não encontrado. https://dotnet.microsoft.com/download/dotnet/9.0"
command -v node   >/dev/null 2>&1 || die "Node.js 20+ não encontrado. https://nodejs.org/"
command -v docker >/dev/null 2>&1 || die "Docker não encontrado. Instale o Docker Desktop e verifique o PATH."

docker info >/dev/null 2>&1 || die "Docker não está em execução. Abra o Docker Desktop e aguarde ficar pronto."

ok ".NET $(dotnet --version)"
ok "Node $(node --version)"
ok "Docker $(docker --version | sed 's/Docker version //;s/,.*//')"

# ---------------------------------------------------------------- 2. ambiente
step "Configurando ambiente"

if [ ! -f .env ]; then
  cp .env.example .env
  # Gera senhas locais aleatórias em vez de usar os marcadores do exemplo
  for var in POSTGRES_APP_USER_PASSWORD POSTGRES_APP_REGULATOR_PASSWORD POSTGRES_APP_WORKER_PASSWORD; do
    secret="$(head -c 18 /dev/urandom | base64 | tr -d '/+=' | head -c 24)"
    sed -i.bak "s|^${var}=.*|${var}=${secret}|" .env && rm -f .env.bak
  done
  ok ".env criado com senhas geradas localmente"
else
  ok ".env já existe (preservado)"
fi

mkdir -p infrastructure/secrets "$LOG_DIR"
if [ ! -f infrastructure/secrets/db_password.txt ]; then
  head -c 18 /dev/urandom | base64 | tr -d '/+=' | head -c 24 > infrastructure/secrets/db_password.txt
  ok "senha do superusuário do banco gerada"
fi

set -a; . ./.env; set +a

# ---------------------------------------------------------------- 3. dependências
step "Instalando dependências"

dotnet restore --nologo -v q >/dev/null && ok "pacotes .NET restaurados"

if [ ! -d apps/frontend/node_modules ]; then
  (cd apps/frontend && npm install --silent >/dev/null 2>&1) && ok "pacotes npm instalados"
else
  ok "node_modules já presente"
fi

dotnet build --nologo -v q --configuration Release >/dev/null 2>&1 \
  && ok "solução compilada" || die "falha na compilação — rode 'dotnet build' para ver o detalhe"

# ---------------------------------------------------------------- 4. banco
step "Subindo PostgreSQL e Redis"

if [ "$RESET" = "1" ]; then
  warn "--reset: removendo volumes e recriando o banco"
  docker compose down -v >/dev/null 2>&1 || true
fi

docker compose up -d secure-database redis >/dev/null 2>&1 \
  || die "docker compose falhou — verifique 'docker compose logs secure-database'"

printf "  aguardando o banco ficar saudável"
for _ in $(seq 1 40); do
  status="$(docker inspect --format '{{.State.Health.Status}}' "$DB_CONTAINER" 2>/dev/null || echo starting)"
  [ "$status" = "healthy" ] && break
  printf "."; sleep 3
done
printf "\n"
[ "${status:-}" = "healthy" ] || die "banco não ficou saudável. Veja: docker compose logs secure-database"
ok "PostgreSQL pronto"

psql_exec() { docker exec -i "$DB_CONTAINER" psql -U "$DB_MIGRATOR" -d "$DB_NAME" -v ON_ERROR_STOP=1 -q "$@"; }

# ---------------------------------------------------------------- 5. migrations
step "Aplicando migrations"

APPLIED="$(docker exec "$DB_CONTAINER" psql -U "$DB_MIGRATOR" -d "$DB_NAME" -tAc \
  "SELECT count(*) FROM pg_tables WHERE schemaname='public'" 2>/dev/null || echo 0)"

if [ "$RESET" = "1" ] || [ "$APPLIED" -eq 0 ]; then
  for file in database/secure/migrations/V*.sql; do
    psql_exec < "$file" >/dev/null || die "falha em $(basename "$file")"
    ok "$(basename "$file")"
  done
else
  ok "esquema já aplicado ($APPLIED tabelas) — use --reset para recriar"
fi

# ---------------------------------------------------------------- 6. dados
if [ "$SEED" = "1" ]; then
  step "Carregando massa sintética"

  ROWS="$(docker exec "$DB_CONTAINER" psql -U "$DB_MIGRATOR" -d "$DB_NAME" -tAc \
    "SELECT count(*) FROM customers" 2>/dev/null || echo 0)"

  if [ "$ROWS" -eq 0 ]; then
    psql_exec < database/secure/seeds/demo-seed.sql | tail -1 | sed 's/^/  /'
    ok "massa carregada"
  else
    ok "$ROWS cliente(s) já presentes — use --reset para recarregar"
  fi
fi

# ---------------------------------------------------------------- 7. serviços
step "Iniciando serviços"

for port in "$API_PORT" "$WEB_PORT"; do
  if command -v netstat >/dev/null 2>&1 && netstat -ano 2>/dev/null | grep -q ":$port .*LISTENING"; then
    warn "porta $port já está em uso — encerrando processo anterior"
    if [ -f "$LOG_DIR/api.pid" ]; then kill "$(cat "$LOG_DIR/api.pid")" 2>/dev/null || true; fi
    if [ -f "$LOG_DIR/web.pid" ]; then kill "$(cat "$LOG_DIR/web.pid")" 2>/dev/null || true; fi
    sleep 2
  fi
done

nohup dotnet run --project apps/secure-api --no-launch-profile --no-build \
      --configuration Release --urls "http://localhost:$API_PORT" \
      > "$LOG_DIR/api.log" 2>&1 &
echo $! > "$LOG_DIR/api.pid"

printf "  aguardando o backend"
for _ in $(seq 1 30); do
  if curl -sf "http://localhost:$API_PORT/health/ready" >/dev/null 2>&1; then break; fi
  printf "."; sleep 2
done
printf "\n"
curl -sf "http://localhost:$API_PORT/health/ready" >/dev/null 2>&1 \
  || die "backend não respondeu. Veja: $LOG_DIR/api.log"
ok "backend no ar"

nohup npm --prefix apps/frontend run dev -- --host 127.0.0.1 --port "$WEB_PORT" \
      > "$LOG_DIR/web.log" 2>&1 &
echo $! > "$LOG_DIR/web.pid"

printf "  aguardando o frontend"
for _ in $(seq 1 30); do
  if curl -sf -o /dev/null "http://localhost:$WEB_PORT" 2>/dev/null; then break; fi
  printf "."; sleep 2
done
printf "\n"
curl -sf -o /dev/null "http://localhost:$WEB_PORT" 2>/dev/null \
  || die "frontend não respondeu. Veja: $LOG_DIR/web.log"
ok "frontend no ar"

# ---------------------------------------------------------------- 8. resumo
TABLES="$(docker exec "$DB_CONTAINER" psql -U "$DB_MIGRATOR" -d "$DB_NAME" -tAc \
  "SELECT count(*) FROM pg_tables WHERE schemaname='public'")"
POLICIES="$(docker exec "$DB_CONTAINER" psql -U "$DB_MIGRATOR" -d "$DB_NAME" -tAc \
  "SELECT count(*) FROM pg_policies WHERE schemaname='public'")"

cat <<BANNER

${BOLD}════════════════════════════════════════════════════════════════${RESET_C}
${BOLD}  Portal do Corretor — ambiente pronto${RESET_C}
${BOLD}════════════════════════════════════════════════════════════════${RESET_C}

  ${BOLD}Aplicação${RESET_C}
    Portal .................. ${GREEN}http://localhost:$WEB_PORT${RESET_C}
    Administração ........... ${GREEN}http://localhost:$WEB_PORT/#admin${RESET_C}
    Live Processing Console . ${GREEN}http://localhost:$WEB_PORT/#console${RESET_C}

  ${BOLD}API${RESET_C}
    Swagger / OpenAPI ....... ${GREEN}http://localhost:$API_PORT/swagger${RESET_C}
    Especificação OpenAPI ... http://localhost:$API_PORT/swagger/v1/swagger.json
    Base da API ............. http://localhost:$API_PORT/api
    Health (liveness) ....... http://localhost:$API_PORT/health/live
    Health (readiness) ...... http://localhost:$API_PORT/health/ready
    Stream de eventos (SSE) . http://localhost:$API_PORT/api/events/stream

  ${BOLD}Banco de dados${RESET_C}
    PostgreSQL .............. localhost:5432/$DB_NAME
    Tabelas ................. $TABLES
    Políticas de RLS ........ $POLICIES

  ${BOLD}Logs${RESET_C}
    Backend ................. $LOG_DIR/api.log
    Frontend ................ $LOG_DIR/web.log
    Banco ................... docker compose logs -f secure-database

  ${BOLD}Documentação${RESET_C}
    README .................. ./README.md
    ADRs .................... ./docs/adr/
    Modelo físico ........... ./docs/database/physical-model.md

  ${DIM}Encerrar: ./start.sh --stop${RESET_C}
  ${DIM}Recriar o banco: ./start.sh --reset${RESET_C}

BANNER
