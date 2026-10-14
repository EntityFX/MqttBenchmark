"""Owned transient user service; environment stays in memory over user D-Bus."""
import json, os, sys
import dbus
payload=json.load(sys.stdin)
bus=dbus.bus.BusConnection('unix:path=/run/user/%d/bus'%os.getuid())
manager=dbus.Interface(bus.get_object('org.freedesktop.systemd1','/org/freedesktop/systemd1'),'org.freedesktop.systemd1.Manager')
properties=[('Description',dbus.String('MqttBenchmark owned native broker')),('Type',dbus.String('exec')),
            ('CollectMode',dbus.String('inactive-or-failed')),('WorkingDirectory',dbus.String(payload['workingDirectory'])),
            ('StandardOutputFileToAppend',dbus.String(payload['stdout'])),('StandardErrorFileToAppend',dbus.String(payload['stderr'])),
            ('Environment',dbus.Array([key+'='+value for key,value in os.environ.items()],signature='s')),
            ('ExecStart',dbus.Array([dbus.Struct((dbus.String(payload['executable']),dbus.Array([payload['executable']]+payload['args'],signature='s'),dbus.Boolean(False)))],signature='(sasb)')),
            ('MemoryMax',dbus.UInt64(payload['memoryLimit']))]
if payload.get('cpu') is not None:
    cpu=int(payload['cpu']);mask=bytearray(cpu//8+1);mask[cpu//8]=1<<(cpu%8)
    properties.append(('CPUAffinity',dbus.ByteArray(mask)))
manager.StartTransientUnit(payload['unit'],'fail',properties,[],signature='ssa(sv)a(sa(sv))')
