# syntax=docker/dockerfile:1.7
# Must match package-lock.json. Browser binaries are provided by this image.
FROM mcr.microsoft.com/playwright:v1.62.1-noble
ARG REVIEW_TOOLING_SHA=unknown
ARG REVIEW_TOOLING_DIRTY=false
ARG REVIEW_TOOLING_VERSION=unknown
LABEL org.opencontainers.image.revision="${REVIEW_TOOLING_SHA}" \
      org.opencontainers.image.version="${REVIEW_TOOLING_VERSION}"
USER root
RUN apt-get update && apt-get install -y --no-install-recommends \
    xvfb x11vnc novnc websockify libnss3-tools fluxbox \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /runner
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
ENV HOME=/home/pwuser
COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts --no-audit --no-fund \
    && node -e "if(require('playwright/package.json').version !== '1.62.1') process.exit(1)"
COPY tests/acceptance ./tests/acceptance
COPY tests/browser ./tests/browser
COPY dev/review/docker/acceptance-entrypoint.sh dev/review/docker/localhost-forwarder.cjs ./
RUN printf '{"commit":"%s","dirty":%s}\n' "$REVIEW_TOOLING_SHA" "$REVIEW_TOOLING_DIRTY" > /runner/tooling.json \
    && sed -i 's/\r$//' /runner/acceptance-entrypoint.sh \
    && chmod 755 /runner/acceptance-entrypoint.sh \
    && mkdir -p /artifacts /home/pwuser/.pki/nssdb \
    && chown -R pwuser:pwuser /artifacts /home/pwuser
USER pwuser
ENTRYPOINT ["/runner/acceptance-entrypoint.sh"]
