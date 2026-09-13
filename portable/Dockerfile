FROM debian:trixie-slim@sha256:d7e12182ce18b85b93007c1dedf31f2d29e01ccf3182cc4017c709b6259bc132

ENV DEBIAN_FRONTEND=noninteractive \
    PATH=/opt/venv/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

RUN apt-get update && apt-get install -y --no-install-recommends \
      ca-certificates python3 python3-venv \
      tigervnc-standalone-server tigervnc-tools xauth xdotool \
      openbox xterm xfonts-base fonts-dejavu-core \
      chromium chromium-sandbox libxcb1 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 deskweave \
    && useradd --uid 10001 --gid 10001 --create-home --shell /bin/sh deskweave \
    && mkdir /data && chown 10001:10001 /data

WORKDIR /app
COPY requirements.txt /app/requirements.txt
RUN python3 -m venv /opt/venv \
    && /opt/venv/bin/pip install --no-cache-dir -r /app/requirements.txt

COPY deskweave/ /app/deskweave/
COPY web/ /app/web/
COPY tests/ /app/tests/
USER 10001:10001
EXPOSE 8765
ENTRYPOINT ["python", "-m", "deskweave"]
CMD ["serve", "--backend", "desktop", "--port", "8765", "--container-bind", "--data-dir", "/data"]
