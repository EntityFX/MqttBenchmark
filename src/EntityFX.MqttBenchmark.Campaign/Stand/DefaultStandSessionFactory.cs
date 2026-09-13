namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Реализация по умолчанию — делегирует <see cref="StandSession.StartAsync"/>.</summary>
public sealed class DefaultStandSessionFactory : IStandSessionFactory
{
    /// <inheritdoc cref="IStandSessionFactory.StartAsync"/>
    public Task<StandSession> StartAsync(string script, string inventoryPath, CampaignDefinition config,
        string broker, string output, string dockerExecutable = "docker", CancellationToken cancellationToken = default,
        string? expectedStandSha256 = null, string? trustedBuildProvenanceDirectory = null) =>
        StandSession.StartAsync(script, inventoryPath, config, broker, output, dockerExecutable, cancellationToken,
            expectedStandSha256, trustedBuildProvenanceDirectory);
}