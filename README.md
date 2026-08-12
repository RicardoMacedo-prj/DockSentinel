# DockSentinel

DockSentinel is a C# tool designed to remove stopped Docker containers.

Instead of using standard terminal commands, this program communicates directly with the Docker Engine through its physical file (the Unix Domain Socket located at `/var/run/docker.sock`). 

The tool reads the state of all containers, finds the ones that are stopped (state: "exited"), and deletes them to release disk space.

## Key Features

* **Small Size (Native AOT):** The program is compiled directly into a native Linux binary. The final Docker image is less than 20MB.
* **No .NET Runtime Needed:** The container does not require the large .NET SDK or runtime to execute.
* **Direct Socket Connection:** It sends HTTP requests directly into the `.sock` file, bypassing standard network ports.
* **Safe JSON Reading:** Uses C# Source Generation to extract only the needed data (ID and State) without causing native compilation errors.

## Build and Run

1. Build the Docker image:
`docker build -t docksentinel-agent .`

2. Run the container. You must map your machine's Docker socket to the container:
`docker run --rm -v /var/run/docker.sock:/var/run/docker.sock docksentinel-agent`