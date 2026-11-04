FROM python:3.13-alpine
RUN apk add --no-cache openssl && addgroup -g 10001 p2p && adduser -D -u 10001 -G p2p p2p
WORKDIR /app
COPY server.py /app/server.py
USER 10001:10001
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "/app/server.py"]
CMD ["serve"]
