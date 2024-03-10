using System.Linq;

//var csvPath = args.Length > 0 ? args[0] : "CPU-2024-03-02 20_51_24.csv";
var csvPath = args.Length > 0 ? args[0] : "CPU Usage-2024_03_03__17_00_00__22.10.csv";

var aggregatePeriod = args.Length > 1 ? (args[1] != "*" ? TimeSpan.Parse(args[1]) : TimeSpan.FromSeconds(30)) : TimeSpan.FromSeconds(30);

var startPeriod = args.Length > 2 ? DateTime.Parse(args[2]) : default;

var endPeriod = args.Length > 3 ? DateTime.Parse(args[3]) : default;

var mode = args.Length > 4 ? (args[4] != "*" ? args[4] : "*") : "*";

var csvContent = File.ReadAllLines(csvPath);

var data = CsvHelper.ReadCsv(csvContent);

var aggregatedMetrics = data
    .FilterMetrics(startPeriod, endPeriod)
    .AggregateMetrics(aggregatePeriod, (items, key) => items.Average(gi => gi.Values[key]));

var normalizedMetrics = aggregatedMetrics.NormalizeMetrics(mode);



var asCsv = normalizedMetrics.WriteCsv();

var fileName = Path.GetFileNameWithoutExtension(csvPath);
var pathOnly = Directory.GetParent(fileName);

File.WriteAllText($"{fileName}.new.csv", asCsv);