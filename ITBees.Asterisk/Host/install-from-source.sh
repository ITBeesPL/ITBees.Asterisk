#!/bin/bash
# Builds and installs Asterisk from the official sources. Used by ITBees.Asterisk on systems whose package
# repositories have no "asterisk" package (Debian 12, Raspberry Pi OS bookworm). Runs as root, takes no arguments,
# prints one "[itbees] <time> <n>/7 <what>" line per step so the caller can show progress.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

SERIES=22
BASE_URL="https://downloads.asterisk.org/pub/telephony/asterisk"
TARBALL="asterisk-${SERIES}-current.tar.gz"
SRC=/usr/src/itbees-asterisk
DOWNLOAD=/tmp/itbees-asterisk.tar.gz

step() { echo "[itbees] $(date +%H:%M:%S) $*"; }

step "1/7 installing build tools"
apt-get update -q || true
apt-get install -y -q build-essential wget ca-certificates

step "2/7 downloading ${TARBALL}"
wget -q -O "$DOWNLOAD" "${BASE_URL}/${TARBALL}"
# The .sha256 file names the versioned tarball, so only the hash is compared (catches a cut-off download).
expected=$(wget -q -O - "${BASE_URL}/asterisk-${SERIES}-current.sha256" | awk '{print $1}')
actual=$(sha256sum "$DOWNLOAD" | awk '{print $1}')
if [ -z "$expected" ] || [ "$expected" != "$actual" ]; then
  echo "[itbees] checksum of ${TARBALL} does not match (expected '${expected}', got '${actual}')" >&2
  exit 1
fi
rm -rf "$SRC"
mkdir -p "$SRC"
tar xzf "$DOWNLOAD" -C "$SRC" --strip-components=1
rm -f "$DOWNLOAD"
cd "$SRC"

step "3/7 installing build dependencies"
contrib/scripts/install_prereq install

step "4/7 configuring"
./configure --with-jansson-bundled --with-pjproject-bundled

# About 1 GB of memory per compiler job: a small board runs out of memory with one job per core.
mem_gb=$(awk '/MemTotal/ {print int($2 / 1048576)}' /proc/meminfo)
jobs=$(nproc)
if [ "$mem_gb" -lt 1 ]; then mem_gb=1; fi
if [ "$jobs" -gt "$mem_gb" ]; then jobs=$mem_gb; fi

step "5/7 compiling with ${jobs} job(s) - the longest step"
make -j"$jobs"

step "6/7 installing"
make install
# Sample configuration only on a machine that has none: "make samples" would replace existing files.
if [ ! -f /etc/asterisk/asterisk.conf ]; then
  make samples
fi
# Without it /var/log/asterisk grows until the disk is full.
make install-logrotate
ldconfig

step "7/7 registering the service"
cat > /etc/systemd/system/asterisk.service <<'UNIT'
[Unit]
Description=Asterisk PBX (installed by ITBees.Asterisk)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/usr/sbin/asterisk -f -C /etc/asterisk/asterisk.conf
ExecReload=/usr/sbin/asterisk -rx "core reload"
ExecStop=/usr/sbin/asterisk -rx "core stop now"
Restart=always
RestartSec=5
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable asterisk

/usr/sbin/asterisk -V
# The build tree takes almost 1 GB and is not needed to run Asterisk.
cd /
rm -rf "$SRC"
step "done"
