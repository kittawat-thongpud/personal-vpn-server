"""Qualify NIC-level observations of synthetic lab sockets. Export counts only."""

import ipaddress
import json
import re
import struct
import subprocess
import sys
from collections import Counter
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
        if ether_type in (b'\x08\x00', b'\x86\xdd'):
            packet = packet[offset:]
        # Packet Monitor can label the Wintun NDIS IP payload as Ethernet.
        # Validate the IPv4/IPv6 header below before using a raw frame.
    elif link in (0, 108):
        packet = packet[4:]
    elif link not in (101, 228, 229):
        return None
    if len(packet) < 20:
        return None
    if packet[0] >> 4 == 4:
        protocol, start = packet[9], 4 * (packet[0] & 15)
        if start < 20 or int.from_bytes(packet[2:4], 'big') < start + 8 or int.from_bytes(packet[6:8], 'big') & 0x1fff:
            return None
        source, destination = packet[12:16], packet[16:20]
    elif packet[0] >> 4 == 6 and len(packet) >= 40:
        protocol, start = packet[6], 40
        source, destination = packet[8:24], packet[24:40]
    else:
        return None
    if protocol not in (6, 17) or len(packet) < start + 8:
        return None
    source_port, destination_port = struct.unpack_from('!HH', packet, start)
    payload_start = start + 8
    if protocol == 6 and len(packet) >= start + 20:
        payload_start = start + 4 * (packet[start + 12] >> 4)
    return (protocol, source, source_port, destination, destination_port), packet[payload_start:]


def adapters_from(directory):
    tun = json.loads((directory / 'wintun.json').read_text())
    names = (directory / 'pktmon-list.txt').read_text(encoding='utf-8-sig').replace('\0', '')
    rows = dict(re.findall(r'^[ \t]*(\d+)[ \t]+(?:(?:\S{17}|-)[ \t]+)?(.+?)[ \t]*$', names, re.M))
    aliases = {tun[key].casefold().strip('{}') for key in ('Id', 'Name', 'Description')}
    candidates = [component for component, name in rows.items() if name.casefold().strip('{}') in aliases]
    if len(candidates) != 1 or len(rows) < 2:
        raise ValueError(f'Could not identify Wintun and at least one other NIC: {list(rows.values())}')
    return candidates[0]


def dns_name(name):
    labels = name.rstrip('.').lower().encode('ascii').split(b'.')
    return b''.join(bytes([len(label)]) + label for label in labels) + b'\0'


def dns_event_counts(directory, queries):
    nic = json.loads((directory / 'wintun.json').read_text())
    events = json.loads((directory / 'dns-events.json').read_text(encoding='utf-8-sig'))
    names = {row['Name'].casefold().rstrip('.'): key for key, row in queries.items()}
    routes = {name: {phase: Counter() for phase in ('queryInterface', 'send', 'reply')} for name in queries}
    for event in events:
        query = names.get(event['QueryName'].casefold().rstrip('.'))
        if not query:
            continue
        index = event.get('InterfaceIndex', '')
        name = event.get('AdapterName', '').casefold().strip('{}')
        local = event.get('LocalAddress', '').split('%')[0]
        server = event.get('DnsServerIpAddress', event.get('DNSServerAddress', '')).split('%')[0]
        phase = {3010: 'send', 3011: 'reply'}.get(event['event'], 'queryInterface')
        if (index.isdecimal() and int(index) in nic['indexes']) or local in nic['addresses'] or name == nic['Id'].casefold().strip('{}') or server in nic['servers']:
            routes[query][phase]['wintun'] += 1
        elif server in nic['otherServers'] or (index.isdecimal() and int(index) > 0) or (local and local not in ('0.0.0.0', '::')):
            routes[query][phase]['otherNics'] += 1
        else:
            routes[query][phase]['unassigned'] += 1
    return routes


def qualify(directory):
    manifest = json.loads((directory / 'manifest.json').read_text())
    wintun = adapters_from(directory)

    flows, counts, stun_tokens = {}, {}, {}
    for row in manifest['flows']:
        role = (row['Stage'], row['App'], row['Protocol'])
        address, port = row['Endpoint'].split(':')
        key = (6 if row['Protocol'] == 'tcp' else 17, ipaddress.ip_address(row['Local']).packed,
               row['Port'], ipaddress.ip_address(address).packed, int(port))
        reverse = (key[0], key[3], key[4], key[1], key[2])
        if key in flows or reverse in flows:
            raise ValueError('Socket tuples in the capture are not unique')
        flows[key] = role
        flows[reverse] = role
        # The local VPN server retransmits a tunneled STUN payload using a
        # different socket on the same VM. Use tokens only for direct probes.
        direct = row['App'] == 'bypass' and row['Stage'] in ('capture_with_reference_exclusion', 'after_disconnect')
        if direct and row.get('WireToken'):
            stun_tokens[bytes.fromhex(row['WireToken'])] = role
        counts.setdefault(role, Counter())

    queries = {dns_name(row['Name']): row for row in manifest['dns']}
    dns_counts = {name: Counter() for name in queries}
    capture_stats = {}
    for label, selector in (('allNics', []), ('wintun', ['--component-id', wintun])):
        pcap = directory / f'{label}.pcapng'
        subprocess.run(['pktmon.exe', 'etl2pcap', str(directory / 'probe.etl'), '--out', str(pcap), *selector], check=True)
        frames, decoded = Counter(), Counter()
        for link, packet in read_blocks(pcap):
            frames[link] += 1
            if parsed := transport(link, packet):
                key, payload = parsed
                decoded[key[0]] += 1
                role = flows.get(key)
                if key[0] == 17 and len(payload) >= 20 and payload[4:8] == b'\x21\x12\xa4\x42':
                    role = stun_tokens.get(payload[8:20], role)
                if role:
                    counts[role][label] += 1
                if key[0] == 6:
                    payload = payload[2:]  # DNS over TCP has a two-byte length prefix.
                if key[-1] == 53 and len(payload) > 12 and not (payload[2] & 0x80):
                    for name in queries:
                        if payload[12:].lower().startswith(name):
                            dns_counts[name][label] += 1
        capture_stats[label] = dict(linkTypes=frames, decodedProtocols=decoded, matchingFlows=sum(row[label] > 0 for row in counts.values()))

    def categorize(observed):
        total, tun = observed.get('allNics', 0), observed.get('wintun', 0)
        if tun > total:
            raise ValueError('Wintun observations are not a subset of the NIC capture')
        return dict(wintun=tun, otherNics=total - tun)

    baseline = {protocol: categorize(counts.get(('after_disconnect', 'bypass', protocol), {})) for protocol in ('tcp', 'udp')}
    baseline_passed = all(seen['wintun'] == 0 and seen['otherNics'] > 0 for seen in baseline.values())

    results = []
    for stage in ('capture_without_exclusion', 'capture_with_reference_exclusion', 'capture_after_exclusion_removal'):
        for app in ('bypass', 'tunneled', 'blocked'):
            for protocol in ('tcp', 'udp'):
                seen = categorize(counts.get((stage, app, protocol), {}))
                direct = app == 'bypass' and stage == 'capture_with_reference_exclusion'
                valid = (seen['otherNics'] > 0 and seen['wintun'] == 0) if direct else (seen['wintun'] > 0 and seen['otherNics'] == 0)
                results.append(dict(stage=stage, app=app, protocol=protocol, packets=seen, passCheck=valid))
    dns_results = []
    dns_events = dns_event_counts(directory, queries)
    for name, row in queries.items():
        negative = row['Name'].startswith('pvn-')
        seen = categorize(dns_counts[name])
        events = dict(dns_events[name])
        observed = sum(seen.values()) > 0 or any(path.get('wintun', 0) > 0 or path.get('otherNics', 0) > 0 for path in events.values())
        valid = observed and (row['Status'] in (9003, 9501) if negative else row['Status'] == 0)
        dns_results.append(dict(app=row['App'], query='negative' if negative else 'positive', windowsStatus=row['Status'], packets=seen, windowsDnsEvents=events, passCheck=valid))
    passed = baseline_passed and all(row['passCheck'] for row in results + dns_results) and len(dns_results) == 6
    return dict(result='PASS' if passed else 'FAIL', capture=capture_stats, afterDisconnect=baseline, verifiedAfterDisconnect=baseline_passed, flows=results, dns=dns_results)


if __name__ == '__main__':
    try:
        report = qualify(Path(sys.argv[1]))
    except (OSError, ValueError, KeyError, struct.error, subprocess.CalledProcessError) as error:
        report = dict(result='FAIL', reason=str(error))
    Path(sys.argv[2]).write_text(json.dumps(report, indent=2) + '\n')
    sys.exit(report['result'] != 'PASS')
