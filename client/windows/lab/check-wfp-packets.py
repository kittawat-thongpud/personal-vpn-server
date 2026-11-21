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


def adapters_from(directory):
    tun = json.loads((directory / 'wintun.json').read_text())
    names = (directory / 'pktmon-list.txt').read_text(encoding='utf-8-sig').replace('\0', '')
    rows = dict(re.findall(r'^[ \t]*(\d+)[ \t]+(?:(?:\S{17}|-)[ \t]+)?(.+?)[ \t]*$', names, re.M))
    aliases = {tun[key].casefold().strip('{}') for key in ('Id', 'Name', 'Description')}
    candidates = [component for component, name in rows.items() if name.casefold().strip('{}') in aliases]
    if len(candidates) != 1 or len(rows) < 2:
        raise ValueError(f'Could not identify Wintun and at least one other NIC: {list(rows.values())}')
    return rows.keys(), candidates[0]


def dns_name(name):
    labels = name.rstrip('.').lower().encode('ascii').split(b'.')
    return b''.join(bytes([len(label)]) + label for label in labels) + b'\0'


def qualify(directory):
    manifest = json.loads((directory / 'manifest.json').read_text())
    components, wintun = adapters_from(directory)

    flows, counts = {}, {}
    for row in manifest['flows']:
        role = (row['Stage'], row['App'], row['Protocol'])
        address, port = row['Endpoint'].split(':')
        key = (6 if row['Protocol'] == 'tcp' else 17, ipaddress.ip_address(row['Local']).packed,
               row['Port'], ipaddress.ip_address(address).packed, int(port))
        if key in flows or role in counts:
            raise ValueError('Socket tuples in the capture are not unique')
        flows[key] = role
        counts[role] = dict.fromkeys(components, 0)

    queries = {dns_name(row['Name']): row for row in manifest['dns']}
    dns_counts = {name: dict.fromkeys(components, 0) for name in queries}
    for component in components:
        pcap = directory / f'nic-{component}.pcapng'
        subprocess.run(['pktmon.exe', 'etl2pcap', str(directory / 'probe.etl'), '--out', str(pcap),
                        '--component-id', component], check=True)
        for link, packet in read_blocks(pcap):
            if parsed := transport(link, packet):
                key, payload = parsed
                if role := flows.get(key):
                    counts[role][component] += 1
                if key[0] == 17 and key[-1] == 53 and len(payload) > 12 and not (payload[2] & 0x80):
                    for name in queries:
                        if payload[12:].lower().startswith(name):
                            dns_counts[name][component] += 1

    physical = {}
    for protocol in ('tcp', 'udp'):
        baseline = counts.get(('after_disconnect', 'bypass', protocol), {})
        physical[protocol] = {component for component, count in baseline.items() if count and component != wintun}
        if not physical[protocol] or baseline.get(wintun):
            raise ValueError(f'No independent physical capture baseline for {protocol} after removing Wintun routes')

    def categorize(observed, physical_components):
        return dict(physical=sum(observed.get(component, 0) for component in physical_components), wintun=observed.get(wintun, 0),
                    other=sum(count for component, count in observed.items() if component not in physical_components and component != wintun))

    results = []
    for stage in ('capture_without_exclusion', 'capture_with_reference_exclusion', 'capture_after_exclusion_removal'):
        for app in ('bypass', 'tunneled', 'blocked'):
            for protocol in ('tcp', 'udp'):
                seen = categorize(counts.get((stage, app, protocol), {}), physical[protocol])
                direct = app == 'bypass' and stage == 'capture_with_reference_exclusion'
                valid = seen['other'] == 0 and ((seen['physical'] > 0 and seen['wintun'] == 0) if direct else (seen['wintun'] > 0 and seen['physical'] == 0))
                results.append(dict(stage=stage, app=app, protocol=protocol, packets=seen, passCheck=valid))
    dns_results = []
    for name, row in queries.items():
        negative = row['Name'].startswith('pvn-')
        seen = categorize(dns_counts[name], physical['tcp'] | physical['udp'])
        valid = sum(seen.values()) > 0 and (row['Status'] in (9003, 9501) if negative else row['Status'] == 0)
        dns_results.append(dict(app=row['App'], query='negative' if negative else 'positive', windowsStatus=row['Status'], packets=seen, passCheck=valid))
    passed = all(row['passCheck'] for row in results + dns_results) and len(dns_results) == 6
    return dict(result='PASS' if passed else 'FAIL', verifiedAfterDisconnect=True, flows=results, dns=dns_results)


if __name__ == '__main__':
    try:
        report = qualify(Path(sys.argv[1]))
    except (OSError, ValueError, KeyError, struct.error, subprocess.CalledProcessError) as error:
        report = dict(result='FAIL', reason=str(error))
    Path(sys.argv[2]).write_text(json.dumps(report, indent=2) + '\n')
    sys.exit(report['result'] != 'PASS')
