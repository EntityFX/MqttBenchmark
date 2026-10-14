"""One-time setup of an authenticated, controller-only share on the evidence disk.

Run remotely with sudo. Read the new dedicated SMB account password from stdin;
never accept it as a command argument or print it. Existing accounts are preserved.
"""
import pathlib, subprocess, sys

mount = pathlib.Path('/run/media/entityfx/hd250')
target = subprocess.check_output(['findmnt', '-n', '-o', 'TARGET', '-T', str(mount)], text=True).strip()
if target != str(mount):
    raise RuntimeError('Evidence disk is not mounted')
root = mount / 'mqtty-evidence'
root.mkdir(exist_ok=True)
subprocess.run(['chown', 'entityfx:entityfx', str(root)], check=True)
user = 'mqttybench'
accounts = subprocess.check_output(['pdbedit', '-L'], text=True)
if any(line.startswith(user + ':') for line in accounts.splitlines()):
    raise RuntimeError('Dedicated account already exists; preserve its password and credentials')
if subprocess.run(['id', user], capture_output=True).returncode:
    subprocess.run(['useradd', '-r', '-M', '-s', '/sbin/nologin', user], check=True)
password = sys.stdin.readline().strip()
if len(password) < 24:
    raise RuntimeError('A random dedicated password is required')
subprocess.run(['smbpasswd', '-s', '-a', user], input=password + '\n' + password + '\n', text=True, check=True, stdout=subprocess.DEVNULL)
config = pathlib.Path('/etc/samba/usershares.conf')
text = '''\n[mqtty-evidence]
    path = /run/media/entityfx/hd250/mqtty-evidence
    read only = no
    browseable = no
    guest ok = no
    valid users = mqttybench
    force user = entityfx
    hosts allow = 192.168.5.246 127.0.0.1
    hosts deny = ALL
    create mask = 0600
    directory mask = 0700
'''
if config.exists():
    old = config.read_text()
    if '[mqtty-evidence]' in old:
        raise RuntimeError('Share already exists; do not overwrite it')
    config.write_text(old + text)
else:
    config.write_text(text)
subprocess.run(['testparm', '-s'], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
subprocess.run(['smbcontrol', 'all', 'reload-config'], check=True)
print('Dedicated evidence share prepared; existing services remain running.')
