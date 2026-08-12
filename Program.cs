using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

// Set the execution mode. True prevents actual deletion.
bool simulateOnly = true;

// Set the path to the Unix socket used to communicate with Docker.
var socketPath = "/var/run/docker.sock";

// Create an endpoint using the Docker socket path.
var endpoint = new UnixDomainSocketEndPoint(socketPath);

// Create an HTTP handler with a custom connection method.
var handler = new SocketsHttpHandler
{
    // Create a Unix socket and connects it to the Docker socket.
    ConnectCallback = async (context, token) =>
    {
        // Create a Unix stream socket.
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        
        // Connect the socket to Docker.
        await socket.ConnectAsync(endpoint, token);

        // Convert the socket into a stream that HttpClient can use.
        return new NetworkStream(socket, ownsSocket: true);
    }
};

// Create an HttpClient that uses the custom Docker socket connection.
var client = new HttpClient(handler);

// Set the base HTTP address used when making requests.
client.BaseAddress = new Uri("http://localhost");

// Request a list of all Docker containers, including stopped containers.
var response = await client.GetAsync("containers/json?all=1");

// Throw an exception if Docker returns an unsuccessful response.
response.EnsureSuccessStatusCode();

// Read Docker's response as a JSON string.
var rawJson = await response.Content.ReadAsStringAsync();

// Parse the JSON string into a structured list of ContainerInfo objects
// using the compile-time generated context to guarantee AOT compatibility and prevent trimming.
var containers = JsonSerializer.Deserialize(rawJson, ContainerJsonContext.Default.ListContainerInfo) 
                 ?? new List<ContainerInfo>();

// Iterate through the list of deserialized containers.
foreach (var container in containers)
{
    // Print the first 12 characters of the ID and the container state to the console.
    Console.WriteLine($"ID: {container.Id.Substring(0, 12)} | State: {container.State}");

    // Target containers that are stopped.
    if (container.State == "exited")
    {
        if (simulateOnly)
        {
            // Log the planned action without mutating the system state.
            Console.WriteLine($"[SIMULATION] Would delete container {container.Id.Substring(0, 12)}.");
        }
        else
        {
            // Log the imminent destruction of the container to the terminal.
            Console.WriteLine($"[EXECUTING] Deleting container {container.Id.Substring(0, 12)}...");

            // Send an HTTP DELETE request to the Docker Engine to delete the container.
            var deleteResponse = await client.DeleteAsync($"containers/{container.Id}");

            // Throw an exception if the Docker Engine returns an unsuccessful status code.
            deleteResponse.EnsureSuccessStatusCode();

            // Log the successful destruction.
            Console.WriteLine($"[SUCCESS] Container {container.Id.Substring(0, 12)} deleted.");
        }
    }
}

public class ContainerInfo
{
    [JsonPropertyName("Id")]
    public string Id {get; set; } = string.Empty;

    [JsonPropertyName("State")]
    public string State {get; set; } = string.Empty;
}

// Instructs the compiler to generate AOT-safe JSON serialization code for the specified type during build time.
[JsonSerializable(typeof(List<ContainerInfo>))]
internal partial class ContainerJsonContext : JsonSerializerContext
{
}