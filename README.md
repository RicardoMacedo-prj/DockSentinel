# DockSentinel

[![Build and Publish Docker Image](https://github.com/RicardoMacedo-prj/DockSentinel/actions/workflows/build.yml/badge.svg)](https://github.com/RicardoMacedo-prj/DockSentinel/actions/workflows/build.yml)
![Docker Image Size](https://img.shields.io/badge/image%20size-%3C%2025%20MB-blue)
![RAM Usage](https://img.shields.io/badge/RAM%20usage-~3--8%20MB-brightgreen)
![Platform](https://img.shields.io/badge/platform-linux--x64-lightgrey)
![.NET Version](https://img.shields.io/badge/.NET-8.0%20Native%20AOT-purple)

**DockSentinel** is a lightweight background service that periodically checks your Docker host and removes containers that have been stopped for longer than a configured period. It connects to Docker through `/var/run/docker.sock` and can optionally remove anonymous volumes associated with the containers it deletes.

---

## Why DockSentinel?

Over time, stopped containers take up disk space. Managing them with built-in Docker commands or custom scripts has common trade-offs:

* **Docker’s until filter uses container creation time:** `docker container prune --filter until=24h` can match a container that was created months ago but stopped only five minutes ago.
* **System prune has a broad scope:** `docker system prune -a` also removes unused images, networks, and build cache alongside stopped containers. Volumes are not removed by default.
* **DockSentinel focuses on stopped duration:** It inspects when containers actually exited and only cleans up those that have been stopped longer than your configured limit. It also supports exclusions via labels or container names, and runs in dry-run mode by default so you can inspect actions before enabling them.

---

## Key Features

* **Low Resource Usage (.NET 8 Native AOT):** Built as a standalone Linux binary. The Docker image is **under 25 MB** and typically uses **3–8 MB of RAM**.
* **Dry-Run by Default:** Runs with `SIMULATION_MODE=true` by default, logging planned actions without deleting containers until explicitly enabled.
* **Cleanup Based on Stopped Time:** Cleans up containers based on how long they have been stopped, rather than when they were created.
* **Exclude Containers from Cleanup:** Protect specific containers using Docker labels (`docksentinel.ignore=true`) or environment variables (`IGNORE_CONTAINERS`).
* **Optional Anonymous Volume Cleanup:** Automatically removes anonymous volumes tied to deleted containers (`REMOVE_VOLUMES=true`). Named volumes and host bind mounts are never deleted.
* **Direct Docker Socket:** Connects directly to `/var/run/docker.sock`. No network ports are opened.
* **Resilient to Docker Restarts:** Retries the Docker socket connection on startup, applies 30-second request timeouts, and handles `SIGTERM` and `SIGINT` for graceful shutdown.
* **Runs as a Non-Root User:** Runs as an unprivileged user inside the container (`$APP_UID`), reducing application process privileges. However, access to `/var/run/docker.sock` grants control over the Docker daemon, so only deploy trusted images.

---

## Quickstart

DockSentinel is published to the GitHub Container Registry. You can run it immediately using Docker or Docker Compose.

### Option A: Docker CLI

Start the container in the background (runs safely in simulation mode):

```bash
docker run -d \
  --name docksentinel \
  --restart unless-stopped \
  --group-add $(stat -c '%g' /var/run/docker.sock) \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e SIMULATION_MODE=true \
  -e INACTIVITY_DAYS=15 \
  -e RUN_INTERVAL_HOURS=24 \
  -e REMOVE_VOLUMES=false \
  ghcr.io/ricardomacedo-prj/docksentinel-agent:latest
```

### Option B: Docker Compose

Clone the repository and start the container:

```bash
git clone https://github.com/RicardoMacedo-prj/DockSentinel.git
cd DockSentinel
docker compose up -d
```

### View Logs

Inspect what DockSentinel is evaluating:

```bash
docker logs -f docksentinel
# Or with Docker Compose:
# docker compose logs -f
```

Sample output:
```text
[CONFIG] Simulation Mode: true
[CONFIG] Inactivity Limit: 15 days
[CONFIG] Run Interval: 24 hours
[CONFIG] Remove Volumes: false
[CONFIG] Ignored Containers: my_db, redis

[EXECUTION] Starting container check at 2026-09-30 17:00:00 UTC...
ID: web-frontend | State: running | Inactivity Time: N/A
ID: test-worker  | State: exited  | Inactivity Time: 18.25 days
ID: my_db        | State: exited  | Inactivity Time: 45.10 days

[INFO] Skipping ignored container my_db.
[SIMULATION] Would delete container test-worker.
[SYSTEM] Container check complete. Waiting 24 hours until the next cycle.
```

Once you review the logs and verify the planned deletions, set `SIMULATION_MODE=false` in your environment to allow DockSentinel to delete containers.

---

## Configuration

Configure DockSentinel using environment variables:

| Variable | Default | Description |
| :--- | :---: | :--- |
| `SIMULATION_MODE` | `true` | Dry-run mode. When `true`, actions are logged without deleting containers. Set to `false` to enable active deletion. |
| `INACTIVITY_DAYS` | `15` | Number of days a container must be stopped before it is eligible for cleanup. |
| `RUN_INTERVAL_HOURS` | `24` | Interval in hours between container evaluation cycles. |
| `REMOVE_VOLUMES` | `false` | When `true`, deletes anonymous volumes tied to deleted containers. Named volumes and host bind mounts are never deleted. |
| `IGNORE_CONTAINERS` | *(empty)* | Comma-separated list of container names to exclude from cleanup (case-insensitive, e.g. `postgres, redis`). |

---

## Excluding Containers from Cleanup

You can exclude specific containers from cleanup using two methods:

### 1. Docker Labels

Add this label to the service definition in your Compose file:

```yaml
services:
  database:
    image: postgres:16
    labels:
      - "docksentinel.ignore=true"
```

DockSentinel skips any container with this label, regardless of how long it has been stopped.

### 2. Container Names

List container names in the `IGNORE_CONTAINERS` environment variable:

```env
IGNORE_CONTAINERS=production_db, test_cache, old_registry
```

Container names are matched case-insensitively, and leading slashes (e.g. `/production_db`) are stripped automatically.

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.