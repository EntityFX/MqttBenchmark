using System.Globalization;
using System.Text;

static class CsvHelper
{
    public static Dictionary<DateTime, DataLine> ReadCsv(this string[] content, char separator = ',')
    {
        var result = new Dictionary<DateTime, DataLine>();

        var header = content[0];
        var headerItems = header.Split(separator).Select(h => h.Replace("\"", "")).ToArray();

        for (int i = 1; i < content.Length; i++)
        {
            var line = content[i];
            var lineItem = line.Split(separator);

            var dataLine = new DataLine();

            for (int j = 0; j < lineItem.Length; j++)
            {
                var headerName = headerItems[j];

                if ("time" == headerName.ToLowerInvariant())
                {
                    dataLine.DateTime = DateTime.Parse(lineItem[j]);
                    continue;
                }

                dataLine.Values[headerName] = decimal.Parse(lineItem[j]);
            }

            result[dataLine.DateTime] = dataLine;
        }

        return result;
    }

    public static string WriteCsv(this Dictionary<DateTime, DataLine> items, char separator = ',')
    {
        var sb = new StringBuilder();

        if (items?.Any() != true)
        {
            return string.Empty;
        }

        var first = items.FirstOrDefault();
        sb.Append("date_time");

        foreach (var value in first.Value.Values)
        {
            sb.Append(separator);
            sb.Append(value.Key);
        }
        sb.AppendLine();

        foreach (var item in items)
        {
            sb.Append(item.Key.ToString("s", CultureInfo.InvariantCulture));

            foreach (var value in item.Value.Values)
            {
                sb.Append(separator);
                sb.Append(value.Value);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
