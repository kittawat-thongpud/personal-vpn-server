"""Qualify NIC-level observations of synthetic lab sockets. Export counts only."""

import ipaddress
import json
import re
import struct
import subprocess
import sys
from pathlib import Path


def read_blocks(path):
    links = []
    with path.open('rb') as stream:
        while header := stream.read(8):
            kind, size = struct.unpack('<II', header)
            if size < 12 or size % 4 or size > 8 * 1024 * 1024:
                raise ValueError('Invalid Packet Monitor pcapng block length')
            data = stream.read(size - 8)
            if len(data) != size - 8 or struct.unpack_from('<I', data, len(data) - 4)[0] != size:
                raise ValueError('Truncated Packet Monitor pcapng block')
            if kind == 0x0A0D0D0A:
                if data[:4] != b'\x4d\x3c\x2b\x1a':
                    raise ValueError('Expected little-endian Packet Monitor output')
                links.clear()
            elif kind == 1:
                links.append(struct.unpack_from('<H', data)[0])
            elif kind == 6:
                interface, _, _, captured, _ = struct.unpack_from('<IIIII', data)
                if captured <= len(data) - 24:
                    yield links[interface], data[20:20 + captured]


def transport(link, packet):
    if link == 1:
        if len(packet) < 14:
            return None
        offset = 14
        ether_type = packet[12:14]
        while ether_type in (b'\x81\x00', b'\x88\xa8') and len(packet) >= offset + 4:
            ether_type = packet[offset + 2:offset + 4]
            offset += 4
        if ether_type not in (b'\x08\x00', b'\x86\xdd'):
            return None
        packet = packet[offset:]
    elif link in (0, 108):
        packet = packet[4:]
    elif link not in (101, 228, 229):
        return None
    if len(packet) < 20:
        return None
    if packet[0] >> 4 == 4:
        if int.from_bytes(packet[6:8], 'big') & 0x1fff:
            return None
        protocol, start = packet[9], 4 * (packet[0] & 15)
        source, destination = packet[12:16], packet[16:20]
    elif packet[0] >> 4 == 6 and len(packet) >= 40:
        protocol, start = packet[6], 40
        source, destination = packet[8:24], packet[24:40]
    else:
        return None
    if protocol not in (6, 17) or len(packet) < start + 8:
        return None
    source_port, destination_port = struct.unpack_from('!HH', packet, start)
    return (protocol, source, source_port, destination, destination_port), packet[start + 8:]


def component_rows(value):
    if isinstance(value, dict):
        if any(key.casefold() in ('id', 'componentid') for key in value):
            yield {key.casefold(): field for key, field in value.items() if isinstance(field, (str, int))}
        for field in value.values():
            yield from component_rows(field)
    elif isinstance(value, list):
        for field in value:
            yield from component_rows(field)


def adapter_id(text, components, nic):
    candidates = []
    aliases = {nic[key].casefold().strip('{}') for key in ('Id', 'Name', 'Description')}
    for component in components:
        identifier = str(component.get('id', component.get('componentid', '')))
        if not identifier.isdigit():
            continue
        if nic['index'] in (component.get('ifindex'), component.get('interfaceindex')):
            candidates.append((4, identifier))
        elif aliases.intersection(str(value).casefold().strip('{}') for value in component.values()):
            candidates.append((2, identifier))
    for match in re.finditer(r'^[ \t]*(\d+)[ \t]+(?:(\S{17}|-)[ \t]+)?(.+?)[ \t]*$', text, re.M):
        identifier, mac, name = match.groups()
        if mac and nic['mac'] and mac.replace('-', '').casefold() == nic['mac'].casefold():
            candidates.append((3, identifier))
        elif name.casefold().strip('{}') in aliases:
            candidates.append((2, identifier))
    best_rank = max((rank for rank, _ in candidates), default=0)
    best = {identifier for rank, identifier in candidates if rank == best_rank}
    if len(best) != 1:
        raise ValueError(f'Could not identify a unique Packet Monitor component for {nic["Description"]}')
    return best.pop()


def dns_name(name):
    labels = name.rstrip('.').lower().encode('ascii').split(b'.')
    return b''.join(bytes([len(label)]) + label for label in labels) + b'\0'


def qualify(directory):
    manifest = json.loads((directory / 'manifest.json').read_text())
    adapters = json.loads((directory / 'adapters.json').read_text())
    names = (directory / 'pktmon-list.txt').read_text(encoding='utf-8-sig').replace('\0', '')
    topology = (directory / 'pktmon-list.json').read_text(encoding='utf-8-sig').replace('\0', '')
    components = list(component_rows(json.loads(topology)))
    try:
        ids = {label: adapter_id(names, components, nic) for label, nic in adapters.items()}
    except ValueError as error:
        names = re.sub(r'(?i)(?:[0-9a-f]{2}-){5}[0-9a-f]{2}', '<MAC>', names)
        return dict(result='FAIL', reason=str(error), nicDescriptions={label: nic['Description'] for label, nic in adapters.items()},
                    adapterInventory=names.splitlines()[:25], componentFields=sorted({key for row in components for key in row})[:35])
    if len(set(ids.values())) != 2:
        raise ValueError('Physical and Wintun capture components must be separate')

    flows, counts = {}, {}
    for row in manifest['flows']:
        role = (row['Stage'], row['App'], row['Protocol'])
        address, port = row['Endpoint'].split(':')
        key = (6 if row['Protocol'] == 'tcp' else 17, ipaddress.ip_address(row['Local']).packed,
               row['Port'], ipaddress.ip_address(address).packed, int(port))
        if key in flows or role in counts:
            raise ValueError('Socket tuples in the capture are not unique')
        flows[key] = role
        counts[role] = dict.fromkeys(ids, 0)

    queries = {dns_name(row['Name']): row for row in manifest['dns']}
    dns_counts = {row['App']: dict.fromkeys(ids, 0) for row in queries.values()}
    for label, component in ids.items():
        pcap = directory / f'{label}.pcapng'
        subprocess.run(['pktmon.exe', 'etl2pcap', str(directory / 'probe.etl'), '--out', str(pcap),
                        '--component-id', component], check=True)
        for link, packet in read_blocks(pcap):
            if parsed := transport(link, packet):
                key, payload = parsed
                if role := flows.get(key):
                    counts[role][label] += 1
                if key[0] == 17 and key[-1] == 53 and payload[12:]:
                    for name, row in queries.items():
                        if payload[12:].lower().startswith(name):
                            dns_counts[row['App']][label] += 1

    results = []
    for stage in ('capture_without_exclusion', 'capture_with_reference_exclusion', 'capture_after_exclusion_removal'):
        for app in ('bypass', 'tunneled', 'blocked'):
            for protocol in ('tcp', 'udp'):
                seen = counts.get((stage, app, protocol), dict.fromkeys(ids, 0))
                direct = app == 'bypass' and stage == 'capture_with_reference_exclusion'
                valid = (seen['physical'] > 0 and seen['wintun'] == 0) if direct else (seen['wintun'] > 0 and seen['physical'] == 0)
                results.append(dict(stage=stage, app=app, protocol=protocol, packets=seen, passCheck=valid))
    dns_results = [dict(app=row['App'], windowsStatus=row['Status'], packets=dns_counts[row['App']]) for row in queries.values()]
    dns_observed = len(dns_results) == 3 and all(sum(row['packets'].values()) > 0 and row['windowsStatus'] in (0, 9003) for row in dns_results)
    return dict(result='PASS' if all(row['passCheck'] for row in results) and dns_observed else 'FAIL', flows=results, dns=dns_results)


if __name__ == '__main__':
    try:
        report = qualify(Path(sys.argv[1]))
    except (OSError, ValueError, KeyError, struct.error, subprocess.CalledProcessError) as error:
        report = dict(result='FAIL', reason=str(error))
    Path(sys.argv[2]).write_text(json.dumps(report, indent=2) + '\n')
    sys.exit(report['result'] != 'PASS')
