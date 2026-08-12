# Initialize the build environment with the .NET SDK.
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Install C++ compilation tools required for Native AOT on Alpine Linux.
RUN apk add --no-cache clang build-base zlib-dev

# Copy project files and restore dependencies.
COPY ["DockerAgentWorker.csproj", "./"]
RUN dotnet restore "DockerAgentWorker.csproj"

# Copy the remaining source code and publish the native executable.
COPY . .
RUN dotnet publish "DockerAgentWorker.csproj" -c Release -r linux-musl-x64 -o /app/publish /p:DisableFullFramework=true

# Initialize the minimal runtime environment.
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine AS final
WORKDIR /app

# Copy the compiled native binary from the build environment.
COPY --from=build /app/publish/DockerAgentWorker .

# Set execution rights for the native binary.
RUN chmod +x /app/DockerAgentWorker

# Define the entrypoint binary.
ENTRYPOINT ["/app/DockerAgentWorker"]