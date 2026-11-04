"""Render a self-contained VPS installer for one qualified GHCR digest."""
import argparse
from pathlib import Path
import re

IMAGE = re.compile(r"ghcr\.io/kittawat-thongpud/personal-vpn-server/p2p-signal@sha256:[0-9a-f]{64}")


def render(image):
    if not IMAGE.fullmatch(image):
        raise ValueError("Expected the qualified P2P signaling image and a SHA-256 digest")
    here = Path(__file__).resolve().parent
    compose = (here / "compose.yaml").read_text(encoding="utf-8")
    old = "image: personal-vpn/p2p-signal:lab.2"
    if compose.count(old) != 1:
        raise ValueError("Expected exactly one signaling service image")
    compose = compose.replace(old, f"image: {image}").rstrip()
    script = (here / "install-ghcr.template.sh").read_text(encoding="utf-8")
    script = script.replace("@@IMAGE_REF@@", image).replace("@@COMPOSE@@", compose)
    if "@@" in script:
        raise ValueError("Unresolved installer placeholder")
    return script


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("image")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.write_text(render(args.image), encoding="utf-8", newline="\n")
