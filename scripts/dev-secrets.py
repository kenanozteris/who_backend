#!/usr/bin/env python3
"""Fill missing local .env secrets only. Never rotate existing secrets silently."""
import base64
from pathlib import Path
import secrets

root = Path(__file__).resolve().parent.parent
env = root / '.env'
if not env.exists():
    env.write_text((root / '.env.example').read_text())
lines = env.read_text().splitlines()
defaults = {
    'WHO_POSTGRES_PASSWORD': lambda: secrets.token_hex(32),
    'WHO_AUTH_VERIFICATION_PEPPER': lambda: base64.b64encode(secrets.token_bytes(48)).decode(),
    'WHO_AUTH_JWT_SIGNING_KEY': lambda: base64.b64encode(secrets.token_bytes(48)).decode(),
    'WHO_AUTH_JWT_ISSUER': lambda: 'who-local',
    'WHO_AUTH_JWT_AUDIENCE': lambda: 'who-local-client',
}
for key, generate in defaults.items():
    found = False
    for index, line in enumerate(lines):
        if line.startswith(key + '='):
            found = True
            if not line.split('=', 1)[1].strip().strip('"\''):
                lines[index] = key + '=' + generate()
            break
    if not found:
        lines.append(key + '=' + generate())
env.write_text('\n'.join(lines) + '\n')
env.chmod(0o600)
print('Local .env ready; existing values preserved, missing secrets filled without disclosure.')
