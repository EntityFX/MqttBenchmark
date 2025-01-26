namespace EntityFX.MqttBenchmark.Helper;

static class StatisticsExtensions
{
    public static double StandardDeviation(this IEnumerable<double> items)
    {
        var arrayItems = items?.ToArray() ?? Array.Empty<double>();
        if (arrayItems.Any() != true)
        {
            return 0;
        }

        var average = arrayItems.Average();
        var squareDiffSum = arrayItems.Select(m => Math.Pow(m - average, 2)).Sum();
        return Math.Sqrt(squareDiffSum / arrayItems.Count());
    }
}