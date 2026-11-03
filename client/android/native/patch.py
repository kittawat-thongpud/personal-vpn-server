"""Small, checked backport of full-leaf certificate pinning for Hysteria exports."""
from pathlib import Path
root = Path('.')
p = root / 'option/tls.go'
s = p.read_text()
needle = '\n\tCertificatePublicKeySHA256 badoption.Listable[[]byte]'
assert needle in s
s = s.replace(needle, '\n\tCertificateSHA256 badoption.Listable[[]byte] `json:"certificate_sha256,omitempty"`'+needle, 1)
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
