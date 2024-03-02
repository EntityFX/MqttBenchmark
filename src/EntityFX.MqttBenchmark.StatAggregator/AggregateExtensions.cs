static class AggregateExtensions
{
    public static Dictionary<DateTime, DataLine> NormalizeMetrics(
        this Dictionary<DateTime, DataLine> items, string mode)
    {
        return (mode == "*" ? items.DetectMode() : mode) switch
        {
            "cpu" => items.ToCpuMetrics(),
            "bandwidth" => items.ToBandwidthMetrics(),
            _ => items,
        };
    }

    public static string DetectMode(
        this Dictionary<DateTime, DataLine> items)
    {
        var first = items.First();
        //"dpc","interrupt","privileged","user"
        if (first.Value.Values.Keys.Contains("dpc"))
        {
            return "cpu";
        }

        return "bandwidth";
    }

    public static Dictionary<DateTime, DataLine> ToBandwidthMetrics(
        this Dictionary<DateTime, DataLine> items)
    {
        return items.ToDictionary(c => c.Key,
            c => new DataLine()
            {
                DateTime = c.Key,
                Values = new Dictionary<string, decimal>()
                {
                    ["up"] = c.Value.Values["Sent- Upload"],
                    ["down"] = Math.Abs(c.Value.Values["Received- Download"]),
                }
            });
    }

    public static Dictionary<DateTime, DataLine> ToCpuMetrics(
        this Dictionary<DateTime, DataLine> items)
    {
        return items.ToDictionary(c => c.Key,
            c => new DataLine()
            {
                DateTime = c.Key,
                Values = new Dictionary<string, decimal>() 
                { 
                    ["load"] = c.Value.Values.Values.Sum() 
                }
            });
    }

    public static Dictionary<DateTime, DataLine> FilterMetrics(
        this Dictionary<DateTime, DataLine> items, DateTime? startPeriod, DateTime? endPeriod)
    {
        var itemsEn = items.AsEnumerable();
        if (startPeriod != null)
        {
            itemsEn.Where(p => p.Key >= startPeriod.Value);
        }

        if (endPeriod != null)
        {
            itemsEn.Where(p => p.Key <= endPeriod.Value);
        }
        
        return itemsEn.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);
    }

    public static Dictionary<DateTime, DataLine> AggregateMetrics(
        this Dictionary<DateTime, DataLine> items, TimeSpan period, Func<IEnumerable<DataLine>, string, decimal> aggregateFunc)
    {
        return items.GroupBy(g => RoundUp(g.Key, period))
        .ToDictionary(k => k.Key, g =>
        {
            var first = g.First();

            var result = new DataLine()
            {
                DateTime = g.Key
            };

            foreach (var value in first.Value.Values)
            {
                result.Values[value.Key] = aggregateFunc(g.AsEnumerable().Select(e => e.Value), value.Key);
            }

            return result;
        });
    }

    private static DateTime RoundUp(DateTime dt, TimeSpan d)
    {
        return new DateTime(((dt.Ticks + d.Ticks - 1) / d.Ticks) * d.Ticks);
    }
}
