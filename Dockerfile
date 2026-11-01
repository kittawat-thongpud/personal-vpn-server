# syntax=docker/dockerfile:1
# Only the fetch stages contain a shell or package manager. The runtime images
# contain a static upstream binary, a CA bundle, and numeric-user metadata.
FROM --platform=$BUILDPLATFORM alpine:3.22.2@sha256:4b7ce07002c69e8f3d704a9c5d6fd3053be500b7f1c69fc0d80990c2ad8dd412 AS fetch-tools
RUN apk add --no-cache ca-certificates wget unzip \
    && mkdir -p /out/bin /out/etc /out/hysteria-state \
    && printf 'vpn:x:10001:10001:VPN:/nonexistent:/sbin/nologin\n' > /out/etc/passwd \
    && printf 'vpn:x:10001:\n' > /out/etc/group \
    && chown 10001:10001 /out/hysteria-state \
    && chmod 0700 /out/hysteria-state

# Release artifacts and SHA-256 digests were checked against the official GitHub
# release API. Updates are deliberate edits, never an implicit latest download.
FROM fetch-tools AS fetch-xray
ARG TARGETARCH
RUN set -eu; \
    case "$TARGETARCH" in \
      amd64) asset=Xray-linux-64.zip; digest=8a5f3e6a1057e4bd0f8a7c57c9d91204b4be0b74222a6e210d070603101163c2 ;; \
      arm64) asset=Xray-linux-arm64-v8a.zip; digest=afe01f19045b05237420328028ee1ee2e1edd2100eb41a8cbbf50e683b0a6e58 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    wget -q -O /tmp/xray.zip "https://github.com/XTLS/Xray-core/releases/download/v26.10.29/$asset"; \
    printf '%s  %s\n' "$digest" /tmp/xray.zip | sha256sum -c -; \
    unzip -p /tmp/xray.zip xray > /out/bin/xray; \
    chmod 0755 /out/bin/xray

FROM fetch-tools AS fetch-hysteria
ARG TARGETARCH
RUN set -eu; \
    case "$TARGETARCH" in \
      amd64) asset=hysteria-linux-amd64; digest=907ba8c9693edb104b20582681fb7dc15639d5b64a9cbb616a7b539190a86691 ;; \
      arm64) asset=hysteria-linux-arm64; digest=a68a61a84452ca250ce0368202521965ca9cc9d801a404f1dc9008ac6cf677a7 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    wget -q -O /out/bin/hysteria "https://github.com/HyNetworks/hysteria/releases/download/app/v2.13.0/$asset"; \
    printf '%s  %s\n' "$digest" /out/bin/hysteria | sha256sum -c -; \
    chmod 0755 /out/bin/hysteria

FROM scratch AS runtime
COPY --from=fetch-tools /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
COPY --from=fetch-tools /out/etc/ /etc/
USER 10001:10001
WORKDIR /

FROM runtime AS xray
LABEL org.opencontainers.image.title="Personal VPN - Xray" \
      org.opencontainers.image.version="26.10.29" \
      org.opencontainers.image.source="https://github.com/XTLS/Xray-core"
COPY --from=fetch-xray /out/bin/xray /usr/local/bin/xray
EXPOSE 443/tcp
ENTRYPOINT ["/usr/local/bin/xray"]
CMD ["run", "-config", "/etc/vpn/xray.json"]

FROM runtime AS hysteria
LABEL org.opencontainers.image.title="Personal VPN - Hysteria 2" \
      org.opencontainers.image.version="2.13.0" \
      org.opencontainers.image.source="https://github.com/HyNetworks/hysteria"
COPY --from=fetch-hysteria /out/bin/hysteria /usr/local/bin/hysteria
# Docker initializes a fresh named volume from this owned image directory.
# Keep this path consistent with acme.dir in the generated configuration.
COPY --from=fetch-tools --chown=10001:10001 /out/hysteria-state/ /var/lib/hysteria/
EXPOSE 443/udp 80/tcp
ENTRYPOINT ["/usr/local/bin/hysteria"]
CMD ["server", "-c", "/etc/vpn/hysteria.json"]
