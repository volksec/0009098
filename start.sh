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

# Encerra API, workers e frontend de uma execução anterior.
#
# Precisa rodar ANTES do build: no Windows o .NET não sobrescreve um .exe em uso,
# então rodar o script duas vezes seguidas falhava na compilação.
#
# Não dá para confiar apenas no .pid: sob Git Bash, `cmd &` registra o PID do job do
# bash, não o do processo do Windows. `dotnet run` e `npm` ainda criam um filho, então
# o processo que realmente segura a porta é neto do pid registrado — e `kill` no pid
# registrado o deixava vivo. Por isso o encerramento é feito pelo nome da imagem
# (as duas são exclusivas deste projeto) e, para o frontend, por quem ocupa a porta.
stop_app_processes() {
  local mode="${1:-quiet}" stopped=0

  if command -v taskkill >/dev/null 2>&1; then
    for image in PortalDoCorretor.SecureApi.exe PortalDoCorretor.Workers.exe; do
      if taskkill //F //T //IM "$image" >/dev/null 2>&1; then
        [ "$mode" = "verbose" ] && ok "${image%.exe} encerrado"
        stopped=1
      fi
    done

    # O Vite roda dentro do node; localiza pela porta em vez do nome
    if command -v netstat >/dev/null 2>&1; then
      for winpid in $(netstat -ano 2>/dev/null | grep ":$WEB_PORT .*LISTENING"                       | awk '{print $NF}' | sort -u); do
        if taskkill //F //T //PID "$winpid" >/dev/null 2>&1; then
          [ "$mode" = "verbose" ] && ok "frontend (pid $winpid) encerrado"
          stopped=1
        fi
      done
    fi
  fi

  # Fallback POSIX, e limpeza dos arquivos de pid
  for name in api workers web; do
    if [ -f "$LOG_DIR/$name.pid" ]; then
      kill "$(cat "$LOG_DIR/$name.pid")" 2>/dev/null && stopped=1
      rm -f "$LOG_DIR/$name.pid"
    fi
  done

  if [ "$stopped" = "1" ]; then
    # Dá tempo do sistema liberar os binários antes do próximo build
    sleep 3
  else
    [ "$mode" = "verbose" ] && warn "nenhum serviço estava em execução"
  fi
  return 0
}

# ---------------------------------------------------------------- parar
if [ "$STOP" = "1" ]; then
  step "Encerrando serviços"
  stop_app_processes verbose
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

# Gera um segredo local e o grava na variável indicada do .env
gerar_segredo() {
  local var="$1" tamanho="${2:-24}" bytes="${3:-18}"
  local secret
  secret="$(head -c "$bytes" /dev/urandom | base64 | tr -d '/+=' | head -c "$tamanho")"
  sed -i.bak "s|^${var}=.*|${var}=${secret}|" .env && rm -f .env.bak
}

if [ ! -f .env ]; then
  cp .env.example .env
  ok ".env criado a partir do exemplo"
fi

# Acrescenta chave que instalação anterior não tinha, sem tocar no resto do arquivo
grep -q '^JWT_SIGNING_KEY=' .env || echo 'JWT_SIGNING_KEY=troque_este_valor_local' >> .env

# Troca TODO marcador que tenha sobrado — inclusive em .env preservado de instalação
# anterior. Sem isto o arquivo podia continuar com "troque_este_valor_local" enquanto o
# banco fora criado com senha aleatória: a API subia e devolvia 500 em toda requisição,
# sem nada no console dizendo o porquê.
trocados=0
for var in POSTGRES_APP_USER_PASSWORD POSTGRES_APP_REGULATOR_PASSWORD POSTGRES_APP_WORKER_PASSWORD; do
  if grep -q "^${var}=troque_este_valor_local$" .env; then
    gerar_segredo "$var" 24 18
    trocados=$((trocados + 1))
  fi
done

if grep -q '^JWT_SIGNING_KEY=troque_este_valor_local$' .env; then
  # 48 bytes, bem acima do mínimo de 32 do HMAC-SHA256
  gerar_segredo JWT_SIGNING_KEY 64 48
  trocados=$((trocados + 1))
fi

if [ "$trocados" -gt 0 ]; then
  ok "$trocados segredo(s) gerado(s) localmente no .env"
else
  ok ".env já configurado (preservado)"
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

# Libera os binarios: o .NET nao sobrescreve um .exe em uso
stop_app_processes

dotnet build --nologo -v q --configuration Release >/dev/null 2>&1 \
  && ok "solução compilada" || die "falha na compilação — rode 'dotnet build' para ver o detalhe"

# ---------------------------------------------------------------- 4. banco
step "Subindo PostgreSQL"

if [ "$RESET" = "1" ]; then
  warn "--reset: removendo volumes e recriando o banco"
  docker compose down -v >/dev/null 2>&1 || true
fi

# --remove-orphans limpa contêiner de serviço que saiu do compose — o redis, retirado por
# não ser usado por nenhuma linha de código. Sem isso o aviso de órfão derruba o script em
# quem já tinha o ambiente de pé.
docker compose up -d --remove-orphans secure-database >/dev/null 2>&1 \
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

# A credencial da aplicação precisa ser testada de verdade, e não presumida.
#
# As senhas dos papéis são gravadas quando o volume nasce. Se o .env for trocado depois
# — outro clone, arquivo restaurado, marcador que sobrou —, o banco continua com a senha
# antiga e a API sobe normalmente para devolver 500 em toda requisição. O sintoma aparece
# longe da causa: o console diz "ambiente pronto" e a tela mostra erro.
#
# A verificação usa o papel app_user pela porta publicada, que é o caminho exato da
# aplicação. Conexão de dentro do contêiner não serve: é trust e passaria mesmo errada.
if ! docker run --rm --network host -e PGPASSWORD="${POSTGRES_APP_USER_PASSWORD:-}"      postgres:16.4-alpine psql -h 127.0.0.1 -p 5432 -U app_user -d "$DB_NAME"      -tAc 'SELECT 1' >/dev/null 2>&1; then
  die "o banco recusou a senha de app_user do .env.

  O volume foi criado com outra senha. Recrie o banco com o .env atual:

      ./start.sh --reset

  Isso apaga os dados locais — que são sintéticos e recarregados pelo seed."
fi
ok "credencial da aplicação verificada" 

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
    warn "porta $port ainda ocupada — aguardando liberação"
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

# Workers: Outbox Dispatcher, Renewal Scanner, Billing Scheduler, Quotation Expirer
# e Integrity Checker. Sem eles a API funciona, mas a Outbox acumula e as parcelas
# não são marcadas como vencidas.
nohup dotnet run --project apps/workers --no-build --configuration Release \
      > "$LOG_DIR/workers.log" 2>&1 &
echo $! > "$LOG_DIR/workers.pid"
sleep 3
if grep -q "iniciado" "$LOG_DIR/workers.log" 2>/dev/null; then
  ok "workers no ar ($(grep -c 'iniciado' "$LOG_DIR/workers.log") em execução)"
else
  warn "workers podem não ter iniciado — veja $LOG_DIR/workers.log"
fi

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
    Workers ................. $LOG_DIR/workers.log
    Frontend ................ $LOG_DIR/web.log
    Banco ................... docker compose logs -f secure-database

  ${BOLD}Documentação${RESET_C}
    README .................. ./README.md
    ADRs .................... ./docs/adr/
    Modelo físico ........... ./docs/database/physical-model.md

  ${DIM}Encerrar: ./start.sh --stop${RESET_C}
  ${DIM}Recriar o banco: ./start.sh --reset${RESET_C}

BANNER
