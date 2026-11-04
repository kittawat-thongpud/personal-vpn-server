import base64
import json
from pathlib import Path
import tempfile
import time
import unittest
from server import Store, ApiError, MAX_ROOMS, MAX_ROOM_BYTES


class Rooms(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        path = Path(self.temp.name)
        path.joinpath('server.crt').write_text('-----BEGIN CERTIFICATE-----\nQUJD\n-----END CERTIFICATE-----\n')
        self.store = Store(path, 'https://127.0.0.1:9443')
        self.owner = self.enroll('owner')
        self.guest = self.enroll()
        self.third = self.enroll()

    def tearDown(self):
        self.store.db.close()
        self.temp.cleanup()

    def enroll(self, role='member', invite=None):
        with self.store.lock:
            invite = invite or self.store.invitation(role)
            data = json.loads(base64.urlsafe_b64decode(invite[5:] + '=' * (-len(invite[5:]) % 4)))
            return self.store.enroll({'ticket': data['ticket']})

    def call(self, method, path, who=None, data=None):
        return self.store.dispatch(method, path, (who or self.owner)['token'], data or {}, '127.0.0.1')

    def test_authenticated_two_member_room_and_correct_poll_cursor(self):
        room = self.call('POST', '/v1/rooms')
        code = room['code']
        self.assertEqual(len(code), 8)
        self.call('POST', f'/v1/rooms/{code}/join', self.guest)
        self.call('POST', f'/v1/rooms/{code}/messages', data={'kind': 'retry'})
        result = self.call('GET', f'/v1/rooms/{code}/poll?after=0', self.guest)
        self.assertTrue(result['joined'])
        self.assertEqual(result['messages'][0]['data'], {'kind': 'retry'})
        self.assertGreater(result['cursor'], 0)
        with self.assertRaises(ApiError): self.call('POST', f'/v1/rooms/{code}/join', self.third)
        with self.assertRaises(ApiError): self.call('GET', f'/v1/rooms/{code}/poll', self.third)
        with self.assertRaises(ApiError): self.store.dispatch('POST', '/v1/rooms', 'bad', {}, '127.0.0.1')

    def test_invitation_is_single_use_and_does_not_embed_device_token(self):
        room = self.call('POST', '/v1/rooms')
        self.assertNotIn(self.owner['token'], room['invite'])
        member = self.enroll(invite=room['invite'])
        self.assertEqual(member['room'], room['code'])
        with self.assertRaises(ApiError): self.enroll(invite=room['invite'])

    def test_expired_rooms_and_relay_or_application_messages_are_rejected(self):
        room = self.call('POST', '/v1/rooms'); code = room['code']
        self.call('POST', f'/v1/rooms/{code}/join', self.guest)
        for message in [{'kind':'payload','bytes':'x'}, {'kind':'candidate','candidate':{'candidate':'candidate:1 1 UDP 1 1.1.1.1 5 typ relay'}}]:
            with self.assertRaises(ApiError): self.call('POST', f'/v1/rooms/{code}/messages', data=message)
        self.store.rooms[code]['expires'] = time.time()-1
        with self.assertRaises(ApiError): self.call('POST', f'/v1/rooms/{code}/join', self.guest)

    def test_room_and_byte_limits_do_not_grow_without_bound(self):
        self.call('POST', '/v1/rooms'); room = self.call('POST', '/v1/rooms')
        with self.assertRaises(ApiError): self.call('POST', '/v1/rooms')
        self.store.rooms[room['code']]['bytes'] = MAX_ROOM_BYTES
        with self.assertRaises(ApiError): self.call('POST', f"/v1/rooms/{room['code']}/messages", data={'kind':'retry'})


if __name__ == '__main__': unittest.main()
