#!/bin/sh
set -eu

dotnet /review/bootstrap/Asap.ReviewBootstrap.dll
exec dotnet /app/Asap.Web.dll
