#!/bin/sh

cp /app/configs/appsettings.Production.json /app/

dotnet DotNetCoreSqlDb.dll
