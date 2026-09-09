# MQTTBENCHMARK_TELEMETRY_V1
set -eu
samples=1
read origin ignored < /proc/uptime
sample=0
encode() { base64 | tr -d '\n'; }
while [ "$sample" -lt "$samples" ]; do
    read now ignored < /proc/uptime
    delay=$(awk -v start="$origin" -v n="$sample" -v now="$now" 'BEGIN { d=start+n-now; printf "%.3f", (d>0?d:0) }')
    sleep "$delay"
    read monotonic ignored < /proc/uptime
    timestamp=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
    cpu=$(encode < /sys/fs/cgroup/cpu.stat)
    memory=$(cat /sys/fs/cgroup/memory.current)
    io=$(encode < /sys/fs/cgroup/io.stat)
    network=$(encode < /proc/net/dev)
    connections=$(cat /proc/net/tcp /proc/net/tcp6 | encode)
    rss=0
    pids=''
    for process in /proc/[0-9]*; do
        [ -r "$process/comm" ] || continue
        read name < "$process/comm" || continue
        case "$name" in
            node|java|mosquitto|beam.smp)
                value=$(awk '/^VmRSS:/ { printf "%.0f", $2 * 1024 }' "$process/status")
                rss=$((rss + ${value:-0}))
                pids="$pids ${process##*/}"
                ;;
        esac
    done
    printf '{"timestamp":"%s","monotonicSeconds":%s,"processRssBytes":%s,"processIds":"%s","cgroupMemoryCurrentBytes":%s,"cgroupCpuAndThrottleBase64":"%s","diskIoBase64":"%s","networkBase64":"%s","connectionsBase64":"%s"}\n' "$timestamp" "$monotonic" "$rss" "$pids" "$memory" "$cpu" "$io" "$network" "$connections"
    sample=$((sample + 1))
done
