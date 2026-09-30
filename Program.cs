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

// Extract volume removal preference from the OS. Default to false if not specified or invalid.
string? deleteEnv = Environment.GetEnvironmentVariable("REMOVE_VOLUMES");
bool removeVolumes = false;
if (deleteEnv != null && bool.TryParse(deleteEnv, out bool volumesResult))
{
    removeVolumes = volumesResult;
}

// Extract inactivity threshold in days from the OS. Default to 15 if not specified or invalid.
string? daysEnv = Environment.GetEnvironmentVariable("INACTIVITY_DAYS");
int inactivityDays = 15;
if (daysEnv != null && int.TryParse(daysEnv, out int daysResult) && daysResult > 0)
{
    inactivityDays = daysResult;
}

// Extract execution interval in hours from the OS. Default to 24.
string? intervalEnv = Environment.GetEnvironmentVariable("RUN_INTERVAL_HOURS");
int intervalHours = 24;
if (intervalEnv != null && int.TryParse(intervalEnv, out int intervalResult) && intervalResult > 0)
{
    intervalHours = intervalResult;
}

// Log the current configuration to the terminal at startup.
Console.WriteLine($"[CONFIG] Simulation Mode: {simulateOnly}");
Console.WriteLine($"[CONFIG] Inactivity Limit: {inactivityDays} days");
Console.WriteLine($"[CONFIG] Run Interval: {intervalHours} hours");
Console.WriteLine($"[CONFIG] Remove Volumes: {removeVolumes}\n");


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
    BaseAddress = new Uri("http://127.0.0.1"),
    Timeout = TimeSpan.FromSeconds(30)
};

// Create a cancellation token source to allow for graceful shutdowns.
using var cancellationTokenSource = new CancellationTokenSource();

Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) =>
{
    cancellationTokenSource.Cancel();
};

while (!cancellationTokenSource.Token.IsCancellationRequested)
{
    Console.WriteLine($"\n[EXECUTION] Starting container check at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC...");
    
    try
    {
        HttpResponseMessage? response = null;

        // Request a list of all Docker containers, including stopped containers, with a retry 
        // mechanism to handle potential Docker Engine startup delays or transient network issues.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                response = await client.GetAsync("containers/json?all=1", cancellationTokenSource.Token);
                if (response.IsSuccessStatusCode)
                {
                    break; // Exit the retry loop if the request is successful
                }
                Console.WriteLine($"[WARN] Docker Engine not ready (attempt {attempt}/3): {response.StatusCode}");
            }
            catch (Exception ex) when (attempt < 3 && !cancellationTokenSource.Token.IsCancellationRequested)
            {
                Console.WriteLine($"[WARN] Attempt {attempt}/3 failed: {ex.Message}. Retrying in 5 seconds...");   
            } 
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationTokenSource.Token);
        }

        // Process the response if the request is successful.
        if (response != null && response.IsSuccessStatusCode)
        {
            // Read Docker's response as a JSON string.
            await using var jsonStream = await response.Content.ReadAsStreamAsync();

            // Deserialize the JSON response using the compile-time generated context
            // for Native AOT and trimming compatibility.
            var containers = await JsonSerializer.DeserializeAsync(jsonStream, ContainerJsonContext.Default.ListContainerInfo) 
                            ?? new List<ContainerInfo>();

            // Calculate and display the display name, state, and inactivity time of each container.
            foreach (var container in containers)
            {
                if (container is null) continue; // Skip null container entries

                ContainerInspect? inspectContainer = null;

                // Inspect stopped containers to determine when they exited.
                if (container.State == "exited")
                {
                    // Request detailed state information for the stopped container.
                    var inspectResponse = await client.GetAsync($"containers/{container.DisplayName}/json", cancellationTokenSource.Token);

                    // Throw an exception if Docker returns an unsuccessful response.
                    try
                    {
                        inspectResponse.EnsureSuccessStatusCode();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Failed to inspect container {container.DisplayName}: {ex.Message}");
                        continue; // Skip to the next container if inspection fails
                    }

                    // Read the inspection response as a stream and deserialize it.
                    await using var inspectStream = await inspectResponse.Content.ReadAsStreamAsync();
                    inspectContainer = await JsonSerializer.DeserializeAsync(inspectStream, ContainerJsonContext.Default.ContainerInspect)
                                        ?? new ContainerInspect();

                    // Skip the container if the response could not be deserialized.
                    if (inspectContainer is null) continue;

                    if (inspectContainer.State.FinishedAt == DateTime.MinValue || inspectContainer.State.FinishedAt.Year <= 1)
                    {
                        // Set as null if the finished time is invalid
                        container.TimeSinceExit = null;
                    }
                    else
                    {
                        // Calculate how long ago the container exited.
                        container.TimeSinceExit = DateTime.UtcNow - inspectContainer.State.FinishedAt;
                    }

                }
                // Display the container Display Name, current state, and inactivity time.
                Console.WriteLine($"ID: {container.DisplayName} | State: {container.State} | " + 
                    $"Inactivity Time: {(container.TimeSinceExit.HasValue ? $"{container.TimeSinceExit.Value.TotalDays:F2} days" : "N/A")}");
            }

            Console.WriteLine();

            // Iterate through all deserialized containers to process stopped containers
            // that have been inactive for at least inactivityDays days.
            foreach (var container in containers)
            {
                // Target containers that are stopped and have at least inactivityDays days of inactivity.
                if (container != null &&
                    container.State == "exited" &&
                    container.TimeSinceExit.HasValue &&
                    container.TimeSinceExit >= TimeSpan.FromDays(inactivityDays))
                {

                    if (simulateOnly)
                    {
                        // Log the planned action without mutating the system state.
                        Console.WriteLine($"[SIMULATION] Would delete container {container.DisplayName}.");
                    }
                    else
                    {
                        // Log the deletion of the container to the terminal.
                        Console.WriteLine($"[EXECUTING] Deleting container {container.DisplayName}...");

                        // Send an HTTP DELETE request to the Docker Engine to delete the container and 
                        // its associated resources if removeVolumes is true. 
                        // Otherwise, delete only the container.
                        var deleteUrl = removeVolumes
                            ? $"containers/{container.DisplayName}?v=true"
                            : $"containers/{container.DisplayName}";

                        var deleteResponse = await client.DeleteAsync(deleteUrl, cancellationTokenSource.Token);
                        
                        try
                        {
                            deleteResponse.EnsureSuccessStatusCode();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] Failed to delete container {container.DisplayName}: {ex.Message}");
                            continue; // Skip to the next container if deletion fails
                        }

                        // Log the successful deletion.
                        Console.WriteLine($"[SUCCESS] Container {container.DisplayName} deleted.");
                    }
                }
            }
        }
        else
        {
            // Log the failure to retrieve the container list.
            Console.WriteLine($"[ERROR] Failed to retrieve container list after 3 attempts: {response?.StatusCode}.");
        }
        
    }
    catch (Exception ex)
    {
        // Fail-safe: catch execution errors so the agent can continue with the next cycle.
        Console.WriteLine($"[ERROR] Operation failed: {ex.Message}");
    }

    Console.WriteLine($"[SYSTEM] Container check complete. Waiting {intervalHours} hours until the next cycle.");
    
    // Wait asynchronously until the next execution cycle without blocking a thread
    // or until a shutdown signal is received.
    try
    {
        await Task.Delay(TimeSpan.FromHours(intervalHours), cancellationTokenSource.Token);
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine("[SYSTEM] Shutdown signal received. Exiting gracefully.");
        break;
    }
    
}


public class ContainerInfo
{
    public string Id {get; set; } = string.Empty;
    public string State {get; set; } = string.Empty;
    public List<string> Names {get; set; } = new();
    public string DisplayName => Names.Count > 0 ? Names[0].TrimStart('/'): Id;

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