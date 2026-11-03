"""Checked backports against the exact pinned sing-box source revision."""
from pathlib import Path
root = Path('.')
p = root / 'option/tls.go'
s = p.read_text()
needle = '\n\tCertificatePublicKeySHA256 badoption.Listable[[]byte]'
assert needle in s
s = s.replace(needle, '\n\tCertificateSHA256 badoption.Listable[[]byte] `json:"certificate_sha256,omitempty"`'+needle, 1)
p.write_text(s)

# Xray >= 26.9.8 authenticates only modern ClientHellos offering the hybrid
# share. Keep the browser's original X25519MLKEM768 extensions and derive
# REALITY authentication from its X25519 component, as Xray's client does.
# Upstream issue: https://github.com/SagerNet/sing-box/issues/4520
p = root / 'common/tls/reality_client.go'
s = p.read_text()
start = s.index('\tfor _, extension := range uConn.Extensions {\n\t\tif ce, ok := extension.(*utls.SupportedCurvesExtension); ok {')
end_marker = '\terr = uConn.BuildHandshakeState()\n\tif err != nil {\n\t\treturn nil, err\n\t}\n'
end = s.index(end_marker, start) + len(end_marker)
removed = s[start:end]
assert removed.count('utls.X25519MLKEM768') == 2
assert '(*utls.KeyShareExtension)' in removed
s = s[:start] + s[end:]
s = s.replace('\n\t"github.com/sagernet/sing/common"', '', 1)
needle = '\tecdheKey := keyShareKeys.Ecdhe\n\tif ecdheKey == nil {'
assert s.count(needle) == 1
s = s.replace(needle, '\tecdheKey := keyShareKeys.Ecdhe\n\tif ecdheKey == nil {\n\t\tecdheKey = keyShareKeys.MlkemEcdhe\n\t}\n\tif ecdheKey == nil {', 1)
p.write_text(s)
p = root / 'common/tls/std_client.go'
s = p.read_text()
needle = '\tif len(options.CertificatePublicKeySHA256) > 0 {'
assert needle in s
s = s.replace(needle, '''\tif len(options.CertificateSHA256) > 0 {
        if len(options.CertificatePublicKeySHA256) > 0 { return nil, E.New("choose one pin type") }
        tlsConfig.InsecureSkipVerify = true
        tlsConfig.VerifyPeerCertificate = func(rawCerts [][]byte, verifiedChains [][]*x509.Certificate) error {
            if len(rawCerts) == 0 { return E.New("certificate missing") }
            digest := sha256.Sum256(rawCerts[0])
            for _, expected := range options.CertificateSHA256 {
                if subtle.ConstantTimeCompare(digest[:], expected) == 1 { return nil }
            }
            return E.New("certificate pin mismatch")
        }
    }
''' + needle, 1)
# sha256 is already imported for public-key pins. Constant-time comparison is new.
assert '"crypto/sha256"' in s
s = s.replace('"crypto/sha256"', '"crypto/sha256"\n "crypto/subtle"', 1)
p.write_text(s)
