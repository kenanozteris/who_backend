#!/usr/bin/env python3
"""Local synthetic Auth/Mailpit flow. Prints redacted outcomes only; tokens stay in memory."""
import datetime as dt
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid

root = Path(__file__).resolve().parent.parent
os.chdir(root)
api = os.environ.get('WHO_API_URL', 'http://127.0.0.1:5080')
mailpit = 'http://127.0.0.1:' + os.environ.get('WHO_MAILPIT_WEB_PORT', '8025')
docker = '/Applications/Docker.app/Contents/Resources/bin/docker'
sensitive = []
codes = []

def http(method, url, body=None, access=None):
    headers = {'Accept': 'application/json'}
    if body is not None:
        headers['Content-Type'] = 'application/json'
    if access is not None:
        headers['Authorization'] = 'Bearer ' + access
    request = urllib.request.Request(url, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read()
        value = json.loads(raw) if raw else {}
        for name in ('accessToken', 'refreshToken', 'onboardingToken'):
            if isinstance(value, dict) and name in value:
                sensitive.append(value[name])
        return response.status, value

def call(path, body=None, access=None, method='POST'):
    return http(method, api + path, body, access)

def expect(status, expected, label, value=None, code=None):
    if status != expected or (code and value.get('code') != code):
        raise RuntimeError('Redacted smoke failure: ' + label + ' HTTP ' + str(status))
    print(label + ': HTTP ' + str(status) + (' ' + code if code else ''))

def sql(query):
    command = [docker, 'compose', 'exec', '-T', 'postgres', 'sh', '-c',
        'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tA -v ON_ERROR_STOP=1']
    result = subprocess.run(command, input=query, text=True, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError('Redacted database smoke query failed.')
    return result.stdout.strip()

def mailbox(email):
    status, data = http('GET', mailpit + '/api/v1/messages')
    if status != 200:
        raise RuntimeError('Mailpit inbox unavailable.')
    return [m for m in data.get('messages', []) if any(t.get('Address', '').lower() == email.lower() for t in m.get('To', []))]

def main():
    today = dt.datetime.now(dt.timezone.utc).date()
    suffix = uuid.uuid4().hex
    email = 'auth-smoke-' + suffix + '@example.test'
    username = 'auth_smoke_' + suffix
    password = secrets.token_urlsafe(28)
    sensitive.append(password)
    underage_email = 'underage-' + suffix + '@example.test'
    baseline = sql('SELECT (SELECT count(*) FROM "AspNetUsers"), (SELECT count(*) FROM "UserProfiles"), (SELECT count(*) FROM "EmailVerificationChallenges");')
    request = {'birthDate': today.replace(year=today.year - 12).isoformat(), 'username': username,
        'displayName': 'WHO Smoke — İpek Öztürk', 'email': underage_email, 'password': password}
    status, body = call('/api/v1/auth/register', request)
    expect(status, 400, 'Under-13 rejection', body, 'AGE_NOT_ELIGIBLE')
    assert baseline == sql('SELECT (SELECT count(*) FROM "AspNetUsers"), (SELECT count(*) FROM "UserProfiles"), (SELECT count(*) FROM "EmailVerificationChallenges");'), 'Under-13 created data.'
    assert not mailbox(underage_email), 'Under-13 sent email.'
    print('Under-13 database counts unchanged; email count zero.')
    request['birthDate'] = '1990-05-20'
    sensitive.append(request['birthDate'])
    request['email'] = email
    status, pending = call('/api/v1/auth/register', request)
    expect(status, 202, 'Register')
    assert pending['emailDeliveryStatus'] == 'sent', 'Mailpit delivery did not succeed.'
    deadline = time.monotonic() + 10
    messages = mailbox(email)
    while not messages and time.monotonic() < deadline:
        time.sleep(0.2)
        messages = mailbox(email)
    assert len(messages) == 1, 'Expected one synthetic verification email.'
    status, message = http('GET', mailpit + '/api/v1/message/' + messages[0]['ID'])
    assert status == 200
    text = message.get('Text', '')
    matches = re.findall(r'(?<!\d)\d{6}(?!\d)', text)
    assert len(matches) == 1 and 'WHO?' in text and '10 minutes' in text and '10 dakika' in text
    code = matches[0]
    codes.append(code)
    assert password not in json.dumps(message)
    print('Mailpit: exactly one WHO? email, six-digit code and bilingual ten-minute expiry; code redacted.')
    status, verified = call('/api/v1/auth/verify-email', {'registrationId': pending['registrationId'], 'code': code})
    expect(status, 200, 'Verify-email')
    assert 'accessToken' not in verified and 'refreshToken' not in verified
    expect(*call('/api/v1/me', access=verified['onboardingToken'], method='GET')[:1], 403, 'Onboarding token rejected by /me')
    status, first = call('/api/v1/onboarding/privacy', {'accountVisibility': 'private'}, verified['onboardingToken'])
    expect(status, 200, 'Private onboarding')
    privacy = first['user']['privacy']
    assert privacy == {'accountVisibility': 'private', 'followPermission': 'requestRequired', 'searchable': True,
        'recommendationsEnabled': True, 'profilePollVisibility': 'everyone', 'socialListVisibility': 'everyone'}
    status, own = call('/api/v1/me', access=first['accessToken'], method='GET')
    expect(status, 200, '/me')
    assert own['id'] == first['user']['id']
    status, second = call('/api/v1/auth/login', {'email': email, 'password': password, 'device': {'platform': 'ios', 'deviceName': 'Synthetic second device', 'appVersion': 'dev'}})
    expect(status, 200, 'Login independent device')
    status, rotated = call('/api/v1/auth/refresh', {'refreshToken': first['refreshToken']})
    expect(status, 200, 'Refresh rotation')
    assert rotated['refreshToken'] != first['refreshToken']
    status, error = call('/api/v1/auth/refresh', {'refreshToken': first['refreshToken']})
    expect(status, 401, 'Old refresh reuse', error, 'REFRESH_TOKEN_REUSE_DETECTED')
    status, error = call('/api/v1/auth/refresh', {'refreshToken': rotated['refreshToken']})
    expect(status, 401, 'Suspicious replacement revoked', error, 'SESSION_REVOKED')
    status, _ = call('/api/v1/me', access=rotated['accessToken'], method='GET')
    expect(status, 403, 'Revoked session /me rejected immediately')
    status, second = call('/api/v1/auth/refresh', {'refreshToken': second['refreshToken']})
    expect(status, 200, 'Independent session unaffected')
    status, third = call('/api/v1/auth/login', {'email': email, 'password': password})
    expect(status, 200, 'Login third device')
    status, _ = call('/api/v1/auth/logout', access=second['accessToken'])
    expect(status, 204, 'Logout current device')
    status, error = call('/api/v1/auth/refresh', {'refreshToken': second['refreshToken']})
    expect(status, 401, 'Current session revoked', error, 'SESSION_REVOKED')
    status, third = call('/api/v1/auth/refresh', {'refreshToken': third['refreshToken']})
    expect(status, 200, 'Other session survives current logout')
    status, fourth = call('/api/v1/auth/login', {'email': email, 'password': password})
    expect(status, 200, 'Login fourth device')
    status, _ = call('/api/v1/auth/logout-all', access=third['accessToken'])
    expect(status, 204, 'Logout all')
    for pair in (third, fourth):
        status, error = call('/api/v1/auth/refresh', {'refreshToken': pair['refreshToken']})
        expect(status, 401, 'All sessions revoked', error, 'SESSION_REVOKED')
    user_id = str(uuid.UUID(first['user']['id']))
    assert sql('SELECT bool_and(length("CodeHash")=64) FROM "EmailVerificationChallenges" WHERE "UserId"=\'' + user_id + '\';') == 't'
    assert sql('SELECT bool_and(length(t."TokenHash")=64) FROM "SessionRefreshTokens" t JOIN "UserSessions" s ON t."SessionId"=s."Id" WHERE s."UserId"=\'' + user_id + '\';') == 't'
    print('Database: verification/refresh stored as 64-character hashes; no plaintext columns.')
    for path in (root / '.verification').rglob('*'):
        if not path.is_file():
            continue
        content = path.read_bytes()
        assert not any(value.encode() in content for value in sensitive), 'Sensitive value in verification output.'
        for value in codes:
            assert not re.search(rb'(?<!\d)' + value.encode() + rb'(?!\d)', content), 'Verification code in output.'
    print('Runtime credential/code/token/DOB scan of actual verification logs: clean.')
    print('Synthetic local development user retained: ' + email)
    print('Password and tokens were runtime-generated and are not retained by tooling.')

if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Auth smoke failed safely: ' + type(error).__name__)
        raise SystemExit(1)
