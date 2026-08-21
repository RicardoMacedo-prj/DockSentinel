using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

// Extract execution mode from the OS. Default to true (safe) if not specified or invalid.
string? simEnv = Environment.GetEnvironmentVariable("SIMULATION_MODE");
bool simulateOnly = true; 
if (simEnv != null && bool.TryParse(simEnv, out bool simResult))
{
    simulateOnly = simResult;
}

// Extract inactivity threshold in days from the OS. Default to 15 if not specified or invalid.
string? daysEnv = Environment.GetEnvironmentVariable("INACTIVITY_DAYS");
int inactivityDays = 15;
if (daysEnv != null && int.TryParse(daysEnv, out int daysResult))
{
    inactivityDays = daysResult;
}

// Extract execution interval in hours from the OS. Default to 24.
string? intervalEnv = Environment.GetEnvironmentVariable("RUN_INTERVAL_HOURS");
int intervalHours = 24;
if (intervalEnv != null && int.TryParse(intervalEnv, out int intervalResult))
{
    intervalHours = intervalResult;
}

// Log the current configuration to the terminal at startup.
Console.WriteLine($"[CONFIG] Simulation Mode: {simulateOnly}");
Console.WriteLine($"[CONFIG] Inactivity Limit: {inactivityDays} days");
Console.WriteLine($"[CONFIG] Run Interval: {intervalHours} hours\n");


// Set the path to the Unix socket used to communicate with Docker.
var socketPath = "/var/run/docker.sock";

// Prevent execution if the socket file is missing from the container's filesystem.
if (!File.Exists(socketPath))
{
    Console.WriteLine($"[FATAL] The Docker socket file does not exist at '{socketPath}'.");
    Console.WriteLine("Ensure the container is executed with the mount flag: -v /var/run/docker.sock:/var/run/docker.sock");
    return;
}

// Create an endpoint using the Docker socket path.
var endpoint = new UnixDomainSocketEndPoint(socketPath);

// Create an HTTP handler with a custom connection method.
var handler = new SocketsHttpHandler
{
    // Disable HTTP proxy usage for direct communication with the Docker Engine.
    UseProxy = false,
    
    // Create a Unix socket and connect it to the Docker Engine.
    ConnectCallback = async (context, token) =>
    {
        // Create a Unix stream socket.
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        
        // Connect the socket to Docker Engine.
        await socket.ConnectAsync(endpoint, token);

        // Convert the socket into a stream that HttpClient can use.
        return new NetworkStream(socket, ownsSocket: true);
    }
};

// Create an HttpClient using the custom Docker socket connection
// and set the base address for Docker Engine API requests.
var client = new HttpClient(handler)
{
    BaseAddress = new Uri("http://127.0.0.1")
};

while (true)
{
    Console.WriteLine($"\n[EXECUTION] Starting container check at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC...");
    
    try
    {
        // Request a list of all Docker containers, including stopped containers.
        var response = await client.GetAsync("containers/json?all=1");

        // Throw an exception if Docker returns an unsuccessful response.
        response.EnsureSuccessStatusCode();

        // Read Docker's response as a JSON string.
        var rawJson = await response.Content.ReadAsStringAsync();

        // Deserialize the JSON response using the compile-time generated context
        // for Native AOT and trimming compatibility.
        var containers = JsonSerializer.Deserialize(rawJson, ContainerJsonContext.Default.ListContainerInfo) 
                        ?? new List<ContainerInfo>();

        // Calculate and display the ID, state, and inactivity time of each container.
        foreach (var container in containers)
        {
            ContainerInspect? inspectContainer = null;

            // Inspect stopped containers to determine when they exited.
            if (container.State == "exited")
            {
                // Request detailed state information for the stopped container.
                var inspectResponse = await client.GetAsync($"containers/{container.Id}/json");

                // Throw an exception if Docker returns an unsuccessful response.
                inspectResponse.EnsureSuccessStatusCode();

                // Read Docker's response as a JSON string.
                var inspectJson = await inspectResponse.Content.ReadAsStringAsync();

                // Parse the JSON string into a structured ContainerState object
                inspectContainer = JsonSerializer.Deserialize(inspectJson, ContainerJsonContext.Default.ContainerInspect);

                // Skip the container if the response could not be deserialized.
                if (inspectContainer is null) continue;

                // Calculate how long ago the container exited.
                container.TimeSinceExit = DateTime.UtcNow - inspectContainer.State.FinishedAt;

            }
            // Display the container ID (first 12 characters), current state, and inactivity time.
            Console.WriteLine($"ID: {container.Id.Substring(0, 12)} | State: {container.State} | Inactivity Time: {container.TimeSinceExit}");
        }

        Console.WriteLine();

        // Iterate through all deserialized containers to process stopped containers
        // that have been inactive for at least inactivityDays days.
        foreach (var container in containers)
        {
            // Target containers that are stopped and have at least inactivityDays days of inactivity.
            if (container.State == "exited" &&
                container.TimeSinceExit.HasValue &&
                container.TimeSinceExit >= TimeSpan.FromDays(inactivityDays))
            {
                if (simulateOnly)
                {
                    // Log the planned action without mutating the system state.
                    Console.WriteLine($"[SIMULATION] Would delete container {container.Id.Substring(0, 12)}.");
                }
                else
                {
                    // Log the deletion of the container to the terminal.
                    Console.WriteLine($"[EXECUTING] Deleting container {container.Id.Substring(0, 12)}...");

                    // Send an HTTP DELETE request to the Docker Engine to delete the container.
                    var deleteResponse = await client.DeleteAsync($"containers/{container.Id}");

                    // Throw an exception if the Docker Engine returns an unsuccessful status code.
                    deleteResponse.EnsureSuccessStatusCode();

                    // Log the successful deletion.
                    Console.WriteLine($"[SUCCESS] Container {container.Id.Substring(0, 12)} deleted.");
                }
            }
        }
    }
    catch (Exception ex)
    {
        // Fail-safe: catch execution errors so the agent can continue with the next cycle.
        Console.WriteLine($"[ERROR] Operation failed: {ex.Message}");
    }

    Console.WriteLine($"[SYSTEM] Container check complete. Waiting {intervalHours} hours until the next cycle.");
    
    // Wait asynchronously until the next execution cycle without blocking a thread.
    await Task.Delay(TimeSpan.FromHours(intervalHours));
}


public class ContainerInfo
{
    public string Id {get; set; } = string.Empty;
    public string State {get; set; } = string.Empty;

    [JsonIgnore]
    public TimeSpan? TimeSinceExit { get; set; }
}

public class ContainerInspect
{
    public ContainerState State { get; set; } = new();
}

public class ContainerState
{
    public string Status { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
}

// Instructs the compiler to generate AOT-safe JSON serialization code for the specified type during build time.
[JsonSerializable(typeof(List<ContainerInfo>))]
[JsonSerializable(typeof(ContainerInspect))]
internal partial class ContainerJsonContext : JsonSerializerContext
{
}