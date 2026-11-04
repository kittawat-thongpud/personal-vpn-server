FROM python:3.13-alpine@sha256:7984cf56ceaddebe4fefeb75d64000b9f45d8b77b11a09b24524da6cdc3af0c8
RUN apk add --no-cache openssl && addgroup -g 10001 p2p && adduser -D -u 10001 -G p2p p2p
WORKDIR /app
LABEL org.opencontainers.image.source="https://github.com/kittawat-thongpud/personal-vpn-server" \
      org.opencontainers.image.description="Private P2P Lab signaling only; no data relay or device credentials in the image"
COPY server.py /app/server.py
USER 10001:10001
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "/app/server.py"]
CMD ["serve"]
