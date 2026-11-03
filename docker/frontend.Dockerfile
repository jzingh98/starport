# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM node:22-alpine AS build
WORKDIR /src
COPY spa/starport/package.json spa/starport/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci --no-audit --no-fund
COPY spa/starport/ ./
RUN npm run build -- --configuration=production

FROM nginxinc/nginx-unprivileged:1.31.7-alpine AS runtime
RUN rm -f /etc/nginx/conf.d/default.conf
ARG VERSION=0.0.0-dev
ARG REVISION=""
LABEL org.opencontainers.image.title="starport-frontend" \
      org.opencontainers.image.source="https://github.com/jzingh98/starport" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
COPY --link docker/nginx/templates/ /etc/nginx/templates/
COPY --link docker/nginx/snippets/ /etc/nginx/snippets/
COPY --link --from=build /src/dist/starport/browser/ /usr/share/nginx/html/
ENV API_UPSTREAM="" \
    CSP_CONNECT_SRC="" \
    CSP_FRAME_SRC="" \
    CSP_SCRIPT_SRC="" \
    NGINX_ENTRYPOINT_LOCAL_RESOLVERS="1" \
    NGINX_ENVSUBST_FILTER="^(API_UPSTREAM|CSP_[A-Z_]+|NGINX_LOCAL_RESOLVERS)$"
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD ["wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8080/health"]
