namespace EntityFX.MqttBenchmark.Calibration;

public sealed record MetricStatistics(
    double Mean,
    double StandardDeviation,
    double Ci95HalfWidth,
    double Ci95Lower,
    double Ci95Upper,
    double Min,
    double Max,
    int Count)
{
    private static readonly double[] StudentTCritical95 =
    {
        0, 12.7062047364, 4.30265272975, 3.18244630528, 2.7764451052,
        2.57058183564, 2.44691184879, 2.36462425101, 2.30600413503,
        2.26215716285, 2.22813885196, 2.20098516008, 2.17881282966,
        2.16036865646, 2.14478668792, 2.13144954556, 2.11990529922,
        2.10981557783, 2.10092204024, 2.09302405441, 2.08596344727,
        2.07961384473, 2.0738730679, 2.06865761042, 2.06389856163,
        2.05953855275, 2.05552943864, 2.05183051648, 2.0484071418,
        2.04522964213, 2.0422724563
    };

    public static MetricStatistics Calculate(IEnumerable<double> values)
    {
        var data = values.ToArray();
        if (data.Length == 0 || data.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Statistics require finite, non-empty input.");
        var mean = data.Average();
        var deviation = data.Length == 1
            ? 0
            : Math.Sqrt(data.Sum(value => Math.Pow(value - mean, 2)) / (data.Length - 1));
        var critical = data.Length == 1 ? 0 : Critical(data.Length - 1);
        var halfWidth = critical * deviation / Math.Sqrt(data.Length);
        return new MetricStatistics(mean, deviation, halfWidth, mean - halfWidth,
            mean + halfWidth, data.Min(), data.Max(), data.Length);
    }

    private static double Critical(int degreesOfFreedom) =>
        degreesOfFreedom < StudentTCritical95.Length
            ? StudentTCritical95[degreesOfFreedom]
            : degreesOfFreedom <= 40 ? 2.02107539031
            : degreesOfFreedom <= 60 ? 2.00029782106
            : degreesOfFreedom <= 120 ? 1.97993040505
            : 1.95996398454;
}
