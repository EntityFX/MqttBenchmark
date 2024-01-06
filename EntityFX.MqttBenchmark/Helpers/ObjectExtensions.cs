namespace EntityFX.MqttBenchmark.Helpers;

static class ObjectExtensions
{
    public static T OverrideValues<T>(this T target, T source)
    {
        Type t = typeof(T);

        var properties = t.GetProperties().Where(prop => prop.CanRead && prop.CanWrite);

        foreach (var prop in properties)
        {
            var value = prop.GetValue(source, null);
            if (value != null)
                prop.SetValue(target, value, null);
        }

        return target;
    }
}