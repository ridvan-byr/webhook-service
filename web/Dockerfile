# The admin panel. Build from the web/ directory:
#
#   docker build -t webhook-service-web web
#
# docker-compose.yml does this for you.

FROM node:24-slim AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

FROM node:24-slim AS build
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
# Nothing secret is needed to build: API_BASE_URL and SESSION_SECRET are read when the server runs.
RUN npm run build

# The build writes a small self-contained server (next.config.ts: output "standalone").
FROM node:24-slim AS run
WORKDIR /app
ENV NODE_ENV=production \
    PORT=3000 \
    HOSTNAME=0.0.0.0 \
    NEXT_TELEMETRY_DISABLED=1
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
USER node
EXPOSE 3000
CMD ["node", "server.js"]
