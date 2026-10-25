FROM node:24-alpine
ENV NODE_ENV=production PORT=8080 HOST=0.0.0.0
WORKDIR /app
COPY package.json *.mjs *.json ./
RUN npm ci --omit=dev
COPY public ./public
RUN mkdir -p /app/data && chown node:node /app/data
VOLUME ["/app/data"]
USER node
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s CMD node -e "fetch('http://127.0.0.1:8080/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
CMD ["node", "server.mjs"]

