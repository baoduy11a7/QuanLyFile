# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["InvoiceManager/InvoiceManager.csproj", "InvoiceManager/"]
RUN dotnet restore "InvoiceManager/InvoiceManager.csproj"

COPY InvoiceManager/ InvoiceManager/
WORKDIR "/src/InvoiceManager"
RUN dotnet publish "InvoiceManager.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Cài đặt thư viện fontconfig, fonts cho PDF, Chromium và Node.js để Headless Agent vượt F5 WAF trên Linux
RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 \
    fonts-dejavu-core \
    chromium \
    nodejs \
    npm \
    && rm -rf /var/lib/apt/lists/*

ENV CHROME_PATH=/usr/bin/chromium
ENV PUPPETEER_SKIP_CHROMIUM_DOWNLOAD=true

# Cài đặt puppeteer-core cho Headless Chrome Agent
COPY package.json /app/
RUN cd /app && npm install --omit=dev

COPY tools/ /app/tools/
COPY --from=build /app/publish .

# Cấu hình cổng cho Render / VPS
ENV ASPNETCORE_HTTP_PORTS=8080;10000
EXPOSE 8080
EXPOSE 10000

ENTRYPOINT ["dotnet", "InvoiceManager.dll"]
