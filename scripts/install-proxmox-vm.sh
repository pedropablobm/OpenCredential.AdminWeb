#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/opencredential-adminweb}"
REPO_URL="${REPO_URL:-https://github.com/pedropablobm/OpenCredential.AdminWeb.git}"
BRANCH="${BRANCH:-main}"
ADMINWEB_PORT="${ADMINWEB_PORT:-8080}"
ADMIN_USERNAME="${ADMIN_USERNAME:-admin}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-$(openssl rand -base64 24 | tr -d '\n')}"
POSTGRES_DB="${POSTGRES_DB:-opencredential_admin}"
POSTGRES_USER="${POSTGRES_USER:-opencredential}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-$(openssl rand -base64 24 | tr -d '\n')}"

if [ "$(id -u)" -ne 0 ]; then
  echo "Ejecuta este script como root o con sudo."
  exit 1
fi

if ! command -v apt-get >/dev/null 2>&1; then
  echo "Este instalador esta pensado para Debian/Ubuntu dentro de una VM o LXC."
  exit 1
fi

echo "==> Actualizando paquetes base"
apt-get update
apt-get install -y ca-certificates curl git openssl gnupg lsb-release

if ! command -v docker >/dev/null 2>&1; then
  echo "==> Instalando Docker Engine"
  install -m 0755 -d /etc/apt/keyrings
  rm -f /etc/apt/keyrings/docker.gpg
  . /etc/os-release
  case "${ID}" in
    debian|ubuntu)
      DOCKER_OS="${ID}"
      ;;
    *)
      echo "Distribucion no soportada automaticamente: ${ID}. Usa Debian o Ubuntu."
      exit 1
      ;;
  esac
  curl -fsSL "https://download.docker.com/linux/${DOCKER_OS}/gpg" | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  chmod a+r /etc/apt/keyrings/docker.gpg
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/${DOCKER_OS} ${VERSION_CODENAME} stable" > /etc/apt/sources.list.d/docker.list
  apt-get update
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
else
  echo "==> Docker ya esta instalado"
fi

systemctl enable --now docker

echo "==> Preparando aplicacion en ${APP_DIR}"
if [ -d "${APP_DIR}/.git" ]; then
  git -C "${APP_DIR}" fetch origin "${BRANCH}"
  git -C "${APP_DIR}" checkout "${BRANCH}"
  git -C "${APP_DIR}" pull --ff-only origin "${BRANCH}"
else
  mkdir -p "$(dirname "${APP_DIR}")"
  git clone --branch "${BRANCH}" "${REPO_URL}" "${APP_DIR}"
fi

echo "==> Creando archivo .env"
cat > "${APP_DIR}/.env" <<EOF
ADMINWEB_PORT=${ADMINWEB_PORT}
ADMIN_USERNAME=${ADMIN_USERNAME}
ADMIN_PASSWORD=${ADMIN_PASSWORD}
ADMIN_ROLE=SuperAdmin
POSTGRES_DB=${POSTGRES_DB}
POSTGRES_USER=${POSTGRES_USER}
POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
EOF

chmod 600 "${APP_DIR}/.env"

echo "==> Levantando contenedores"
docker compose --project-directory "${APP_DIR}" up -d --build

echo
echo "Instalacion completada."
echo "URL: http://$(hostname -I | awk '{print $1}'):${ADMINWEB_PORT}"
echo "Usuario administrador: ${ADMIN_USERNAME}"
echo "Clave administrador: ${ADMIN_PASSWORD}"
echo
echo "Guarda estas credenciales. Tambien quedaron en ${APP_DIR}/.env con permisos 600."
echo
echo "Comandos utiles:"
echo "  cd ${APP_DIR} && docker compose ps"
echo "  cd ${APP_DIR} && docker compose logs -f opencredential-adminweb"
echo "  cd ${APP_DIR} && docker compose pull && docker compose up -d --build"
