#!/bin/sh
set -eu

rm -f /tmp/asap-review-provider-ready
dotnet /review/bootstrap/Asap.ReviewBootstrap.dll
export DOTNET_ADDITIONAL_DEPS=/review/providers/Asap.ReviewProviders.deps.json
exec dotnet /app/Asap.Web.dll
