"""Read the canonical third-party image pins without external YAML dependencies."""
from pathlib import Path
import re

_PIN = re.compile(r"\S+:[^\s@]+@sha256:[0-9a-f]{64}\Z")


def load_image_pins(path=None):
    path = Path(path) if path else Path(__file__).resolve().parents[1] / 'deploy' / 'images' / 'compose.yaml'
    images = {}
    service = None
    for line in path.read_text(encoding='utf-8').splitlines():
        match = re.fullmatch(r'  ([A-Za-z0-9_-]+):', line)
        if match:
            service = match.group(1)
            continue
        match = re.fullmatch(r'    image:\s*(\S+)\s*', line)
        if match and service:
            if service in images or not _PIN.fullmatch(match.group(1)):
                raise ValueError(f'{path} has an invalid or duplicate image pin for service {service!r}')
            images[service] = match.group(1)
    for service in ('camera-edge', 'imaginary'):
        if service not in images:
            raise ValueError(f'{path} must define a tag and sha256-pinned image for service {service!r}')
    return images
