"""Read the canonical third-party image pins without external YAML dependencies."""
from pathlib import Path
import re

_PINNED_IMAGE = re.compile(r"[^\s@]+:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}@sha256:[a-f0-9]{64}")


def load_image_pins(path=None, *, required=("camera-edge", "imaginary")):
    path = Path(path) if path else Path(__file__).resolve().parents[1] / 'deploy' / 'images' / 'compose.yaml'
    images = {}
    service = None
    for line in path.read_text(encoding='utf-8').splitlines():
        match = re.fullmatch(r' {2}([A-Za-z0-9_-]+):', line)
        if match:
            service = match.group(1)
            continue
        match = re.fullmatch(r' {4}image:\s*(\S+)\s*', line)
        if match and service:
            if service in images or not _PINNED_IMAGE.fullmatch(match.group(1)):
                raise ValueError(f'{path} has an invalid or duplicate image pin for service {service!r}')
            images[service] = match.group(1)
    for service in required:
        if service not in images:
            raise ValueError(f'{path} must define a tag and sha256-pinned image for service {service!r}')
    return images
