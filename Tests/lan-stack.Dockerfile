FROM opencredential-adminweb:healthcheck-validation AS app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends postgresql nginx openssl curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=app /app/ /app/
COPY lan-proxy.conf /etc/nginx/conf.d/default.conf
COPY lan-stack-smoke.sh /test/lan-stack-smoke.sh
RUN bash /test/lan-stack-smoke.sh
