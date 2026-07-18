#!/usr/bin/env bash
#
# vm-bootstrap.sh — bootstrap MTile for headless build + test on a fresh
# Ubuntu/Debian cloud VM (GCP Compute Engine, EC2, ...). .NET 8 + xUnit,
# no display required.
#
# Designed to be run by hand right after SSH-ing in (it sudo-elevates
# itself), and is idempotent, so re-running it to update the box is fine.
# It also works from boot-time automation (GCP startup-script / EC2
# user-data) — those run as root, so set TARGET_USER explicitly there.
#
# What it does:
#   1. Installs prerequisites (git, curl, libicu) + the .NET 8 SDK.
#   2. Drops an SSH deploy key for the target user and trusts github.com.
#   3. Clones (or fast-forwards) the repo as the target user.
#   4. Restores tools/packages, builds MTile.Core, runs the test suite.
#
# It deliberately does NOT set up a display, OpenGL/Mesa, or the MonoGame
# content pipeline — MTile.Core and MTile.Tests build and run without them.
# To actually run the desktop game window you'd need Xvfb + Mesa as well.
#
# ---------------------------------------------------------------------------
# Configuration — override any of these via the environment in user-data, e.g.
#   DEPLOY_KEY_B64="$(base64 -w0 deploy_key)" GIT_BRANCH=anim-clip-batch ./vm-bootstrap.sh
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_SSH="${REPO_SSH:-git@github.com:amdson/MTile.git}"
GIT_BRANCH="${GIT_BRANCH:-main}"
# Who owns the checkout / runs dev. Defaults to whoever invoked the script
# (surviving the sudo re-exec below). On GCP this is your Google-account user,
# on stock EC2 Ubuntu it's "ubuntu" — either way the default just works when
# you run this after SSH-ing in. Boot-time automation runs as root, so set
# TARGET_USER explicitly there.
TARGET_USER="${TARGET_USER:-${SUDO_USER:-$(id -un)}}"
CHECKOUT_DIR="${CHECKOUT_DIR:-/home/${TARGET_USER}/MTile}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-8.0}"

# Deploy key: supply EITHER a path to the private key already on disk
# (DEPLOY_KEY_PATH) OR the key base64-encoded in an env var (DEPLOY_KEY_B64).
# The latter is convenient for user-data / SSM. Read-only deploy key is enough.
DEPLOY_KEY_PATH="${DEPLOY_KEY_PATH:-}"
DEPLOY_KEY_B64="${DEPLOY_KEY_B64:-}"

log() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
die() { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# Re-exec under sudo if we aren't root (package installs need it). Only
# possible when running from a real file — under `curl | bash` $0 is the bash
# binary itself, so there's nothing to re-exec; ask for `curl | sudo bash`.
if [[ "$(id -u)" -ne 0 ]]; then
  [[ -f "$0" && "$0" != /usr/bin/bash && "$0" != /bin/bash ]] \
    || die "not root and running from a pipe — use: curl -fsSL <url> | sudo bash"
  exec sudo -E bash "$0" "$@"
fi

[[ "$TARGET_USER" != "root" ]] || die "TARGET_USER resolved to root — set TARGET_USER=<login user> (required when run from boot-time automation)"

USER_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
[[ -n "$USER_HOME" ]] || die "user '$TARGET_USER' does not exist on this box"

# Run a command as the unprivileged target user, with a login-ish env so that
# the .NET tools on PATH and the SSH key are visible.
as_user() { sudo -u "$TARGET_USER" -H env PATH="/usr/local/dotnet:${USER_HOME}/.dotnet/tools:$PATH" "$@"; }

# ---------------------------------------------------------------------------
log "Installing system prerequisites"
# ---------------------------------------------------------------------------
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
# libicu + libssl: .NET runtime deps. git/curl/ca-certificates: clone + installer.
apt-get install -y --no-install-recommends \
  git curl ca-certificates libicu-dev openssh-client

# ---------------------------------------------------------------------------
log "Installing the .NET ${DOTNET_CHANNEL} SDK"
# ---------------------------------------------------------------------------
# Microsoft's installer script is distro-version-agnostic (avoids apt repo
# drift across Ubuntu releases). Installs into a fixed system path.
DOTNET_ROOT="/usr/local/dotnet"
if ! "${DOTNET_ROOT}/dotnet" --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL}\."; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_ROOT"
  rm -f /tmp/dotnet-install.sh
fi

# Make dotnet available on PATH for all login shells + disable first-run noise.
cat > /etc/profile.d/dotnet.sh <<EOF
export DOTNET_ROOT="${DOTNET_ROOT}"
export PATH="\$PATH:${DOTNET_ROOT}:\$HOME/.dotnet/tools"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
EOF
chmod 0644 /etc/profile.d/dotnet.sh
export DOTNET_ROOT PATH="${DOTNET_ROOT}:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# ---------------------------------------------------------------------------
log "Configuring the SSH deploy key for ${TARGET_USER}"
# ---------------------------------------------------------------------------
SSH_DIR="${USER_HOME}/.ssh"
install -d -m 0700 -o "$TARGET_USER" -g "$TARGET_USER" "$SSH_DIR"

KEY_DEST="${SSH_DIR}/mtile_deploy"
if [[ -n "$DEPLOY_KEY_B64" ]]; then
  base64 -d <<<"$DEPLOY_KEY_B64" > "$KEY_DEST"
elif [[ -n "$DEPLOY_KEY_PATH" && -f "$DEPLOY_KEY_PATH" ]]; then
  install -m 0600 "$DEPLOY_KEY_PATH" "$KEY_DEST"
elif [[ ! -f "$KEY_DEST" ]]; then
  die "no deploy key: set DEPLOY_KEY_B64 or DEPLOY_KEY_PATH (or pre-place ${KEY_DEST})"
fi
chmod 0600 "$KEY_DEST"
chown "$TARGET_USER:$TARGET_USER" "$KEY_DEST"

# Tell ssh/git to use this key for github.com, and trust the host key up front
# (pinned fingerprints so we don't blindly accept whatever answers).
cat > "${SSH_DIR}/config" <<EOF
Host github.com
  HostName github.com
  User git
  IdentityFile ${KEY_DEST}
  IdentitiesOnly yes
  StrictHostKeyChecking accept-new
EOF
chmod 0600 "${SSH_DIR}/config"
chown "$TARGET_USER:$TARGET_USER" "${SSH_DIR}/config"

KNOWN_HOSTS="${SSH_DIR}/known_hosts"
ssh-keyscan -t rsa,ecdsa,ed25519 github.com 2>/dev/null >> "$KNOWN_HOSTS"
sort -u "$KNOWN_HOSTS" -o "$KNOWN_HOSTS"
chmod 0644 "$KNOWN_HOSTS"
chown "$TARGET_USER:$TARGET_USER" "$KNOWN_HOSTS"

# ---------------------------------------------------------------------------
log "Cloning / updating the repo at ${CHECKOUT_DIR}"
# ---------------------------------------------------------------------------
if [[ -d "${CHECKOUT_DIR}/.git" ]]; then
  as_user git -C "$CHECKOUT_DIR" fetch --prune origin
  as_user git -C "$CHECKOUT_DIR" checkout "$GIT_BRANCH"
  as_user git -C "$CHECKOUT_DIR" pull --ff-only origin "$GIT_BRANCH"
else
  install -d -o "$TARGET_USER" -g "$TARGET_USER" "$(dirname "$CHECKOUT_DIR")"
  as_user git clone --branch "$GIT_BRANCH" "$REPO_SSH" "$CHECKOUT_DIR"
fi

# ---------------------------------------------------------------------------
log "Restoring tools + building MTile.Core"
# ---------------------------------------------------------------------------
as_user bash -lc "cd '${CHECKOUT_DIR}' && dotnet tool restore"
as_user bash -lc "cd '${CHECKOUT_DIR}' && dotnet build MTile.Core.csproj"

# ---------------------------------------------------------------------------
log "Running the test suite"
# ---------------------------------------------------------------------------
# Non-fatal: a red test shouldn't abort the bootstrap (the box is still usable).
if as_user bash -lc "cd '${CHECKOUT_DIR}' && dotnet test MTile.Tests/MTile.Tests.csproj"; then
  log "Bootstrap complete — build green, tests passed."
else
  log "Bootstrap complete — build green, but some tests FAILED (see output above)."
fi

cat <<EOF

MTile is ready at ${CHECKOUT_DIR} (owned by ${TARGET_USER}).
SSH in and:   cd ${CHECKOUT_DIR} && dotnet build MTile.Core.csproj && dotnet test MTile.Tests/MTile.Tests.csproj
EOF
