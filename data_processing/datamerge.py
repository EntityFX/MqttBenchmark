import pandas as pd
import numpy as np
import json

cpu_usage_files = [
    "cpu.nb.m256.2024-03-13T03_07_03__10.114.22.10.json",
    "cpu.nb.m256.2024-03-13T03_07_03__10.114.22.50.json",
]

result_files = [
    "results.nb.m256.2024-03-13T03_07_03.csv",
]

def load_data_frame(files_list: list):
    df_list = []
    
    for filename in files_list:
        df = pd.read_csv(filename, index_col=None, header=0)
        df_list.append(df)

    return pd.concat(df_list, axis=0, ignore_index=True)

def load_json_data_frame(files_list: list):
    result_df = pd.DataFrame()
    
    for filename in files_list:
        f = open(filename)
        data = json.load(f)
        df = pd.DataFrame()
        column_names = []

        for metric in data["data"]["result"]:
            metric_name = metric["metric"]["mode"]
            metric_values = metric["values"]

            if "Time" not in df:
                # convert timestamp from seconds to nanoseconds
                df["Time"] = list(map(lambda x: pd.Timestamp(x[0]*1000000000), metric_values))

            df[metric_name] = list(map(lambda x: float(x[1]), metric_values))
            column_names.append(metric_name)

        if "Time" in result_df:
            result_df = result_df.merge(df,how='outer',on=["Time"],suffixes=('', '_right'))
            for col in column_names:
                result_df[col] = np.max(result_df[[col, col +'_right']], axis=1)
                result_df.drop(labels=[col +'_right'], axis=1, inplace=True)
        else:
            result_df = df

    return result_df

#load cpu usage data
cpu_df = load_json_data_frame(cpu_usage_files)
cpu_df["Time"] = pd.to_datetime(cpu_df["Time"])
cpu_df["usage"] = (cpu_df["dpc"] + cpu_df["interrupt"] + cpu_df["privileged"] + cpu_df["user"])/100

#load benchmark result
result_df = load_data_frame(result_files)
result_df["test_init_end_time"] = pd.to_datetime(result_df["test_init_end_time"])
result_df["test_clean_start_time"] = pd.to_datetime(result_df["test_clean_start_time"])
result_df["duration"] = pd.to_timedelta(result_df["duration"])
result_df["fail"] = (result_df["ok"] - result_df["received"])/result_df["ok"]

# correct date time
offset = 17
cpu_df["Time"] = cpu_df["Time"] - pd.DateOffset(seconds=offset)

#merge cpu usage to benchmark result
cpu_usage = []
time_overlap = 3

for index, row in result_df.iterrows():
    row_cpu_df = cpu_df[
        (cpu_df["Time"] > row["test_init_end_time"] + pd.DateOffset(seconds=time_overlap)) & 
        (cpu_df["Time"] < row["test_clean_start_time"] - pd.DateOffset(seconds=time_overlap))
        ]
    cpu_usage.append(row_cpu_df["usage"].mean())

result_df['cpu_usage'] = cpu_usage

df = result_df[["params_qos", "scenario", "params_clients", "rps", "fail", "cpu_usage", "99_percent"]]
print(df.head())

pivot_columns = ["params_clients", "rps", "fail", "cpu_usage", "99_percent"] 
brokers = ["mosq", "active", "aedes", "emqx"]
qos_levels = [0,1,2]

brokers_dict = dict()

for qos in qos_levels:
    result_df = pd.DataFrame()
    for broker in brokers:
        broker_df = df[(df["params_qos"] == qos) & (df["scenario"].str.contains(broker))]
        broker_pivot_df = pd.pivot_table(broker_df[pivot_columns], index=["params_clients"])
        brokers_dict[broker] = broker_pivot_df
        
    for col in pivot_columns[1:]:
        for broker in brokers:
            result_df[broker + "_" + col] = brokers_dict[broker][col]

    result_df.to_csv(f"q_{qos}.csv")