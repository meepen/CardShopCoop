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

Create a fine-grained token for `meepen/CardShopCoop` with repository **Administration: Read
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
git tag v2.0.1
git push origin v2.0.1
```

The tag must match the version exactly. GitHub Actions then runs `scripts/build.sh` (builds and
packages every distributable into `dist/`), and publishes through three independent jobs so one
failing cannot block the others: the GitHub release (`scripts/publish-github.sh`), Thunderstore
(`scripts/publish-thunderstore.sh`, stable tags only), and Nexus Mods (the official upload action).

## Publishing setup (one-time)

- **Thunderstore** (`tcg-card-shop-simulator`, team `CardShopCoop`): create the team, add a Service
  Account, and store its token as the repository secret `THUNDERSTORE_TOKEN`. Add the mod icon at
  `thunderstore/icon.png` (must be a 256x256 PNG). Packages: `CardShopCoopCommunity` and
  `CardShopCoopCommunity_ExternalModInterop`.
- **Nexus Mods**: create the mod page and one file group per package, then store the API key as the
  secret `NEXUSMODS_API_KEY` and the file-group ids as repository variables `NEXUSMODS_FILE_ID`
  (core) and `NEXUSMODS_INTEROP_FILE_ID` (ExternalModInterop). Nexus uploads are skipped until those
  variables are set.
- The runner image installs `zip`/`unzip` (see `docker/Dockerfile`); rebuild it with
  `docker compose --env-file .env -f docker/compose.yml up -d --build` after changing it.
