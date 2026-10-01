#!/usr/bin/env python3
"""Run a guarded Rental Command release on the private hosted workbox."""

import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path("/srv/hosted")
STACK = ROOT / "rental"
CONFIG = STACK / "compose.migration.json"
SECRETS = STACK / "release-secrets.json"
RELEASE_COMPOSE = STACK / "compose.release.yml"
UNIT = "hosted-stack@rental.service"
TAG_RE = re.compile(r"[0-9a-f]{7,40}")
ACTOR_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}")
SERVICES = ("api", "engine", "web")
SECRET_KEYS = (
    "Jwt__SecretKey",
    "ConnectionStrings__MigratorConnection",
    "ConnectionStrings__DefaultConnection",
    "ConnectionStrings__EngineConnection",
)
REGISTRY = "ghcr.io/jcoble/rentalcommand"
TIMEOUT = 150


def fail(message):
    raise RuntimeError(message)


def run(command, *, env=None, input_text=None):
    try:
        return subprocess.run(command, env=env, input=input_text, text=True,
                              capture_output=True, check=True).stdout
    except subprocess.CalledProcessError as error:
        fail(f"command failed: {' '.join(command)} (exit {error.returncode})")


def read_request(argv, stream):
    if len(argv) != 2 or not TAG_RE.fullmatch(argv[1]):
        fail("invalid deploy command")
    payload = stream.read()
    lines = payload.split("\n")
    if len(lines) != 3 or lines[2] or "\r" in payload:
        fail("authentication payload must be exactly two lines")
    actor, token = lines[:2]
    if not ACTOR_RE.fullmatch(actor):
        fail("invalid GitHub actor")
    if not token:
        fail("invalid GitHub token")
    return argv[1], actor, token


def load_json(path, expected_keys=None):
    stat = path.stat()
    if stat.st_uid != 0 or stat.st_mode & 0o777 != 0o600:
        fail(f"{path} must be root-owned mode 600")
    value = json.loads(path.read_text())
    if not isinstance(value, dict) or (expected_keys and set(value) != set(expected_keys)):
        fail(f"invalid {path.name}")
    if expected_keys and any(not isinstance(value[key], str) or not value[key] for key in expected_keys):
        fail(f"invalid {path.name}")
    return value


def wait_for(expected, postgres=False):
    deadline = time.monotonic() + TIMEOUT
    while time.monotonic() < deadline:
        output = run(["docker", "compose", "-f", str(CONFIG), "ps", "--format", "json"])
        state = {
            item.get("Service"): item
            for item in (json.loads(line) for line in output.splitlines() if line)
        }
        if postgres:
            if state.get("postgres", {}).get("Health", "").lower() == "healthy":
                return
        else:
            active = subprocess.run(["systemctl", "is-active", "--quiet", UNIT],
                                    capture_output=True).returncode == 0
            if active and all(state.get(name, {}).get("State", "").lower() == "running"
                              and state[name].get("Health", "").lower() in ("", "healthy")
                              for name in expected):
                return
        time.sleep(5)
    fail("PostgreSQL health check timed out" if postgres else "container health check timed out")


def deploy(tag, actor, token):
    if not ROOT.is_mount() or not STACK.is_dir():
        fail("/srv/hosted mount or Rental stack is unavailable")
    with (STACK / ".deploy.lock").open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if subprocess.run(["systemctl", "is-active", "--quiet", UNIT], capture_output=True).returncode:
            fail(f"{UNIT} is not active")
        current = load_json(CONFIG)
        secrets = load_json(SECRETS, SECRET_KEYS)
        updated = json.loads(json.dumps(current))
        services = updated.get("services")
        if not isinstance(services, dict):
            fail("compose config has no services object")
        for service in SERVICES:
            if not isinstance(services.get(service), dict) or "image" not in services[service]:
                fail(f"compose config is missing services.{service}.image")
            services[service]["image"] = f"{REGISTRY}-{service}:{tag}"
        for service, key in (("api", "ConnectionStrings__DefaultConnection"),
                             ("engine", "ConnectionStrings__EngineConnection")):
            environment = services[service].get("environment")
            if not isinstance(environment, dict) or "ConnectionStrings__DefaultConnection" not in environment:
                fail(f"compose config is missing {service} default connection")
            environment["ConnectionStrings__DefaultConnection"] = secrets[key]
        expected = tuple(updated["services"])
        docker_config = tempfile.mkdtemp(prefix="rental-deploy-", dir="/run")
        try:
            run(["docker", "--config", docker_config, "login", "ghcr.io", "-u", actor,
                 "--password-stdin"], input_text=token)
            for service in SERVICES:
                run(["docker", "--config", docker_config, "pull", f"{REGISTRY}-{service}:{tag}"])
        finally:
            shutil.rmtree(docker_config, ignore_errors=True)
        fd, temporary_name = tempfile.mkstemp(prefix=".compose.migration.json.", dir=STACK)
        prepared = Path(temporary_name)
        with os.fdopen(fd, "w") as stream:
            json.dump(updated, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        try:
            run(["docker", "compose", "-f", str(prepared), "config", "--quiet"])
            run(["systemctl", "stop", UNIT])
            try:
                run(["docker", "compose", "-f", str(CONFIG), "up", "-d", "--no-deps", "postgres"])
                wait_for(("postgres",), postgres=True)
                env = os.environ.copy()
                env.update(secrets)
                env["IMAGE_TAG"] = tag
                run(["docker", "compose", "-p", "hosted-rental-release", "-f",
                     str(RELEASE_COMPOSE), "run", "--rm", "--no-deps", "migrate"], env=env)
            finally:
                run(["docker", "compose", "-f", str(CONFIG), "stop", "postgres"])
            stamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
            backup = CONFIG.with_name(f"{CONFIG.name}.{stamp}.bak")
            backup_fd = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with CONFIG.open("rb") as source, os.fdopen(backup_fd, "wb") as target:
                shutil.copyfileobj(source, target)
            os.replace(prepared, CONFIG)
        finally:
            prepared.unlink(missing_ok=True)
        run(["systemctl", "start", UNIT])
        wait_for(expected)


def main():
    tag, actor, token = read_request(sys.argv, sys.stdin)
    deploy(tag, actor, token)
    print(f"deployed rental {tag}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as error:
        print(f"rental-hosted-deploy: {error}", file=sys.stderr)
        sys.exit(1)
