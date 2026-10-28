# Build from the repository root: docker build -f Aegis.Studio.PublicSite/Dockerfile .
FROM node:24-bookworm-slim
ENV NODE_ENV=production PORT=8080 HOST=0.0.0.0 \
    WORKDAY_PHOTO_PROBE_PATH=/app/Media/photo_probe.py \
    WORKDAY_RAW_PREVIEW_HELPER_PATH=/app/Media/raw_preview.py \
    WORKDAY_RAW_PYTHON=/opt/raw-preview/bin/python
WORKDIR /app
COPY Aegis.Studio.PublicSite/package.json Aegis.Studio.PublicSite/package-lock.json ./
RUN npm ci --omit=dev
COPY Aegis.Studio.Web/Media/raw-preview-requirements.txt /app/Media/
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-venv \
    && python3 -m venv /opt/raw-preview \
    && /opt/raw-preview/bin/pip install --no-cache-dir -r /app/Media/raw-preview-requirements.txt \
    && rm -rf /var/lib/apt/lists/*
COPY Aegis.Studio.PublicSite/*.mjs Aegis.Studio.PublicSite/*.json ./
COPY Aegis.Studio.PublicSite/public ./public
COPY Aegis.Studio.Web/Media/photo_probe.py Aegis.Studio.Web/Media/raw_preview.py /app/Media/
RUN mkdir -p /app/data && chown node:node /app/data
VOLUME ["/app/data"]
USER node
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s CMD node -e "fetch('http://127.0.0.1:8080/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
CMD ["node", "server.mjs"]
