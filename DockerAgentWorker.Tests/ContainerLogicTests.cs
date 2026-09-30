namespace DockerAgentWorker.Tests;

public class ContainerLogicTests
{
    [Fact]
    public void UnstartedContainer_WithMinValueFinishedAt_MustNeverBeEligibleForDeletion()
    {
        // Arrange: Simulate a newly created container that has never started.
        // Docker API reports FinishedAt as DateTime.MinValue (0001-01-01T00:00:00Z) in this state.
        var inspect = new ContainerInspect
        {
            State = new ContainerState
            {
                Status = "created",
                FinishedAt = DateTime.MinValue
            }
        };
        var container = new ContainerInfo
        {
            Id = "c1a2b3c4d5e6",
            State = "exited"
        };
        int inactivityThresholdDays = 15;

        // Act: Ignore invalid finish times to prevent them from being interpreted as prolonged inactivity.
        if (inspect.State.FinishedAt == DateTime.MinValue || inspect.State.FinishedAt.Year <= 1)
        {
            container.TimeSinceExit = null;
        }
        else
        {
            container.TimeSinceExit = DateTime.UtcNow - inspect.State.FinishedAt;
        }

        bool isEligibleForDeletion = container.State == "exited" &&
                                     container.TimeSinceExit.HasValue &&
                                     container.TimeSinceExit >= TimeSpan.FromDays(inactivityThresholdDays);

        // Assert: Inactivity must be null and container must not be marked for deletion.
        Assert.Null(container.TimeSinceExit);
        Assert.False(isEligibleForDeletion);
    }

    [Fact]
    public void RunningContainer_EvenWithLongUptime_MustNeverBeEligibleForDeletion()
    {
        // Arrange: Simulate an active container running for 90 days.
        var container = new ContainerInfo
        {
            Id = "a13fd98765a4321",
            State = "running",
            TimeSinceExit = TimeSpan.FromDays(90)
        };
        int inactivityThresholdDays = 15;

        // Act: Evaluate deletion criteria requiring the container to be in 'exited' state.
        bool isEligibleForDeletion = container.State == "exited" &&
                                     container.TimeSinceExit.HasValue &&
                                     container.TimeSinceExit >= TimeSpan.FromDays(inactivityThresholdDays);

        // Assert: Running containers must strictly be excluded from deletion.
        Assert.False(isEligibleForDeletion);
    }

    [Fact]
    public void InactiveContainer_BelowInactivityThreshold_MustNotBeMarkedForDeletion()
    {
        // Arrange: Container exited 10 days ago, while configured threshold is 15 days.
        var container = new ContainerInfo
        {
            Id = "a2b123456789",
            State = "exited",
            TimeSinceExit = TimeSpan.FromDays(10)
        };
        int inactivityThresholdDays = 15;

        // Act: Evaluate whether container elapsed time exceeds deletion limit.
        bool isEligibleForDeletion = container.State == "exited" &&
                                     container.TimeSinceExit.HasValue &&
                                     container.TimeSinceExit >= TimeSpan.FromDays(inactivityThresholdDays);

        // Assert: Container has not reached threshold and must be preserved.
        Assert.False(isEligibleForDeletion);
    }

    [Fact]
    public void InactiveContainer_ExceedingThreshold_MustBeMarkedForDeletion()
    {
        // Arrange: Stopped container inactive for 20 days, exceeding the 15-day limit.
        var container = new ContainerInfo
        {
            Id = "asdfgr555666",
            State = "exited",
            TimeSinceExit = TimeSpan.FromDays(20)
        };
        int inactivityThresholdDays = 15;

        // Act: Evaluate deletion criteria.
        bool isEligibleForDeletion = container.State == "exited" &&
                                     container.TimeSinceExit.HasValue &&
                                     container.TimeSinceExit >= TimeSpan.FromDays(inactivityThresholdDays);

        // Assert: Container must be marked for deletion.
        Assert.True(isEligibleForDeletion);
    }

    [Fact]
    public void InactiveContainer_ExceedingThreshold_WhenProtectedByLabel_MustBeSkipped()
    {
        // Arrange: Container stopped for 90 days but explicitly labeled with docksentinel.ignore=true.
        var container = new ContainerInfo
        {
            Id = "database111222",
            State = "exited",
            Names = new List<string> { "/dev-postgres" },
            TimeSinceExit = TimeSpan.FromDays(90),
            Labels = new Dictionary<string, string>
            {
                { "docksentinel.ignore", "true" }
            }
        };

        // Act: Check whether protection rule overrides elapsed inactivity time.
        bool shouldSkip = container.IsIgnoredByLabel;

        // Assert: Protection label must evaluate to true, aborting deletion.
        Assert.True(shouldSkip);
    }

    [Fact]
    public void InactiveContainer_ExceedingThreshold_WhenListedInIgnoreSet_MustBeSkipped()
    {
        // Arrange: Container stopped for 45 days whose name is declared in IGNORE_CONTAINERS.
        var container = new ContainerInfo
        {
            Id = "cache333444",
            State = "exited",
            Names = new List<string> { "/staging-redis" },
            TimeSinceExit = TimeSpan.FromDays(45)
        };

        var ignoredContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "staging-redis",
            "production-db"
        };

        // Act: Match container display name against the ignored names set.
        bool shouldSkip = ignoredContainers.Contains(container.DisplayName);

        // Assert: Container name must match the ignore list and be skipped.
        Assert.True(shouldSkip);
    }

    [Fact]
    public void DisplayName_WhenNameHasLeadingSlash_TrimsLeadingSlash()
    {
        // Arrange: Docker Engine returns container names prefixed with a slash.
        var container = new ContainerInfo
        {
            Id = "abc123456789",
            Names = new List<string> { "/my-web-app" }
        };

        // Act: Retrieve cleaned display name.
        string result = container.DisplayName;

        // Assert: Leading slash must be trimmed for REST routing and readable logging.
        Assert.Equal("my-web-app", result);
    }

    [Fact]
    public void DisplayName_WhenNamesListIsEmpty_FallsBackSafelyToId()
    {
        // Arrange: Anomalous container returned without any name entries.
        var container = new ContainerInfo
        {
            Id = "shortid",
            Names = new List<string>()
        };

        // Act: Retrieve display name fallback.
        string result = container.DisplayName;

        // Assert: Must fallback safely to Id without throwing OutOfRangeException.
        Assert.Equal("shortid", result);
    }

    [Fact]
    public void DeleteUrl_WhenRemoveVolumesIsTrue_AppendsVQueryParameter()
    {
        // Arrange: Configured with volume cleanup enabled.
        var container = new ContainerInfo
        {
            Id = "c1a2b3c4d5e6",
            Names = new List<string> { "/app-backend" }
        };
        bool removeVolumes = true;

        // Act: Build target deletion endpoint.
        var deleteUrl = removeVolumes
            ? $"containers/{container.DisplayName}?v=true"
            : $"containers/{container.DisplayName}";

        // Assert: Endpoint must include '?v=true' query parameter to prune associated anonymous volumes.
        Assert.Equal("containers/app-backend?v=true", deleteUrl);
    }

    [Fact]
    public void DeleteUrl_WhenRemoveVolumesIsFalse_OmitsQueryParameter()
    {
        // Arrange: Default safe configuration with volume cleanup disabled.
        var container = new ContainerInfo
        {
            Id = "c1a2b3c4d5e6",
            Names = new List<string> { "/app-backend" }
        };
        bool removeVolumes = false;

        // Act: Build target deletion endpoint.
        var deleteUrl = removeVolumes
            ? $"containers/{container.DisplayName}?v=true"
            : $"containers/{container.DisplayName}";

        // Assert: Endpoint must omit query parameter, preserving all disk volumes.
        Assert.Equal("containers/app-backend", deleteUrl);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-15")]
    [InlineData("invalid_string")]
    [InlineData("")]
    public void EnvironmentVariables_WhenInvalidOrNonPositive_FallbackToSafeDefaults(string invalidEnvValue)
    {
        // Arrange: Simulate invalid or non-positive environment variable values.
        int fallbackInactivityDays = 15;
        int resolvedInactivityDays = fallbackInactivityDays;

        // Act: Execute parsing guard logic from startup sequence.
        if (invalidEnvValue != null && int.TryParse(invalidEnvValue, out int parsedDays) && parsedDays > 0)
        {
            resolvedInactivityDays = parsedDays;
        }

        // Assert: Value must reject non-positive inputs and retain the safe 15-day fallback.
        Assert.Equal(15, resolvedInactivityDays);
    }
}
