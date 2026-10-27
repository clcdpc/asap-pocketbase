#!/bin/sh
set -eu

export ASPNETCORE_Kestrel__Certificates__Default__Password="$(cat /review/tls/password.txt)"

rm -f /tmp/asap-review-provider-ready
dotnet /review/bootstrap/Asap.ReviewBootstrap.dll
export DOTNET_ADDITIONAL_DEPS=/review/providers/Asap.ReviewProviders.deps.json
exec dotnet /app/Asap.Web.dll
