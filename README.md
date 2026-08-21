# DockSentinel

DockSentinel is a lightweight, autonomous C# agent designed to monitor and remove inactive Docker containers.

Instead of running as a one-off script, the agent executes in a continuous loop in the background. It communicates directly with the Docker Engine through its Unix socket (`/var/run/docker.sock`), evaluates the inactivity time of stopped containers, and automatically deletes those that exceed the configured threshold to release disk space.

## Key Features

- **Autonomous Execution:** Runs continuously in the background, waiting asynchronously between execution cycles without blocking a thread.
- **Declarative Configuration:** Rules and timers are injected via OS environment variables without needing to recompile the code.
- **Small Size (Native AOT):** Compiled directly into a native Linux binary. The final Docker image is less than 30MB, requiring no .NET SDK or runtime.
- **Direct Socket Connection:** Sends HTTP requests directly through the Docker Unix socket, avoiding standard network ports.

## Configuration

The agent's behavior is controlled by environment variables. A template is provided in the repository.

1. Create your local environment file:

   ```bash
   cp .env.example .env
   ```

2. Adjust the variables in the `.env` file:

   - `SIMULATION_MODE`: Set to `true` to only log actions, or `false` to actually delete containers.
   - `INACTIVITY_DAYS`: Number of days a container must be stopped before deletion.
   - `RUN_INTERVAL_HOURS`: How often the agent starts a new execution cycle.

## Build and Run

Deployment is managed via Docker Compose.

Start the agent in the background (detached mode):

```bash
docker compose up -d --build
```

To view the agent's execution logs and verify its current state:

```bash
docker compose logs -f
```