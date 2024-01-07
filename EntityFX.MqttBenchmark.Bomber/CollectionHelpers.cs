using System.Collections;

static class CollectionHelpers
{
    public static IEnumerable<T[]> Product<T>(T[][] items)
    {
        int length = items.Length;
        int[] indexes = new int[length];

        while (true)
        {
            T[] arr = new T[length];
            for (int i = 0; i < length; i++)
            {
                arr[i] = items[i][indexes[i]];
            }
            yield return arr;

            int row = length - 1;
            indexes[row]++;
            while (indexes[row] == items[row].Length)
            {
                if (row == 0)
                    yield break;
                indexes[row] = 0;
                row--;
                indexes[row]++;
            }
        }
    }

    public static IEnumerable Cartesian(this IEnumerable<IEnumerable> items)
    {
        var slots = items
           // initialize enumerators
           .Select(x => x.GetEnumerator())
           // get only those that could start in case there is an empty collection
           .Where(x => x.MoveNext())
           .ToArray();

        while (true)
        {
            // yield current values
            yield return slots.Select(x => x.Current);

            // increase enumerators
            foreach (var slot in slots)
            {
                // reset the slot if it couldn't move next
                if (!slot.MoveNext())
                {
                    // stop when the last enumerator resets
                    if (slot == slots.Last()) { yield break; }
                    slot.Reset();
                    slot.MoveNext();
                    // move to the next enumerator if this reseted
                    continue;
                }
                // we could increase the current enumerator without reset so stop here
                break;
            }
        }
    }
}

//(string Result, Dictionary<string, object> OutParams)[] ReplaceParamsCombinations(
//    string replaceString, Dictionary<string, object[]> testParams, out Dictionary<string, object> outParams)
//{
//    outParams = new Dictionary<string, object>();
//    foreach (var testParam in testParams)
//    {
//        foreach (var testParamValue in testParam.Value)
//        {
//            if (replaceString.Contains($"{{{testParam.Key}}}"))
//            {
//                outParams.Add(testParam.Key, testParamValue);
//            }
//            replaceString = replaceString.Replace($"{{{testParam.Key}}}", testParamValue.ToString());
//        }
//    }

//    return replaceString;
//}

//Dictionary<string, ScenarioSettings[]> GetScenarioSettings(Dictionary<string, ScenarioNameTemplate> scenarioNameTemplates, Dictionary<string, object[]> testParams)
//{
//    var scenarioSettings = new Dictionary<string, ScenarioSettings[]>();
//    foreach (var scenarioGroup in scenarioNameTemplates)
//    {
//        var scenarioGroupName = scenarioGroup.Key;
//        scenarioGroupName = ReplaceParams(scenarioGroupName, testParams, out _);

//        var scenario = ReplaceParams(scenarioGroup.Value, testParams, out var outParams);
//        return new ScenarioSettings(scenario, outParams);

//        scenarioSettings[scenarioGroupName] = scenarioGroup.SelectMany(s =>
//        {
//            var scenario = ReplaceParams(s, testParams, out var outParams);
//            return new ScenarioSettings(scenario, outParams);
//        }).ToArray();

//    }
//    return scenarioSettings;
//}