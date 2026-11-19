"""Read the canonical third-party image pins without external YAML dependencies."""
from pathlib import Path
import re

_DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


def _has_tag_and_digest(reference):
    tagged_name, _, digest = reference.rpartition('@')
    # The tag follows the repository name and cannot contain an @.
    tag_separator = tagged_name.find(':', max(1, tagged_name.rfind('@') + 1))
    return 0 <= tag_separator < len(tagged_name) - 1 and _DIGEST.fullmatch(digest) is not None


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
            if service in images or not _has_tag_and_digest(match.group(1)):
                raise ValueError(f'{path} has an invalid or duplicate image pin for service {service!r}')
            images[service] = match.group(1)
    for service in required:
        if service not in images:
            raise ValueError(f'{path} must define a tag and sha256-pinned image for service {service!r}')
    return images
