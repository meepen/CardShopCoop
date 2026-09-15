#!/usr/bin/env python3
"""Read TCG Card Shop Simulator's game version without launching the game.

`Application.version` is Unity's `PlayerSettings.bundleVersion`, serialized in
`Card Shop Simulator_Data/globalgamemanagers`. This prints it by parsing that file
directly, so it works offline and on any OS.

Usage
-----
    python scripts/game-version.py
    python scripts/game-version.py --game-path "Z:/SteamLibrary/steamapps/common/TCG Card Shop Simulator"
    python scripts/game-version.py --data-dir "Z:/.../Card Shop Simulator_Data"
    python scripts/game-version.py --quiet        # print only the version (for scripts/CI)

Discovery order when no path is given:
    1. --game-path / --data-dir
    2. $CARDSHOP_GAMEPATH
    3. <GamePath> from the repo's Directory.Build.user.props / Directory.Build.props
    4. the usual Steam library locations for this OS

Requires Python 3.8+ and UnityPy:

    pip install UnityPy

Exit codes: 0 success, 1 game/version not found, 2 UnityPy missing.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

GAME_DIR_NAME = "TCG Card Shop Simulator"
DATA_DIR_NAME = "Card Shop Simulator_Data"
GLOBAL_GAMEMANAGERS = "globalgamemanagers"
REPO_ROOT = Path(__file__).resolve().parent.parent


def _steam_roots() -> list[Path]:
    """Common Steam install roots for Windows, Linux and macOS."""
    home = Path.home()
    roots: list[Path] = []
    if sys.platform.startswith("win"):
        for env in ("ProgramFiles(x86)", "ProgramFiles"):
            base = os.environ.get(env)
            if base:
                roots.append(Path(base) / "Steam")
    elif sys.platform == "darwin":
        roots.append(home / "Library" / "Application Support" / "Steam")
    else:
        roots += [
            home / ".steam" / "steam",
            home / ".steam" / "root",
            home / ".local" / "share" / "Steam",
            home / ".var" / "app" / "com.valvesoftware.Steam" / "data" / "Steam",
        ]
    return roots


def _steam_library_paths() -> list[Path]:
    """All Steam library roots, including extra ones listed in libraryfolders.vdf."""
    seen: list[Path] = []
    for root in _steam_roots():
        if root not in seen:
            seen.append(root)
        vdf = root / "steamapps" / "libraryfolders.vdf"
        try:
            text = vdf.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        for match in re.finditer(r'"path"\s*"([^"]+)"', text):
            lib = Path(match.group(1).replace("\\\\", "\\"))
            if lib not in seen:
                seen.append(lib)
    return seen


def _repo_game_path() -> Path | None:
    """The <GamePath> the build would use, read from the repo props files."""
    for name in ("Directory.Build.user.props", "Directory.Build.props"):
        path = REPO_ROOT / name
        try:
            text = path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        match = re.search(r"<GamePath>([^<]+)</GamePath>", text)
        if match:
            return Path(match.group(1).strip())
    return None


def find_game_dir(explicit: str | None) -> Path | None:
    """Resolve the game install directory, or None."""
    candidates: list[Path] = []
    if explicit:
        p = Path(explicit).expanduser()
        candidates.append(p if p.name != DATA_DIR_NAME else p.parent)

    env = os.environ.get("CARDSHOP_GAMEPATH")
    if env:
        candidates.append(Path(env))

    repo = _repo_game_path()
    if repo:
        candidates.append(repo)

    for lib in _steam_library_paths():
        candidates.append(lib / "steamapps" / "common" / GAME_DIR_NAME)

    for candidate in candidates:
        if (candidate / GLOBAL_GAMEMANAGERS).is_file():
            return candidate
        data = candidate / DATA_DIR_NAME
        if (data / GLOBAL_GAMEMANAGERS).is_file():
            return candidate
    return None


def locate_data_dir(args: argparse.Namespace) -> Path | None:
    """The *_Data directory that holds globalgamemanagers."""
    for raw in (args.data_dir, args.game_path):
        if not raw:
            continue
        p = Path(raw).expanduser()
        if p.is_file() and p.name == GLOBAL_GAMEMANAGERS:
            return p.parent
        if (p / GLOBAL_GAMEMANAGERS).is_file():
            return p
        if (p / DATA_DIR_NAME / GLOBAL_GAMEMANAGERS).is_file():
            return p / DATA_DIR_NAME
    game = find_game_dir(args.game_path)
    return game / DATA_DIR_NAME if game else None


def read_player_settings(ggm_path: Path) -> dict:
    import UnityPy  # imported lazily so --help works without the dependency

    env = UnityPy.load(str(ggm_path))
    for obj in env.objects:
        if obj.type.name != "PlayerSettings":
            continue
        try:
            data = obj.read_typetree()
        except Exception:
            # UnityPy's bundled typetree can lag a brand-new Unity version by a few
            # bytes; that only trips the redundant size check, so parse anyway.
            data = obj.read_typetree(check_read=False)
        if data is None:
            raise RuntimeError("PlayerSettings typetree decoded to nothing")
        return data
    raise RuntimeError("no PlayerSettings object in " + ggm_path.name)


def read_unity_version(ggm_path: Path) -> str | None:
    """Best effort: the engine version is a plain string near the file header."""
    try:
        head = ggm_path.read_bytes()[:512]
    except OSError:
        return None
    match = re.search(rb"(\d+\.\d+\.\d+[abfp]\d+)", head)
    return match.group(1).decode("ascii") if match else None


def resolve_version(args: argparse.Namespace) -> tuple[str, str | None]:
    data_dir = locate_data_dir(args)
    if data_dir is None:
        raise FileNotFoundError(
            "could not locate the game. Pass --game-path, or set CARDSHOP_GAMEPATH."
        )
    ggm = data_dir / GLOBAL_GAMEMANAGERS
    if not ggm.is_file():
        raise FileNotFoundError(f"{GLOBAL_GAMEMANAGERS} not found under {data_dir}")

    settings = read_player_settings(ggm)
    version = settings.get("bundleVersion")
    if not isinstance(version, str) or not version.strip():
        raise RuntimeError(
            "PlayerSettings has no bundleVersion (game build too old, or parser drift)"
        )
    return version.strip(), read_unity_version(ggm)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Print the installed TCG Card Shop Simulator game version (Application.version)."
    )
    parser.add_argument("--game-path", help="game install directory (or its *_Data directory)")
    parser.add_argument("--data-dir", help="Card Shop Simulator_Data directory")
    parser.add_argument(
        "--quiet", action="store_true", help="print only the version (for scripts/CI)"
    )
    args = parser.parse_args(argv)

    try:
        import UnityPy  # noqa: F401
    except ImportError:
        print(
            "error: UnityPy is required. Install it with:  pip install UnityPy",
            file=sys.stderr,
        )
        return 2

    try:
        version, unity = resolve_version(args)
    except Exception as exc:  # fail loud with one actionable line
        print(f"error: {exc}", file=sys.stderr)
        return 1

    if args.quiet:
        print(version)
    else:
        print(f"game version: {version}")
        if unity:
            print(f"unity version: {unity}")
        # Handy for the decompiled/<gameversion>/ convention in AGENTS.md.
        print(f"suggested decompile dir: decompiled/{version}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
