#!/bin/bash
# Installe (ou met à jour) Lutins : télécharge le code, le compile sur ce Mac et lance l'app.
#
#   curl -fsSL https://raw.githubusercontent.com/lucasjanvierpro-oss/lutins/main/install.sh | bash
set -euo pipefail

REPO="https://github.com/lucasjanvierpro-oss/lutins.git"
say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31m✗ %s\033[0m\n\n' "$*" >&2; exit 1; }

[[ "$(uname)" == "Darwin" ]] || fail "Lutins ne marche que sur Mac."
version=$(sw_vers -productVersion)
(( ${version%%.*} >= 13 )) || fail "Il faut macOS 13 (Ventura) ou plus récent. Ce Mac a macOS $version."

if ! xcode-select -p >/dev/null 2>&1 || ! xcrun --find swiftc >/dev/null 2>&1; then
  say "Il manque les outils de développement d'Apple (gratuits)."
  echo "Une fenêtre va s'ouvrir : clique sur « Installer » et attends la fin (5 à 15 minutes)."
  echo "Ensuite, relance exactement la même commande."
  xcode-select --install >/dev/null 2>&1 || true
  exit 1
fi

say "Téléchargement de Lutins…"
dir=$(mktemp -d)
trap 'rm -rf "$dir"' EXIT
git clone --quiet --depth 1 "$REPO" "$dir/lutins"

say "Compilation (une minute environ)…"
"$dir/lutins/build.sh" --install

say "✓ Lutins est installé !"
cat <<'EOF'
  • L'encoche est en haut de ton écran.
  • Si macOS te le demande, clique sur « Autoriser » pour les notifications.
  • Ouvre une nouvelle conversation dans Claude Code : un perso apparaît dès que tu écris.
  • Réglages (taille, perso, écran…) : clic droit sur l'encoche.
  • Pour mettre à jour plus tard : relance cette même commande.

EOF
