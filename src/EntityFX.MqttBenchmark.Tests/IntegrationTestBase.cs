using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityFX.MqttBenchmark.Tests;

/// <summary>
/// Base class for <b>integration</b> tests that require external infrastructure (a Docker daemon and the
/// broker images, or a live MQTT broker). By default these tests are reported as <i>inconclusive</i>
/// (not failed), so the pure unit-test suite stays green in any environment. Set the
/// <see cref="EnableVariable"/> environment variable to a non-blank value to actually execute them.
/// </summary>
public abstract class IntegrationTestBase
{
    /// <summary>Environment variable that, when set to a non-blank value, enables integration tests.</summary>
    public const string EnableVariable = "MQB_ENABLE_INTEGRATION";

    [TestInitialize]
    public void EnsureIntegrationEnabled()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnableVariable)))
        {
            Assert.Inconclusive($"Integration tests are disabled by default (they require Docker + broker images). Set {EnableVariable}=1 to run them.");
        }
    }
}