# Self-hosted release runner

This directory contains everything needed for the Dockerized, ephemeral GitHub Actions
runner. The game files are kept in the ignored `game/` directory beside this README and are
mounted read-only into the runner as compile-time references.

Run the commands below from this directory. Docker, Docker Compose, Git, `curl`, and `unzip`
are assumed to already be installed.

## Install game references

The installer interactively logs into Steam, downloads the Windows game files, and installs
BepInEx 5.4.23 x64 and caches the Steam login session locally in the ignored `steam/`
directory:

```bash
bash install-game.sh
```

Steam credentials and Steam Guard codes are entered directly into SteamCMD. They are not
stored in the repository or GitHub.

## Configure the runner

Create a fine-grained token for `DeliriumPulse/CardShopCoop` with repository **Administration: Read
and write** permission. Store it only in `.env`:

```bash
cat > .env <<'EOF'
RUNNER_ACCESS_TOKEN=replace-with-your-token
DOTNET_INSTALL_VERSION=9.0.100
EOF
chmod 600 .env
nano .env
```

Build and start the runner:

```bash
docker compose --env-file .env -f docker/compose.yml up -d --build
docker compose --env-file .env -f docker/compose.yml logs -f runner
```

The runner should appear in the repository's **Settings → Actions → Runners** with the
`cardshop-game` label. It is ephemeral and automatically registers again after each job.

## Update game files

```bash
docker compose --env-file .env -f docker/compose.yml down
bash install-game.sh
docker compose --env-file .env -f docker/compose.yml up -d --build
```

## Release

Update `CardShopCoopVersion` in `../Directory.Build.props`, commit it, and push a matching
tag:

```bash
git tag v1.2.0
git push origin v1.2.0
```

The tag must match the version exactly. GitHub Actions builds and publishes the package as a
GitHub Release.
